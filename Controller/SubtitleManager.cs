using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    public class SubtitleManager
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SubtitleManager> _logger;

        public SubtitleManager(ILibraryManager libraryManager, ILogger<SubtitleManager> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        public async Task GenerateSubtitleAsync(BaseItem item, ISubtitleProvider provider, string language, CancellationToken cancellationToken)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            // Route audio items to lyrics generation
            if (item is MediaBrowser.Controller.Entities.Audio.Audio)
            {
                await GenerateLyricsAsync(item, provider, language, cancellationToken);
                return;
            }

            var mediaPath = item.Path;
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                _logger.LogWarning("Media file not found for item {ItemName}", item.Name);
                return;
            }

            var languages = await ResolveLanguagesAsync(mediaPath, language, cancellationToken);
            var subtitleMode = Plugin.Instance?.Configuration?.SubtitleMode ?? SubtitleMode.Full;

            foreach (var lang in languages)
            {
                if (subtitleMode == SubtitleMode.Full || subtitleMode == SubtitleMode.FullAndForced)
                {
                    await GenerateFullSubtitleForLanguageAsync(item, provider, lang, mediaPath, cancellationToken);
                }

                if (subtitleMode == SubtitleMode.ForcedOnly || subtitleMode == SubtitleMode.FullAndForced)
                {
                    await GenerateForcedSubtitleAsync(item, provider, lang, mediaPath, cancellationToken);
                }
            }

            await item.RefreshMetadata(cancellationToken);
        }

        /// <summary>
        /// Generates a full (complete) subtitle file for a single language. Existing v2.5 behavior.
        /// </summary>
        private async Task GenerateFullSubtitleForLanguageAsync(
            BaseItem item, ISubtitleProvider provider, string lang,
            string mediaPath, CancellationToken cancellationToken)
        {
            var srtPath = Path.ChangeExtension(mediaPath, $".{lang}.generated.srt");
            string existingSrt = "";
            double resumeOffsetSeconds = 0;
            int existingEntryCount = 0;

            if (File.Exists(srtPath))
            {
                existingSrt = await File.ReadAllTextAsync(srtPath, cancellationToken);
                var lastTimestamp = WhisperProvider.ParseLastSrtTimestamp(existingSrt);
                var mediaDuration = await GetMediaDurationAsync(mediaPath, cancellationToken);

                if (mediaDuration > 0 && lastTimestamp >= mediaDuration - 30)
                {
                    _logger.LogInformation("Subtitle already complete for {ItemName} [{Language}] ({Last:F0}s / {Duration:F0}s), skipping",
                        item.Name, lang, lastTimestamp, mediaDuration);
                    return;
                }

                if (lastTimestamp > 0)
                {
                    resumeOffsetSeconds = Math.Max(0, lastTimestamp - 2);
                    existingEntryCount = WhisperProvider.CountSrtEntries(existingSrt);
                    _logger.LogInformation("Resuming subtitle for {ItemName} [{Language}] from {Offset:F1}s ({Entries} existing entries)",
                        item.Name, lang, resumeOffsetSeconds, existingEntryCount);
                }
                else if (mediaDuration <= 0)
                {
                    _logger.LogInformation("Subtitle exists for {ItemName} [{Language}] (can't verify completeness), skipping", item.Name, lang);
                    return;
                }
            }

            var tempAudioPath = Path.Combine(Path.GetTempPath(), $"{item.Id}_{Guid.NewGuid()}.wav");
            _logger.LogInformation("Generating full subtitle for {ItemName} [{Language}]", item.Name, lang);

            try
            {
                await ExtractAudioAsync(mediaPath, tempAudioPath, lang, cancellationToken, resumeOffsetSeconds);
                string srtContent = await provider.TranscribeAsync(tempAudioPath, lang, cancellationToken);

                if (resumeOffsetSeconds > 0 && !string.IsNullOrWhiteSpace(existingSrt))
                {
                    var offsetContent = WhisperProvider.OffsetSrt(srtContent, resumeOffsetSeconds, existingEntryCount + 1);
                    srtContent = existingSrt.TrimEnd() + "\n\n" + offsetContent;
                }

                await File.WriteAllTextAsync(srtPath, srtContent, CancellationToken.None);
                _logger.LogInformation("Saved full subtitle to {SrtPath}", srtPath);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cancelled full subtitle generation for {ItemName} [{Language}]", item.Name, lang);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating full subtitle for {ItemName} [{Language}], continuing with next language", item.Name, lang);
            }
            finally
            {
                if (File.Exists(tempAudioPath))
                {
                    try { File.Delete(tempAudioPath); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp audio: {Path}", tempAudioPath); }
                }
            }
        }

        /// <summary>
        /// Generates a forced subtitle file containing only foreign-language segments.
        /// Uses VAD-based chunking, per-chunk language detection, and selective transcription.
        /// Output: Movie.{lang}.forced.generated.srt
        /// </summary>
        private async Task GenerateForcedSubtitleAsync(
            BaseItem item, ISubtitleProvider provider, string primaryLanguage,
            string mediaPath, CancellationToken cancellationToken)
        {
            // Resolve actual primary language if "auto"
            string resolvedPrimary = primaryLanguage;
            if (string.Equals(primaryLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var detected = await DetectAudioLanguagesAsync(mediaPath, cancellationToken);
                if (detected.Count > 0)
                {
                    resolvedPrimary = detected[0];
                    _logger.LogInformation("Resolved primary language for forced subs via ffprobe: {Language}", resolvedPrimary);
                }
                else
                {
                    // Fallback: extract first 30s of audio and let whisper detect the language
                    _logger.LogInformation("No audio language tags for {ItemName}, using whisper to detect primary language", item.Name);
                    var probeDir = Path.Combine(Path.GetTempPath(), $"whispersubs_probe_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(probeDir);
                    try
                    {
                        var probeAudio = Path.Combine(probeDir, "probe.wav");
                        await ExtractAudioAsync(mediaPath, probeAudio, null, cancellationToken);
                        // Take only the first 30s for detection
                        var probeChunk = Path.Combine(probeDir, "probe_chunk.wav");
                        await ExtractAudioChunkAsync(probeAudio, probeChunk, 0, 30.0, cancellationToken);
                        var (detectedLang, _) = await provider.DetectLanguageAsync(probeChunk, cancellationToken);
                        resolvedPrimary = detectedLang;
                        _logger.LogInformation("Resolved primary language for forced subs via whisper: {Language}", resolvedPrimary);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Cannot determine primary language for forced subtitles of {ItemName} — " +
                            "tag your audio streams or set a specific language in config", item.Name);
                        return;
                    }
                    finally
                    {
                        try { if (Directory.Exists(probeDir)) Directory.Delete(probeDir, true); } catch { }
                    }
                }
            }

            var forcedSrtPath = Path.ChangeExtension(mediaPath, $".{resolvedPrimary}.forced.generated.srt");
            var noForeignMarkerPath = Path.ChangeExtension(mediaPath, $".{resolvedPrimary}.forced.noforeignlang");

            // Skip if forced SRT already exists with content
            if (File.Exists(forcedSrtPath))
            {
                var existing = await File.ReadAllTextAsync(forcedSrtPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    _logger.LogInformation("Forced subtitle already exists for {ItemName} [{Language}], skipping",
                        item.Name, resolvedPrimary);
                    return;
                }
            }

            // Skip if previously analyzed and found no foreign dialogue
            if (File.Exists(noForeignMarkerPath))
            {
                _logger.LogInformation("No-foreign-language marker exists for {ItemName} [{Language}], skipping",
                    item.Name, resolvedPrimary);
                return;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"whispersubs_{item.Id:N}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var fullAudioPath = Path.Combine(tempDir, "full.wav");

            try
            {
                _logger.LogInformation("Generating forced subtitle for {ItemName} [{Language}]", item.Name, resolvedPrimary);

                // Step 1: Extract full audio
                await ExtractAudioAsync(mediaPath, fullAudioPath, resolvedPrimary, cancellationToken);

                // Step 2: Get duration
                var totalDuration = await GetMediaDurationAsync(fullAudioPath, cancellationToken);
                if (totalDuration <= 0)
                {
                    totalDuration = await GetMediaDurationAsync(mediaPath, cancellationToken);
                }
                if (totalDuration <= 0)
                {
                    _logger.LogWarning("Cannot determine duration for {ItemName}, aborting forced subtitle", item.Name);
                    return;
                }

                // Step 3: VAD-based speech segmentation via silencedetect
                var speechSegments = await DetectSpeechSegmentsAsync(fullAudioPath, totalDuration, cancellationToken);

                if (speechSegments.Count == 0)
                {
                    _logger.LogInformation("No speech segments detected via VAD for {ItemName}, falling back to fixed chunks", item.Name);
                    speechSegments = GenerateFixedChunks(totalDuration, 30.0);
                }

                // Step 4: Group speech into ~30s chunks
                var chunks = GroupSpeechIntoChunks(speechSegments, 30.0);
                _logger.LogInformation("Analyzing {Count} audio chunks for foreign language in {ItemName}", chunks.Count, item.Name);

                // Step 5: Language detection per chunk
                var foreignChunks = new List<(double Start, double End, string Language)>();
                int successfulDetections = 0;

                for (int i = 0; i < chunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = chunks[i];
                    var chunkDuration = chunk.End - chunk.Start;

                    // Skip very short chunks (< 1s) — unreliable detection
                    if (chunkDuration < 1.0) continue;

                    var chunkPath = Path.Combine(tempDir, $"chunk_{i:D4}.wav");

                    try
                    {
                        await ExtractAudioChunkAsync(fullAudioPath, chunkPath, chunk.Start, chunkDuration, cancellationToken);
                        var (detectedLang, probability) = await provider.DetectLanguageAsync(chunkPath, cancellationToken);
                        successfulDetections++;

                        _logger.LogDebug("Chunk {Index}/{Total}: {Start:F1}s-{End:F1}s → {Language} (p={Prob:F3})",
                            i + 1, chunks.Count, chunk.Start, chunk.End, detectedLang, probability);

                        if (!string.Equals(detectedLang, resolvedPrimary, StringComparison.OrdinalIgnoreCase)
                            && probability >= 0.3f)
                        {
                            foreignChunks.Add((chunk.Start, chunk.End, detectedLang));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Language detection failed for chunk {Index} ({Start:F1}s-{End:F1}s), skipping",
                            i, chunk.Start, chunk.End);
                    }
                }

                if (foreignChunks.Count == 0 && successfulDetections == 0)
                {
                    _logger.LogWarning("All {Count} language detection attempts failed for {ItemName} — not writing marker (will retry next run)",
                        chunks.Count, item.Name);
                    return;
                }

                if (foreignChunks.Count == 0)
                {
                    // Write marker (not .srt) so the task won't reprocess but Jellyfin won't show an empty track
                    await File.WriteAllTextAsync(noForeignMarkerPath, "", CancellationToken.None);
                    _logger.LogInformation("No foreign language segments found in {ItemName} ({Checked} chunks checked), wrote no-foreign marker",
                        item.Name, successfulDetections);
                    return;
                }

                _logger.LogInformation("Found {Count} foreign language chunk(s) in {ItemName}, transcribing",
                    foreignChunks.Count, item.Name);

                // Step 6: Merge adjacent foreign chunks with same language
                var mergedSegments = MergeForeignChunks(foreignChunks);

                // Step 7: Transcribe foreign segments
                var forcedSrt = new StringBuilder();
                int entryNum = 1;

                foreach (var segment in mergedSegments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segDuration = segment.End - segment.Start;
                    var segmentPath = Path.Combine(tempDir, $"foreign_{segment.Start:F0}_{segment.End:F0}.wav");

                    try
                    {
                        await ExtractAudioChunkAsync(fullAudioPath, segmentPath, segment.Start, segDuration, cancellationToken);
                        var srtContent = await provider.TranscribeAsync(segmentPath, segment.Language, cancellationToken);

                        if (!string.IsNullOrWhiteSpace(srtContent))
                        {
                            var offsetContent = WhisperProvider.OffsetSrt(srtContent, segment.Start, entryNum);
                            forcedSrt.Append(offsetContent);
                            entryNum += WhisperProvider.CountSrtEntries(srtContent);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to transcribe foreign segment {Start:F1}s-{End:F1}s [{Language}]",
                            segment.Start, segment.End, segment.Language);
                    }
                }

                // Step 8: Save forced SRT
                if (forcedSrt.Length > 0)
                {
                    await File.WriteAllTextAsync(forcedSrtPath, forcedSrt.ToString(), CancellationToken.None);
                    _logger.LogInformation("Saved forced subtitle to {Path} ({Entries} entries)",
                        forcedSrtPath, entryNum - 1);
                }
                else
                {
                    _logger.LogInformation("Foreign segments detected but no content transcribed for {ItemName}", item.Name);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cancelled forced subtitle generation for {ItemName}", item.Name);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating forced subtitle for {ItemName}", item.Name);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temp directory: {Path}", tempDir);
                }
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Lyrics (LRC) generation for Audio items
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates LRC lyrics for an audio item by transcribing with whisper
        /// and converting the SRT output to LRC format.
        /// </summary>
        private async Task GenerateLyricsAsync(BaseItem item, ISubtitleProvider provider, string language, CancellationToken cancellationToken)
        {
            var mediaPath = item.Path;
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                _logger.LogWarning("Media file not found for item {ItemName}", item.Name);
                return;
            }

            // Resolve transcription language (use first detected or configured).
            // Jellyfin expects a single track.lrc sidecar, not per-language files.
            var languages = await ResolveLanguagesAsync(mediaPath, language, cancellationToken);
            var transcriptionLang = languages.FirstOrDefault() ?? "auto";

            await GenerateLyricsForTrackAsync(item, provider, transcriptionLang, mediaPath, cancellationToken);

            await item.RefreshMetadata(cancellationToken);
        }

        private async Task GenerateLyricsForTrackAsync(
            BaseItem item, ISubtitleProvider provider, string lang,
            string mediaPath, CancellationToken cancellationToken)
        {
            var baseName = Path.GetFileNameWithoutExtension(mediaPath);
            var dir = Path.GetDirectoryName(mediaPath)!;
            // Jellyfin's LyricResolver expects track.lrc (matching the audio filename)
            var lrcPath = Path.Combine(dir, $"{baseName}.lrc");

            if (File.Exists(lrcPath))
            {
                _logger.LogInformation("Lyrics already exist for {ItemName}, skipping", item.Name);
                return;
            }

            var tempAudioPath = Path.Combine(Path.GetTempPath(), $"{item.Id}_{Guid.NewGuid()}.wav");
            _logger.LogInformation("Generating lyrics for {ItemName} [{Language}]", item.Name, lang);

            try
            {
                await ExtractAudioAsync(mediaPath, tempAudioPath, lang, cancellationToken);
                string srtContent = await provider.TranscribeAsync(tempAudioPath, lang, cancellationToken);
                string lrcContent = ConvertSrtToLrc(srtContent, item.Name);

                await File.WriteAllTextAsync(lrcPath, lrcContent, CancellationToken.None);
                _logger.LogInformation("Saved lyrics to {LrcPath}", lrcPath);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cancelled lyrics generation for {ItemName} [{Language}]", item.Name, lang);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating lyrics for {ItemName} [{Language}]", item.Name, lang);
            }
            finally
            {
                if (File.Exists(tempAudioPath))
                {
                    try { File.Delete(tempAudioPath); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete temp audio: {Path}", tempAudioPath); }
                }
            }
        }

        /// <summary>
        /// Converts SRT subtitle content to LRC lyrics format.
        /// LRC uses [MM:SS.cc] timestamps (start only, no end timestamps).
        /// </summary>
        internal static string ConvertSrtToLrc(string srtContent, string? title = null)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(title))
                sb.AppendLine($"[ti:{title}]");
            sb.AppendLine("[by:WhisperSubs]");
            sb.AppendLine();

            var entries = Regex.Split(srtContent.Trim(), @"\r?\n\r?\n");
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;

                var lines = entry.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 3) continue;

                // Line 0: sequence number
                // Line 1: timestamp (00:01:23,456 --> 00:01:25,789)
                // Line 2+: text
                var timestampMatch = Regex.Match(lines[1], @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})");
                if (!timestampMatch.Success) continue;

                int hours = int.Parse(timestampMatch.Groups[1].Value);
                int minutes = int.Parse(timestampMatch.Groups[2].Value);
                int seconds = int.Parse(timestampMatch.Groups[3].Value);
                int millis = int.Parse(timestampMatch.Groups[4].Value);

                int totalMinutes = hours * 60 + minutes;
                int centiseconds = millis / 10;

                var text = string.Join(" ", lines.Skip(2)).Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    sb.AppendLine($"[{totalMinutes:D2}:{seconds:D2}.{centiseconds:D2}]{text}");
                }
            }

            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────
        //  VAD / Chunking helpers
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Uses FFmpeg silencedetect to find speech segments in an audio file.
        /// Returns a list of (start, end) time ranges where speech is present.
        /// </summary>
        private async Task<List<(double Start, double End)>> DetectSpeechSegmentsAsync(
            string audioPath, double totalDuration, CancellationToken cancellationToken)
        {
            var ffmpegPath = FindFfmpegExecutable();
            if (ffmpegPath == null)
            {
                _logger.LogWarning("FFmpeg not found, cannot run VAD");
                return new List<(double, double)>();
            }

            // silencedetect: noise threshold -30dB, minimum silence duration 0.5s
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("-af");
            startInfo.ArgumentList.Add("silencedetect=noise=-30dB:d=0.5");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add("-");
            var process = new Process { StartInfo = startInfo };

            var errorBuilder = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            var output = errorBuilder.ToString();

            // Parse silence intervals from ffmpeg stderr
            var silenceIntervals = new List<(double Start, double End)>();
            double? currentSilenceStart = null;

            foreach (var line in output.Split('\n'))
            {
                var startMatch = Regex.Match(line, @"silence_start:\s*([\d.]+)");
                if (startMatch.Success)
                {
                    currentSilenceStart = double.Parse(startMatch.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }

                var endMatch = Regex.Match(line, @"silence_end:\s*([\d.]+)");
                if (endMatch.Success && currentSilenceStart.HasValue)
                {
                    var silenceEnd = double.Parse(endMatch.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    silenceIntervals.Add((currentSilenceStart.Value, silenceEnd));
                    currentSilenceStart = null;
                }
            }

            // Handle trailing silence (silence_start without matching silence_end)
            if (currentSilenceStart.HasValue)
            {
                silenceIntervals.Add((currentSilenceStart.Value, totalDuration));
            }

            // Invert silence intervals to get speech segments
            var speechSegments = new List<(double Start, double End)>();
            double lastEnd = 0;

            foreach (var silence in silenceIntervals)
            {
                if (silence.Start > lastEnd + 0.1) // Min 100ms speech segment
                {
                    speechSegments.Add((lastEnd, silence.Start));
                }
                lastEnd = silence.End;
            }

            if (lastEnd < totalDuration - 0.1)
            {
                speechSegments.Add((lastEnd, totalDuration));
            }

            // If no silence was detected, treat the entire audio as one speech segment
            if (silenceIntervals.Count == 0 && totalDuration > 0)
            {
                speechSegments.Add((0, totalDuration));
            }

            _logger.LogInformation("VAD: {SilenceCount} silence intervals → {SpeechCount} speech segments in {Duration:F0}s audio",
                silenceIntervals.Count, speechSegments.Count, totalDuration);

            return speechSegments;
        }

        /// <summary>
        /// Groups speech segments into chunks of approximately targetDuration seconds,
        /// splitting only at silence boundaries (between speech segments).
        /// </summary>
        private static List<(double Start, double End)> GroupSpeechIntoChunks(
            List<(double Start, double End)> speechSegments, double targetDuration = 30.0)
        {
            var chunks = new List<(double Start, double End)>();
            if (speechSegments.Count == 0) return chunks;

            double chunkStart = speechSegments[0].Start;
            double chunkEnd = speechSegments[0].End;

            for (int i = 1; i < speechSegments.Count; i++)
            {
                var segment = speechSegments[i];

                if (segment.End - chunkStart <= targetDuration)
                {
                    // Extend current chunk
                    chunkEnd = segment.End;
                }
                else
                {
                    // Finalize current chunk
                    chunks.Add((chunkStart, chunkEnd));
                    chunkStart = segment.Start;
                    chunkEnd = segment.End;
                }
            }

            // Don't forget the last chunk
            chunks.Add((chunkStart, chunkEnd));

            return chunks;
        }

        /// <summary>
        /// Fallback: generate fixed-duration chunks when VAD is unavailable.
        /// </summary>
        private static List<(double Start, double End)> GenerateFixedChunks(double totalDuration, double chunkDuration)
        {
            var chunks = new List<(double Start, double End)>();
            for (double start = 0; start < totalDuration; start += chunkDuration)
            {
                chunks.Add((start, Math.Min(start + chunkDuration, totalDuration)));
            }
            return chunks;
        }

        /// <summary>
        /// Merges consecutive foreign chunks with the same language and small gaps (&lt;5s).
        /// </summary>
        private static List<(double Start, double End, string Language)> MergeForeignChunks(
            List<(double Start, double End, string Language)> chunks)
        {
            if (chunks.Count == 0) return new List<(double, double, string)>();

            var merged = new List<(double Start, double End, string Language)>();
            var current = chunks[0];

            for (int i = 1; i < chunks.Count; i++)
            {
                if (string.Equals(chunks[i].Language, current.Language, StringComparison.OrdinalIgnoreCase)
                    && chunks[i].Start - current.End < 5.0)
                {
                    // Merge: extend current segment
                    current = (current.Start, chunks[i].End, current.Language);
                }
                else
                {
                    merged.Add(current);
                    current = chunks[i];
                }
            }
            merged.Add(current);

            return merged;
        }

        /// <summary>
        /// Extracts an audio chunk from a WAV file using FFmpeg.
        /// </summary>
        private async Task ExtractAudioChunkAsync(
            string sourceAudioPath, string outputPath,
            double startSeconds, double durationSeconds,
            CancellationToken cancellationToken)
        {
            var ffmpegPath = FindFfmpegExecutable();
            if (ffmpegPath == null) throw new InvalidOperationException("FFmpeg not found");

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startSeconds.ToString("F3"));
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(durationSeconds.ToString("F3"));
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(sourceAudioPath);
            startInfo.ArgumentList.Add("-acodec");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("16000");
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add(outputPath);

            var process = new Process { StartInfo = startInfo };

            process.Start();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    $"Failed to extract audio chunk at {startSeconds:F1}s ({durationSeconds:F1}s)");
            }
        }

        // ────────────────────────────────────────────────────────────
        //  Existing helpers (language detection, audio extraction, etc.)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the target language(s) for subtitle generation.
        /// "auto" detects languages from the media's audio streams via FFprobe.
        /// A specific language code (e.g. "es") is returned as-is.
        /// </summary>
        public async Task<List<string>> ResolveLanguagesAsync(string mediaPath, string language, CancellationToken cancellationToken)
        {
            if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string> { language };
            }

            var detected = await DetectAudioLanguagesAsync(mediaPath, cancellationToken);
            if (detected.Count > 0)
            {
                return detected;
            }

            // FFprobe could not determine the language — let whisper auto-detect
            _logger.LogInformation("No audio language tags found in {Path}, falling back to whisper auto-detection", mediaPath);
            return new List<string> { "auto" };
        }

        /// <summary>
        /// Uses FFprobe to extract audio stream language tags from a media file.
        /// Returns distinct ISO 639-1 language codes (e.g. "es", "en").
        /// </summary>
        public async Task<List<string>> DetectAudioLanguagesAsync(string mediaPath, CancellationToken cancellationToken)
        {
            var ffprobePath = FindFfprobeExecutable();
            if (ffprobePath == null)
            {
                _logger.LogWarning("FFprobe not found, cannot detect audio languages");
                return new List<string>();
            }

            var probeInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            probeInfo.ArgumentList.Add("-v");
            probeInfo.ArgumentList.Add("quiet");
            probeInfo.ArgumentList.Add("-print_format");
            probeInfo.ArgumentList.Add("json");
            probeInfo.ArgumentList.Add("-show_streams");
            probeInfo.ArgumentList.Add("-select_streams");
            probeInfo.ArgumentList.Add("a");
            probeInfo.ArgumentList.Add(mediaPath);

            var process = new Process { StartInfo = probeInfo };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("FFprobe exited with code {Code} for {Path}", process.ExitCode, mediaPath);
                return new List<string>();
            }

            var languages = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(outputBuilder.ToString());
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.TryGetProperty("tags", out var tags) &&
                            tags.TryGetProperty("language", out var langProp))
                        {
                            var lang = langProp.GetString();
                            if (!string.IsNullOrEmpty(lang) && lang != "und")
                            {
                                var normalized = NormalizeLanguageCode(lang);
                                if (!languages.Contains(normalized))
                                {
                                    languages.Add(normalized);
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse FFprobe output for {Path}", mediaPath);
            }

            _logger.LogInformation("Detected audio languages for {Path}: [{Languages}]", mediaPath, string.Join(", ", languages));
            return languages;
        }

        private async Task<int> FindAudioStreamIndexAsync(string mediaPath, string language, CancellationToken cancellationToken)
        {
            var ffprobePath = FindFfprobeExecutable();
            if (ffprobePath == null) return -1;

            var probeInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            probeInfo.ArgumentList.Add("-v");
            probeInfo.ArgumentList.Add("quiet");
            probeInfo.ArgumentList.Add("-print_format");
            probeInfo.ArgumentList.Add("json");
            probeInfo.ArgumentList.Add("-show_streams");
            probeInfo.ArgumentList.Add("-select_streams");
            probeInfo.ArgumentList.Add("a");
            probeInfo.ArgumentList.Add(mediaPath);

            var process = new Process { StartInfo = probeInfo };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0) return -1;

            try
            {
                using var doc = JsonDocument.Parse(outputBuilder.ToString());
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    int audioIndex = 0;
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.TryGetProperty("tags", out var tags) &&
                            tags.TryGetProperty("language", out var langProp))
                        {
                            var lang = langProp.GetString();
                            if (!string.IsNullOrEmpty(lang) &&
                                string.Equals(NormalizeLanguageCode(lang), language, StringComparison.OrdinalIgnoreCase))
                            {
                                return audioIndex;
                            }
                        }
                        audioIndex++;
                    }
                }
            }
            catch (JsonException) { }

            return -1;
        }

        private async Task ExtractAudioAsync(string videoPath, string outputAudioPath, string? targetLanguage, CancellationToken cancellationToken, double startOffsetSeconds = 0)
        {
            var ffmpegPath = FindFfmpegExecutable();
            if (ffmpegPath == null)
            {
                throw new InvalidOperationException(
                    "FFmpeg not found. Ensure ffmpeg is installed and available in PATH or at /usr/lib/jellyfin-ffmpeg/ffmpeg");
            }

            var extractInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (startOffsetSeconds > 0)
            {
                extractInfo.ArgumentList.Add("-ss");
                extractInfo.ArgumentList.Add(startOffsetSeconds.ToString("F1"));
            }

            extractInfo.ArgumentList.Add("-i");
            extractInfo.ArgumentList.Add(videoPath);

            if (!string.IsNullOrEmpty(targetLanguage) && !string.Equals(targetLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var streamIndex = await FindAudioStreamIndexAsync(videoPath, targetLanguage, cancellationToken);
                if (streamIndex >= 0)
                {
                    extractInfo.ArgumentList.Add("-map");
                    extractInfo.ArgumentList.Add($"0:a:{streamIndex}");
                    _logger.LogInformation("Selected audio stream {Index} for language {Language}", streamIndex, targetLanguage);
                }
            }

            extractInfo.ArgumentList.Add("-vn");
            extractInfo.ArgumentList.Add("-acodec");
            extractInfo.ArgumentList.Add("pcm_s16le");
            extractInfo.ArgumentList.Add("-ac");
            extractInfo.ArgumentList.Add("1");
            extractInfo.ArgumentList.Add("-ar");
            extractInfo.ArgumentList.Add("16000");
            extractInfo.ArgumentList.Add("-y");
            extractInfo.ArgumentList.Add(outputAudioPath);

            _logger.LogInformation("Running FFmpeg: {Path} {Arguments}", ffmpegPath,
                string.Join(" ", extractInfo.ArgumentList));

            var process = new Process { StartInfo = extractInfo };

            var errorBuilder = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    _logger.LogDebug("FFmpeg: {Output}", e.Data);
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg failed with exit code {process.ExitCode}. Error: {errorBuilder}");
            }

            if (!File.Exists(outputAudioPath))
            {
                throw new FileNotFoundException($"Audio extraction failed. Output not found: {outputAudioPath}");
            }

            _logger.LogInformation("Extracted audio to {AudioPath}", outputAudioPath);
        }

        private async Task<double> GetMediaDurationAsync(string mediaPath, CancellationToken cancellationToken)
        {
            var ffprobePath = FindFfprobeExecutable();
            if (ffprobePath == null) return 0;

            var durationInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            durationInfo.ArgumentList.Add("-v");
            durationInfo.ArgumentList.Add("quiet");
            durationInfo.ArgumentList.Add("-print_format");
            durationInfo.ArgumentList.Add("json");
            durationInfo.ArgumentList.Add("-show_format");
            durationInfo.ArgumentList.Add(mediaPath);

            var process = new Process { StartInfo = durationInfo };

            var outputBuilder = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0) return 0;

            try
            {
                using var doc = JsonDocument.Parse(outputBuilder.ToString());
                if (doc.RootElement.TryGetProperty("format", out var format) &&
                    format.TryGetProperty("duration", out var durationProp))
                {
                    if (double.TryParse(durationProp.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var duration))
                    {
                        return duration;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse FFprobe duration for {Path}", mediaPath);
            }

            return 0;
        }

        private string? FindFfmpegExecutable()
        {
            return FindExecutable(new[]
            {
                "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                "ffmpeg",
                "/usr/bin/ffmpeg"
            });
        }

        private string? FindFfprobeExecutable()
        {
            return FindExecutable(new[]
            {
                "/usr/lib/jellyfin-ffmpeg/ffprobe",
                "ffprobe",
                "/usr/bin/ffprobe"
            });
        }

        private string? FindExecutable(string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                // For absolute paths, trust File.Exists without probing
                if (Path.IsPathRooted(candidate))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    continue;
                }

                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = candidate,
                            Arguments = "-version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();

                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        continue;
                    }

                    if (process.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Continue to next candidate
                }
            }

            return null;
        }

        /// <summary>
        /// Normalizes ISO 639-2/B or 639-2/T three-letter codes to ISO 639-1 two-letter codes
        /// used by whisper.cpp. Falls through to the original code if no mapping exists.
        /// </summary>
        private static string NormalizeLanguageCode(string code)
        {
            return code.ToLowerInvariant() switch
            {
                "spa" => "es",
                "eng" => "en",
                "fra" or "fre" => "fr",
                "deu" or "ger" => "de",
                "ita" => "it",
                "por" => "pt",
                "rus" => "ru",
                "jpn" => "ja",
                "zho" or "chi" => "zh",
                "kor" => "ko",
                "ara" => "ar",
                "hin" => "hi",
                "pol" => "pl",
                "nld" or "dut" => "nl",
                "tur" => "tr",
                "swe" => "sv",
                "dan" => "da",
                "fin" => "fi",
                "nor" => "no",
                "ces" or "cze" => "cs",
                "ron" or "rum" => "ro",
                "hun" => "hu",
                "ell" or "gre" => "el",
                "heb" => "he",
                "tha" => "th",
                "ukr" => "uk",
                "vie" => "vi",
                "ind" => "id",
                "cat" => "ca",
                "eus" or "baq" => "eu",
                "glg" => "gl",
                _ => code.ToLowerInvariant()
            };
        }
    }
}
