using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Setup
{
    public class WhisperSetupService
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly string _dataPath;

        // Static progress tracking — polled by the API endpoint.
        private static string _currentOperation = "";
        private static double _progress;
        private static string _progressMessage = "";
        private static bool _isRunning;
        private static string? _error;
        private static readonly object _lock = new();

        public static DownloadProgress CurrentProgress
        {
            get
            {
                lock (_lock)
                {
                    return new DownloadProgress
                    {
                        Operation = _currentOperation,
                        Percent = _progress,
                        Message = _progressMessage,
                        IsRunning = _isRunning,
                        Error = _error
                    };
                }
            }
        }

        public WhisperSetupService(ILogger logger, string dataPath)
        {
            _logger = logger;
            _dataPath = dataPath;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperSubs-Jellyfin-Plugin");
        }

        public string WhisperDirectory => Path.Combine(_dataPath, "whisper");
        public string ModelsDirectory => Path.Combine(_dataPath, "whisper", "models");

        public string BinaryPath => Path.Combine(WhisperDirectory,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "whisper-cli.exe" : "whisper-cli");

        public static string GetPlatformIdentifier()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RuntimeInformation.OSArchitecture == Architecture.X64 ? "win-x64" : "win-x86";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        }

        /// <summary>
        /// Checks whether the auto-downloaded binary and model exist.
        /// Also checks if the current config already points to valid files.
        /// </summary>
        public SetupStatus GetStatus()
        {
            var config = Plugin.Instance.Configuration;
            var autoBinaryExists = File.Exists(BinaryPath);
            var configBinaryValid = !string.IsNullOrEmpty(config.WhisperBinaryPath)
                                    && File.Exists(config.WhisperBinaryPath);

            // Check for any model in the auto-download directory
            string? autoModelPath = null;
            if (Directory.Exists(ModelsDirectory))
            {
                var bins = Directory.GetFiles(ModelsDirectory, "*.bin");
                if (bins.Length > 0) autoModelPath = bins[0];
            }

            var configModelValid = !string.IsNullOrEmpty(config.WhisperModelPath)
                                   && File.Exists(config.WhisperModelPath);

            // Also check if whisper-cli is discoverable in PATH
            var inPath = IsWhisperInPath();

            var binaryOk = autoBinaryExists || configBinaryValid || inPath;
            var modelOk = autoModelPath != null || configModelValid;

            return new SetupStatus
            {
                BinaryFound = binaryOk,
                BinaryPath = configBinaryValid ? config.WhisperBinaryPath
                           : autoBinaryExists ? BinaryPath
                           : inPath ? "whisper-cli (PATH)"
                           : null,
                ModelFound = modelOk,
                ModelPath = configModelValid ? config.WhisperModelPath : autoModelPath,
                Platform = GetPlatformIdentifier(),
                SetupComplete = binaryOk && modelOk,
                Gpu = DetectGpu()
            };
        }

        /// <summary>
        /// Downloads a whisper model from HuggingFace and saves it to the models directory.
        /// After download, automatically updates the plugin configuration.
        /// </summary>
        public async Task DownloadModelAsync(string modelFileName, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_isRunning) throw new InvalidOperationException("A download is already in progress.");
                _isRunning = true;
                _error = null;
                _currentOperation = "model";
                _progress = 0;
                _progressMessage = $"Starting download of {modelFileName}...";
            }

            try
            {
                Directory.CreateDirectory(ModelsDirectory);

                var url = $"{ModelCatalog.HuggingFaceBaseUrl}/{modelFileName}";
                var destPath = Path.Combine(ModelsDirectory, modelFileName);
                var tempPath = destPath + ".downloading";

                _logger.LogInformation("Downloading model {Model} from {Url}", modelFileName, url);

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (double)downloaded / totalBytes * 100;
                        var dlMB = downloaded / (1024.0 * 1024.0);
                        var totMB = totalBytes / (1024.0 * 1024.0);
                        lock (_lock)
                        {
                            _progress = pct;
                            _progressMessage = $"Downloading {modelFileName}: {dlMB:F1} / {totMB:F1} MB ({pct:F1}%)";
                        }
                    }
                }

                // Close the file stream before moving
                await fileStream.FlushAsync(cancellationToken);
                fileStream.Close();

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tempPath, destPath);

                // Auto-apply to plugin config
                var config = Plugin.Instance.Configuration;
                config.WhisperModelPath = destPath;
                Plugin.Instance.SaveConfiguration();

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = $"Model {modelFileName} downloaded and configured.";
                }

                _logger.LogInformation("Model downloaded to {Path} and config updated", destPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading model: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading model {Model}", modelFileName);
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        /// <summary>
        /// Detects which GPU backends are likely available on the host.
        /// </summary>
        public static GpuInfo DetectGpu()
        {
            var info = new GpuInfo();

            // NVIDIA: check for /dev/nvidia0 or nvidia-smi
            if (File.Exists("/dev/nvidia0") || File.Exists("/dev/nvidiactl"))
            {
                info.HasNvidia = true;
            }
            else
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "nvidia-smi", RedirectStandardOutput = true,
                        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                    });
                    if (p != null) { p.WaitForExit(3000); info.HasNvidia = p.ExitCode == 0; }
                }
                catch { /* not available */ }
            }

            // AMD ROCm: check for /dev/kfd (ROCm kernel driver)
            if (File.Exists("/dev/kfd"))
            {
                info.HasAmdGpu = true;
            }
            else
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "rocm-smi", RedirectStandardOutput = true,
                        RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                    });
                    if (p != null) { p.WaitForExit(3000); info.HasAmdGpu = p.ExitCode == 0; }
                }
                catch { /* not available */ }
            }

            // Intel / Vulkan: check for /dev/dri/renderD128
            if (File.Exists("/dev/dri/renderD128"))
            {
                info.HasRenderDevice = true;
            }

            // Recommend variant based on detection
            if (info.HasNvidia) info.RecommendedVariant = "cuda12";
            else if (info.HasAmdGpu) info.RecommendedVariant = "rocm";
            else if (info.HasRenderDevice) info.RecommendedVariant = "vulkan";
            else info.RecommendedVariant = "cpu";

            return info;
        }

        /// <summary>
        /// Downloads the whisper-cli binary from the whisper-subs GitHub release
        /// matching the current plugin version.
        /// </summary>
        /// <param name="variant">Binary variant: "cpu", "cuda12", or "vulkan".</param>
        public async Task DownloadBinaryAsync(string variant, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_isRunning) throw new InvalidOperationException("A download is already in progress.");
                _isRunning = true;
                _error = null;
                _currentOperation = "binary";
                _progress = 0;
                _progressMessage = $"Starting whisper-cli ({variant}) download...";
            }

            try
            {
                Directory.CreateDirectory(WhisperDirectory);

                var platform = GetPlatformIdentifier();
                var version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "3.2.0";
                var assetName = BinaryCatalog.GetAssetName(platform, variant);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) assetName += ".exe";

                var url = $"https://github.com/GeiserX/whisper-subs/releases/download/v{version}/{assetName}";

                _logger.LogInformation("Downloading whisper-cli from {Url} for platform {Platform}", url, platform);

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                var tempPath = BinaryPath + ".downloading";

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloaded += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (double)downloaded / totalBytes * 100;
                        var dlMB = downloaded / (1024.0 * 1024.0);
                        var totMB = totalBytes / (1024.0 * 1024.0);
                        lock (_lock)
                        {
                            _progress = pct;
                            _progressMessage = $"Downloading whisper-cli: {dlMB:F1} / {totMB:F1} MB ({pct:F1}%)";
                        }
                    }
                }

                await fileStream.FlushAsync(cancellationToken);
                fileStream.Close();

                if (File.Exists(BinaryPath)) File.Delete(BinaryPath);
                File.Move(tempPath, BinaryPath);

                // Make executable on Unix
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var chmod = System.Diagnostics.Process.Start("chmod", new[] { "+x", BinaryPath });
                    chmod?.WaitForExit(5000);
                }

                // Auto-apply to plugin config
                var config = Plugin.Instance.Configuration;
                config.WhisperBinaryPath = BinaryPath;
                Plugin.Instance.SaveConfiguration();

                lock (_lock)
                {
                    _progress = 100;
                    _progressMessage = "whisper-cli downloaded and configured.";
                }

                _logger.LogInformation("Binary downloaded to {Path} and config updated", BinaryPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _error = ex.Message;
                    _progressMessage = $"Error downloading binary: {ex.Message}";
                }
                _logger.LogError(ex, "Error downloading whisper-cli binary");
                throw;
            }
            finally
            {
                lock (_lock) { _isRunning = false; }
            }
        }

        private static bool IsWhisperInPath()
        {
            foreach (var name in new[] { "whisper-cli", "main", "whisper" })
            {
                try
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = "--help",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (p != null)
                    {
                        p.WaitForExit(1000);
                        if (p.ExitCode is 0 or 1) return true;
                    }
                }
                catch { /* not found */ }
            }
            return false;
        }
    }

    public class SetupStatus
    {
        public bool BinaryFound { get; set; }
        public string? BinaryPath { get; set; }
        public bool ModelFound { get; set; }
        public string? ModelPath { get; set; }
        public string Platform { get; set; } = "";
        public bool SetupComplete { get; set; }
        public GpuInfo Gpu { get; set; } = new();
    }

    public class GpuInfo
    {
        public bool HasNvidia { get; set; }
        public bool HasAmdGpu { get; set; }
        public bool HasRenderDevice { get; set; }
        public string RecommendedVariant { get; set; } = "cpu";
    }

    public class DownloadProgress
    {
        public string Operation { get; set; } = "";
        public double Percent { get; set; }
        public string Message { get; set; } = "";
        public bool IsRunning { get; set; }
        public string? Error { get; set; }
    }
}
