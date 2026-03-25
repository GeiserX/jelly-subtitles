using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Configuration;
using JellySubtitles.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Controller
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

            var mediaPath = item.Path;
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                _logger.LogWarning("Media file not found for item {ItemName}", item.Name);
                return;
            }

            // Resolve "auto" to the actual audio language(s)
            var languages = await ResolveLanguagesAsync(mediaPath, language, cancellationToken);

            foreach (var lang in languages)
            {
                var srtPath = Path.ChangeExtension(mediaPath, $".{lang}.generated.srt");
                string existingSrt = "";
                double resumeOffsetSeconds = 0;
                int existingEntryCount = 0;

                // Check for existing partial SRT to resume from
                if (File.Exists(srtPath))
                {
                    existingSrt = await File.ReadAllTextAsync(srtPath, cancellationToken);
                    var lastTimestamp = WhisperProvider.ParseLastSrtTimestamp(existingSrt);
                    var mediaDuration = await GetMediaDurationAsync(mediaPath, cancellationToken);

                    // Consider complete if within 30 seconds of the end (or if we can't determine duration)
                    if (mediaDuration > 0 && lastTimestamp >= mediaDuration - 30)
                    {
                        _logger.LogInformation("Subtitle already complete for {ItemName} [{Language}] ({Last:F0}s / {Duration:F0}s), skipping",
                            item.Name, lang, lastTimestamp, mediaDuration);
                        continue;
                    }

                    if (lastTimestamp > 0)
                    {
                        // Partial SRT exists — resume from where it left off (with 2s overlap for safety)
                        resumeOffsetSeconds = Math.Max(0, lastTimestamp - 2);
                        existingEntryCount = WhisperProvider.CountSrtEntries(existingSrt);
                        _logger.LogInformation("Resuming subtitle for {ItemName} [{Language}] from {Offset:F1}s ({Entries} existing entries)",
                            item.Name, lang, resumeOffsetSeconds, existingEntryCount);
                    }
                    else if (mediaDuration <= 0)
                    {
                        // Can't determine duration and no timestamps — treat as complete
                        _logger.LogInformation("Subtitle exists for {ItemName} [{Language}] (can't verify completeness), skipping", item.Name, lang);
                        continue;
                    }
                }

                var tempAudioPath = Path.Combine(Path.GetTempPath(), $"{item.Id}_{Guid.NewGuid()}.wav");
                _logger.LogInformation("Generating subtitle for {ItemName} [{Language}]", item.Name, lang);

                try
                {
                    await ExtractAudioAsync(mediaPath, tempAudioPath, cancellationToken, resumeOffsetSeconds);
                    string srtContent;

                    srtContent = await provider.TranscribeAsync(tempAudioPath, lang, cancellationToken);

                    // If resuming, offset new timestamps and merge with existing content
                    if (resumeOffsetSeconds > 0 && !string.IsNullOrWhiteSpace(existingSrt))
                    {
                        var offsetContent = WhisperProvider.OffsetSrt(srtContent, resumeOffsetSeconds, existingEntryCount + 1);
                        srtContent = existingSrt.TrimEnd() + "\n\n" + offsetContent;
                    }

                    await File.WriteAllTextAsync(srtPath, srtContent, CancellationToken.None);
                    _logger.LogInformation("Saved subtitle to {SrtPath}", srtPath);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Cancelled subtitle generation for {ItemName} [{Language}]", item.Name, lang);
                    throw;
                }
                catch (Exception ex)
                {
                    // Log but continue to the next language — don't abort the entire item
                    _logger.LogError(ex, "Error generating subtitle for {ItemName} [{Language}], continuing with next language", item.Name, lang);
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

            await item.RefreshMetadata(cancellationToken);
        }

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

            var arguments = $"-v quiet -print_format json -show_streams -select_streams a \"{mediaPath}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

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
                                // Normalize 3-letter codes to 2-letter where possible
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

        private async Task ExtractAudioAsync(string videoPath, string outputAudioPath, CancellationToken cancellationToken, double startOffsetSeconds = 0)
        {
            var ffmpegPath = FindFfmpegExecutable();
            if (ffmpegPath == null)
            {
                throw new InvalidOperationException(
                    "FFmpeg not found. Ensure ffmpeg is installed and available in PATH or at /usr/lib/jellyfin-ffmpeg/ffmpeg");
            }

            var ssArg = startOffsetSeconds > 0 ? $"-ss {startOffsetSeconds:F1} " : "";
            var arguments = $"{ssArg}-i \"{videoPath}\" -vn -acodec pcm_s16le -ac 1 -ar 16000 -y \"{outputAudioPath}\"";
            _logger.LogInformation("Running FFmpeg: {Path} {Arguments}", ffmpegPath, arguments);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

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

            var arguments = $"-v quiet -print_format json -show_format \"{mediaPath}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

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
                try
                {
                    var process = new Process
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
                    process.WaitForExit(1000);

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
