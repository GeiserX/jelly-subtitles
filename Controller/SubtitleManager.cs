using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        private readonly PluginConfiguration _config;

        public SubtitleManager(ILibraryManager libraryManager, ILogger<SubtitleManager> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _config = Plugin.Instance.Configuration;
        }

        public async Task GenerateSubtitleAsync(BaseItem item, ISubtitleProvider provider, CancellationToken cancellationToken)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            
            var mediaPath = item.Path;
            if (string.IsNullOrEmpty(mediaPath) || !File.Exists(mediaPath))
            {
                _logger.LogWarning("Media file not found for item {ItemName}", item.Name);
                return;
            }

            // 1. Extract Audio using FFmpeg
            var tempAudioPath = Path.Combine(Path.GetTempPath(), $"{item.Id}_{Guid.NewGuid()}.wav");
            _logger.LogInformation("Extracting audio for {ItemName} to {AudioPath}", item.Name, tempAudioPath);
            
            try
            {
                await ExtractAudioAsync(mediaPath, tempAudioPath, cancellationToken);
                
                // 2. Transcribe
                var srtContent = await provider.TranscribeAsync(tempAudioPath, "en", cancellationToken);

                // 3. Save Subtitle
                var srtPath = Path.ChangeExtension(mediaPath, ".en.generated.srt");
                await File.WriteAllTextAsync(srtPath, srtContent, cancellationToken);
                _logger.LogInformation("Saved subtitle to {SrtPath}", srtPath);

                // 4. Refresh Item
                await item.RefreshMetadata(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating subtitle for {ItemName}", item.Name);
                throw;
            }
            finally
            {
                // Clean up temp audio file
                if (File.Exists(tempAudioPath))
                {
                    try
                    {
                        File.Delete(tempAudioPath);
                        _logger.LogDebug("Deleted temporary audio file: {AudioPath}", tempAudioPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temporary audio file: {AudioPath}", tempAudioPath);
                    }
                }
            }
        }

        private async Task ExtractAudioAsync(string videoPath, string outputAudioPath, CancellationToken cancellationToken)
        {
            // Try to find ffmpeg in common locations
            var ffmpegPath = FindFfmpegExecutable();
            if (ffmpegPath == null)
            {
                throw new InvalidOperationException(
                    "FFmpeg not found. Please ensure ffmpeg is installed and available in PATH or at /usr/lib/jellyfin-ffmpeg/ffmpeg");
            }

            // Extract audio: convert to WAV, mono, 16kHz (good for Whisper)
            var arguments = $"-i \"{videoPath}\" -vn -acodec pcm_s16le -ac 1 -ar 16000 -y \"{outputAudioPath}\"";

            _logger.LogInformation("Running FFmpeg: {FfmpegPath} {Arguments}", ffmpegPath, arguments);

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

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    // FFmpeg writes progress to stderr, so we log it as debug
                    _logger.LogDebug("FFmpeg: {Output}", e.Data);
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var error = errorBuilder.ToString();
                throw new InvalidOperationException(
                    $"FFmpeg process failed with exit code {process.ExitCode}. Error: {error}");
            }

            if (!File.Exists(outputAudioPath))
            {
                throw new FileNotFoundException($"Audio extraction failed. Output file not found: {outputAudioPath}");
            }

            _logger.LogInformation("Successfully extracted audio to {AudioPath}", outputAudioPath);
        }

        private string? FindFfmpegExecutable()
        {
            // Try common FFmpeg locations
            var candidates = new[]
            {
                "/usr/lib/jellyfin-ffmpeg/ffmpeg", // Jellyfin's bundled FFmpeg
                "ffmpeg", // System PATH
                "/usr/bin/ffmpeg" // Common system location
            };

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
                    process.WaitForExit(1000); // Wait max 1 second
                    
                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("Found FFmpeg executable: {Executable}", candidate);
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
