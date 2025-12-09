using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Providers
{
    public class WhisperProvider : ISubtitleProvider
    {
        private readonly ILogger<WhisperProvider> _logger;
        private readonly string _modelPath;

        public string Name => "Whisper";

        public WhisperProvider(ILogger<WhisperProvider> logger, string modelPath)
        {
            _logger = logger;
            _modelPath = modelPath;
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

            // Create temporary output file for SRT
            var tempSrtPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.srt");
            
            try
            {
                // Try whisper.cpp first (whisper-cli or main)
                var whisperExecutable = FindWhisperExecutable();
                if (whisperExecutable == null)
                {
                    throw new InvalidOperationException(
                        "Whisper executable not found. Please install whisper.cpp and ensure 'whisper-cli' or 'main' is in PATH.");
                }

                var arguments = $"-m \"{_modelPath}\" -f \"{audioPath}\" -l {language} -osrt -of \"{Path.GetFileNameWithoutExtension(tempSrtPath)}\" -pp \"{Path.GetDirectoryName(tempSrtPath)}\"";

                _logger.LogInformation("Running: {Executable} {Arguments}", whisperExecutable, arguments);

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = whisperExecutable,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                        _logger.LogDebug("Whisper output: {Output}", e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                        _logger.LogWarning("Whisper error: {Error}", e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    var error = errorBuilder.ToString();
                    throw new InvalidOperationException(
                        $"Whisper process failed with exit code {process.ExitCode}. Error: {error}");
                }

                // Read the generated SRT file
                if (File.Exists(tempSrtPath))
                {
                    var srtContent = await File.ReadAllTextAsync(tempSrtPath, cancellationToken);
                    _logger.LogInformation("Successfully generated subtitle file");
                    return srtContent;
                }
                else
                {
                    // Try alternative output path (whisper.cpp might use different naming)
                    var altPath = Path.ChangeExtension(audioPath, ".srt");
                    if (File.Exists(altPath))
                    {
                        var srtContent = await File.ReadAllTextAsync(altPath, cancellationToken);
                        _logger.LogInformation("Found subtitle at alternative path: {Path}", altPath);
                        return srtContent;
                    }

                    throw new FileNotFoundException(
                        $"Subtitle file not found at expected location: {tempSrtPath}");
                }
            }
            finally
            {
                // Clean up temp file if it exists
                if (File.Exists(tempSrtPath))
                {
                    try
                    {
                        File.Delete(tempSrtPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temp file {Path}", tempSrtPath);
                    }
                }
            }
        }

        private string? FindWhisperExecutable()
        {
            // Try common whisper.cpp executable names
            var candidates = new[] { "whisper-cli", "main", "whisper" };
            
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
                    process.WaitForExit(1000); // Wait max 1 second
                    
                    if (process.ExitCode == 0 || process.ExitCode == 1) // Help usually exits with 1
                    {
                        _logger.LogInformation("Found Whisper executable: {Executable}", candidate);
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
    }
}
