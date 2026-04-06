using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Providers
{
    public class WhisperProvider : ISubtitleProvider
    {
        private readonly ILogger<WhisperProvider> _logger;
        private readonly string _modelPath;
        private readonly string _binaryPath;
        private readonly int _threadCount;

        public string Name => "Whisper";

        public WhisperProvider(ILogger<WhisperProvider> logger, string modelPath, string binaryPath = "", int threadCount = 0)
        {
            _logger = logger;
            _modelPath = modelPath;
            _binaryPath = binaryPath;
            _threadCount = threadCount;
        }

        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Whisper transcription for {AudioPath} with model {ModelPath}", audioPath, _modelPath);

            if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"Whisper model not found at: {_modelPath}");
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            var tempOutputPrefix = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var tempSrtPath = tempOutputPrefix + ".srt";

            try
            {
                var whisperExecutable = FindWhisperExecutable();
                if (whisperExecutable == null)
                {
                    throw new InvalidOperationException(
                        "Whisper executable not found. Please install whisper.cpp and ensure 'whisper-cli' or 'main' is in PATH.");
                }

                var langPrompt = GetLanguagePrompt(language);

                var startInfo = new ProcessStartInfo
                {
                    FileName = whisperExecutable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.ArgumentList.Add("-m");
                startInfo.ArgumentList.Add(_modelPath);
                startInfo.ArgumentList.Add("-f");
                startInfo.ArgumentList.Add(audioPath);
                startInfo.ArgumentList.Add("-l");
                startInfo.ArgumentList.Add(language);
                if (_threadCount > 0)
                {
                    startInfo.ArgumentList.Add("-t");
                    startInfo.ArgumentList.Add(_threadCount.ToString());
                }
                startInfo.ArgumentList.Add("-mc");
                startInfo.ArgumentList.Add("0");
                startInfo.ArgumentList.Add("-sns");
                startInfo.ArgumentList.Add("-osrt");
                startInfo.ArgumentList.Add("-of");
                startInfo.ArgumentList.Add(tempOutputPrefix);
                if (!string.IsNullOrEmpty(langPrompt))
                {
                    startInfo.ArgumentList.Add("--prompt");
                    startInfo.ArgumentList.Add(langPrompt);
                }

                _logger.LogInformation("Running: {Executable} {Arguments}", whisperExecutable,
                    string.Join(" ", startInfo.ArgumentList));

                var process = new Process { StartInfo = startInfo };

                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogDebug("Whisper output: {Output}", e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        _logger.LogWarning("Whisper stderr: {Error}", e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }

                    // Save whatever partial SRT whisper wrote before being killed
                    if (File.Exists(tempSrtPath))
                    {
                        var partial = await File.ReadAllTextAsync(tempSrtPath);
                        if (!string.IsNullOrWhiteSpace(partial))
                        {
                            _logger.LogInformation("Cancelled — returning partial SRT ({Bytes} bytes)", partial.Length);
                            return partial;
                        }
                    }

                    throw;
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Whisper process failed with exit code {process.ExitCode}. Error: {errorBuilder}");
                }

                if (File.Exists(tempSrtPath))
                {
                    var srtContent = await File.ReadAllTextAsync(tempSrtPath, cancellationToken);
                    _logger.LogInformation("Successfully generated subtitle file");
                    return srtContent;
                }

                var altPath = Path.ChangeExtension(audioPath, ".srt");
                if (File.Exists(altPath))
                {
                    var srtContent = await File.ReadAllTextAsync(altPath, cancellationToken);
                    return srtContent;
                }

                throw new FileNotFoundException($"Subtitle file not found at expected location: {tempSrtPath}");
            }
            finally
            {
                if (File.Exists(tempSrtPath))
                {
                    try { File.Delete(tempSrtPath); }
                    catch { }
                }
            }
        }

        public async Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Detecting language for {AudioPath}", audioPath);

            if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"Whisper model not found at: {_modelPath}");
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            var whisperExecutable = FindWhisperExecutable();
            if (whisperExecutable == null)
            {
                throw new InvalidOperationException(
                    "Whisper executable not found. Please install whisper.cpp and ensure 'whisper-cli' or 'main' is in PATH.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = whisperExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("auto");
            if (_threadCount > 0)
            {
                startInfo.ArgumentList.Add("-t");
                startInfo.ArgumentList.Add(_threadCount.ToString());
            }
            startInfo.ArgumentList.Add("--detect-language");

            _logger.LogDebug("Running: {Executable} {Arguments}", whisperExecutable,
                string.Join(" ", startInfo.ArgumentList));

            var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var allOutput = outputBuilder.ToString() + "\n" + errorBuilder.ToString();

            // Parse language from whisper output. Handles multiple formats:
            // "Detected language: en" (--detect-language mode)
            // "whisper_full_with_state: auto-detected language: en (p = 0.978)"
            var match = Regex.Match(allOutput,
                @"(?:auto-)?[Dd]etected language:\s*(\w+)(?:\s*\(p\s*=\s*([\d.]+)\))?");
            if (match.Success)
            {
                var lang = NormalizeLangName(match.Groups[1].Value);
                var prob = match.Groups[2].Success
                    ? float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                    : 0.5f;
                _logger.LogInformation("Detected language: {Language} (p={Probability:F3})", lang, prob);
                return (lang, prob);
            }

            // Fallback: some builds print probability table to stdout, e.g. "en    0.9780"
            var probMatch = Regex.Match(allOutput, @"^\s*([a-z]{2})\s+([\d.]+)\s*$", RegexOptions.Multiline);
            if (probMatch.Success)
            {
                var lang = probMatch.Groups[1].Value;
                var prob = float.Parse(probMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                _logger.LogInformation("Detected language (fallback parse): {Language} (p={Probability:F3})", lang, prob);
                return (lang, prob);
            }

            _logger.LogWarning("Could not parse language from whisper output:\n{Output}", allOutput);
            throw new InvalidOperationException(
                "Could not detect language. Ensure your whisper.cpp build supports --detect-language (v1.5.0+).");
        }

        /// <summary>
        /// Normalizes whisper language names (e.g. "english" → "en") to ISO 639-1 codes.
        /// </summary>
        private static string NormalizeLangName(string lang)
        {
            return lang.ToLowerInvariant() switch
            {
                "english" => "en",
                "spanish" => "es",
                "french" => "fr",
                "german" => "de",
                "italian" => "it",
                "portuguese" => "pt",
                "russian" => "ru",
                "japanese" => "ja",
                "chinese" => "zh",
                "korean" => "ko",
                "arabic" => "ar",
                "hindi" => "hi",
                "dutch" => "nl",
                "polish" => "pl",
                "turkish" => "tr",
                "swedish" => "sv",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "czech" => "cs",
                "romanian" => "ro",
                "hungarian" => "hu",
                "greek" => "el",
                "hebrew" => "he",
                "thai" => "th",
                "ukrainian" => "uk",
                "vietnamese" => "vi",
                "indonesian" => "id",
                "catalan" => "ca",
                "basque" => "eu",
                "galician" => "gl",
                // Short codes (2-3 chars) pass through; unknown long names are logged and kept as-is
                // rather than silently truncated to potentially invalid codes
                _ => lang.ToLowerInvariant()
            };
        }

        private static string GetLanguagePrompt(string language)
        {
            return language switch
            {
                "en" => "This is a transcription in English.",
                "es" => "Esta es una transcripción en español.",
                "fr" => "Ceci est une transcription en français.",
                "de" => "Dies ist eine Transkription auf Deutsch.",
                "it" => "Questa è una trascrizione in italiano.",
                "pt" => "Esta é uma transcrição em português.",
                "ja" => "これは日本語の書き起こしです。",
                "zh" => "这是中文转录。",
                "ko" => "이것은 한국어 전사입니다.",
                "ru" => "Это транскрипция на русском языке.",
                _ => ""
            };
        }

        /// <summary>
        /// Parses the last end timestamp from an SRT file and returns it in seconds.
        /// Returns 0 if the file is empty or unparseable.
        /// </summary>
        public static double ParseLastSrtTimestamp(string srtContent)
        {
            if (string.IsNullOrWhiteSpace(srtContent)) return 0;

            // Match SRT timestamp lines: "00:12:34,567 --> 00:12:39,890"
            var matches = Regex.Matches(srtContent, @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})");
            if (matches.Count == 0) return 0;

            var last = matches[matches.Count - 1];
            var hours = int.Parse(last.Groups[5].Value);
            var minutes = int.Parse(last.Groups[6].Value);
            var seconds = int.Parse(last.Groups[7].Value);
            var millis = int.Parse(last.Groups[8].Value);

            return hours * 3600.0 + minutes * 60.0 + seconds + millis / 1000.0;
        }

        /// <summary>
        /// Offsets all timestamps in an SRT string by the given number of seconds.
        /// Also renumbers entries starting from the given startIndex.
        /// </summary>
        public static string OffsetSrt(string srtContent, double offsetSeconds, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(srtContent)) return "";

            var result = new StringBuilder();
            int entryNum = startIndex;

            var lines = srtContent.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i].Trim();

                // Skip empty lines
                if (string.IsNullOrEmpty(line)) { i++; continue; }

                // Skip entry number line (we'll renumber)
                if (int.TryParse(line, out _))
                {
                    i++;
                    if (i >= lines.Length) break;
                    line = lines[i].Trim();
                }

                // Parse timestamp line
                var match = Regex.Match(line, @"(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})");
                if (!match.Success) { i++; continue; }

                var startTs = OffsetTimestamp(match.Groups[1].Value, offsetSeconds);
                var endTs = OffsetTimestamp(match.Groups[2].Value, offsetSeconds);

                result.AppendLine(entryNum.ToString());
                result.AppendLine($"{startTs} --> {endTs}");
                i++;

                // Collect subtitle text lines until empty line
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    result.AppendLine(lines[i].TrimEnd());
                    i++;
                }
                result.AppendLine();
                entryNum++;
            }

            return result.ToString();
        }

        private static string OffsetTimestamp(string timestamp, double offsetSeconds)
        {
            var match = Regex.Match(timestamp, @"(\d{2}):(\d{2}):(\d{2}),(\d{3})");
            if (!match.Success) return timestamp;

            var totalMs = int.Parse(match.Groups[1].Value) * 3600000
                        + int.Parse(match.Groups[2].Value) * 60000
                        + int.Parse(match.Groups[3].Value) * 1000
                        + int.Parse(match.Groups[4].Value);

            totalMs += (int)(offsetSeconds * 1000);
            if (totalMs < 0) totalMs = 0;

            var h = totalMs / 3600000;
            var m = (totalMs % 3600000) / 60000;
            var s = (totalMs % 60000) / 1000;
            var ms = totalMs % 1000;

            return $"{h:D2}:{m:D2}:{s:D2},{ms:D3}";
        }

        /// <summary>
        /// Returns the highest entry number in an SRT string.
        /// </summary>
        public static int CountSrtEntries(string srtContent)
        {
            if (string.IsNullOrWhiteSpace(srtContent)) return 0;
            var matches = Regex.Matches(srtContent, @"-->"); // Each entry has one --> line
            return matches.Count;
        }

        private string? FindWhisperExecutable()
        {
            var candidates = !string.IsNullOrEmpty(_binaryPath)
                ? new[] { _binaryPath, "whisper-cli", "main", "whisper" }
                : new[] { "whisper-cli", "main", "whisper" };

            foreach (var candidate in candidates)
            {
                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = candidate,
                            Arguments = "--help",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    process.WaitForExit(1000);

                    if (process.ExitCode == 0 || process.ExitCode == 1)
                    {
                        _logger.LogInformation("Found Whisper executable: {Executable}", candidate);
                        return candidate;
                    }
                }
                catch { }
            }

            return null;
        }
    }
}
