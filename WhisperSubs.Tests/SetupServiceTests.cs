using System.Reflection;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

[Collection("SetupServiceTests")]
public class SetupServiceTests
{
    [Fact]
    public void GetPlatformIdentifier_ReturnsNonEmpty()
    {
        var platform = WhisperSetupService.GetPlatformIdentifier();
        Assert.False(string.IsNullOrEmpty(platform));
    }

    [Fact]
    public void GetPlatformIdentifier_ReturnsKnownFormat()
    {
        var platform = WhisperSetupService.GetPlatformIdentifier();
        // Must match one of the known platform patterns
        var knownPrefixes = new[] { "linux-", "win-", "osx-" };
        Assert.Contains(knownPrefixes, p => platform.StartsWith(p));
    }

    [Fact]
    public void TryAcquire_FirstCall_Succeeds()
    {
        // Reset static state by completing any prior operation
        ForceReleaseDownloadLock();

        Assert.True(WhisperSetupService.TryAcquire("test", "Starting test..."));

        // Verify state
        var progress = WhisperSetupService.CurrentProgress;
        Assert.True(progress.IsRunning);
        Assert.Equal("test", progress.Operation);
        Assert.Equal("Starting test...", progress.Message);
        Assert.Equal(0, progress.Percent);
        Assert.Null(progress.Error);

        // Clean up
        ForceReleaseDownloadLock();
    }

    [Fact]
    public void TryAcquire_SecondCall_Fails()
    {
        ForceReleaseDownloadLock();

        Assert.True(WhisperSetupService.TryAcquire("first", "First op"));
        Assert.False(WhisperSetupService.TryAcquire("second", "Second op"));

        // Verify first operation is still active
        var progress = WhisperSetupService.CurrentProgress;
        Assert.Equal("first", progress.Operation);

        ForceReleaseDownloadLock();
    }

    [Fact]
    public void TryAcquire_AfterRelease_CanReacquire()
    {
        ForceReleaseDownloadLock();

        Assert.True(WhisperSetupService.TryAcquire("first", "First"));
        ForceReleaseDownloadLock();
        Assert.True(WhisperSetupService.TryAcquire("second", "Second"));

        var progress = WhisperSetupService.CurrentProgress;
        Assert.Equal("second", progress.Operation);

        ForceReleaseDownloadLock();
    }

    [Fact]
    public void CurrentProgress_WhenIdle_IsNotRunning()
    {
        ForceReleaseDownloadLock();

        // Also clear any leftover state from concurrent tests
        var progress = WhisperSetupService.CurrentProgress;
        Assert.False(progress.IsRunning);
    }

    [Fact]
    public void GetInstallHint_KnownLibraries()
    {
        var method = typeof(WhisperSetupService).GetMethod(
            "GetInstallHint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Contains("libgomp1", (string)method!.Invoke(null, new object[] { "libgomp.so.1" })!);
        Assert.Contains("libvulkan1", (string)method.Invoke(null, new object[] { "libvulkan.so.1" })!);
        Assert.Contains("CUDA", (string)method.Invoke(null, new object[] { "libcuda.so.1" })!);
        Assert.Contains("CUDA", (string)method.Invoke(null, new object[] { "libcudart.so.12" })!);
        Assert.Contains("ROCm", (string)method.Invoke(null, new object[] { "libamdhip64.so" })!);
    }

    [Fact]
    public void GetInstallHint_UnknownLibrary_FallbackMessage()
    {
        var method = typeof(WhisperSetupService).GetMethod(
            "GetInstallHint",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, new object[] { "libfoo.so" })!;
        Assert.Contains("libfoo.so", result);
        Assert.Contains("apt install", result);
    }

    [Fact]
    public void DetectGpu_ReturnsValidInfo()
    {
        var gpu = WhisperSetupService.DetectGpu();
        Assert.NotNull(gpu);
        Assert.False(string.IsNullOrEmpty(gpu.RecommendedVariant));
        // On macOS (test environment), no GPU devices expected
        var validVariants = new[] { "cpu", "cuda12", "vulkan", "rocm" };
        Assert.Contains(gpu.RecommendedVariant, validVariants);
    }

    [Fact]
    public void Constructor_SetsDataPath()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<WhisperSetupService>();
        var service = new WhisperSetupService(logger, "/tmp/test-data");

        Assert.Equal("/tmp/test-data/whisper", service.WhisperDirectory);
        Assert.Equal("/tmp/test-data/whisper/models", service.ModelsDirectory);
        Assert.Contains("whisper-cli", service.BinaryPath);
    }

    /// <summary>
    /// Force-releases the static download lock by setting _isRunning to false via reflection.
    /// </summary>
    private static void ForceReleaseDownloadLock()
    {
        var field = typeof(WhisperSetupService).GetField("_isRunning",
            BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, false);
    }
}

public class SetupStatusTests
{
    [Fact]
    public void DefaultValues()
    {
        var status = new SetupStatus();

        Assert.False(status.BinaryFound);
        Assert.False(status.BinaryFoundInPath);
        Assert.Null(status.BinaryPath);
        Assert.False(status.ModelFound);
        Assert.Null(status.ModelPath);
        Assert.Equal("", status.Platform);
        Assert.False(status.SetupComplete);
        Assert.NotNull(status.Gpu);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var gpu = new GpuInfo { HasNvidia = true };
        var status = new SetupStatus
        {
            BinaryFound = true,
            BinaryFoundInPath = true,
            BinaryPath = "/usr/bin/whisper-cli",
            ModelFound = true,
            ModelPath = "/models/test.bin",
            Platform = "linux-x64",
            SetupComplete = true,
            Gpu = gpu
        };

        Assert.True(status.BinaryFound);
        Assert.True(status.BinaryFoundInPath);
        Assert.Equal("/usr/bin/whisper-cli", status.BinaryPath);
        Assert.True(status.ModelFound);
        Assert.Equal("/models/test.bin", status.ModelPath);
        Assert.Equal("linux-x64", status.Platform);
        Assert.True(status.SetupComplete);
        Assert.Same(gpu, status.Gpu);
    }
}

public class GpuInfoTests
{
    [Fact]
    public void DefaultValues()
    {
        var gpu = new GpuInfo();

        Assert.False(gpu.HasNvidia);
        Assert.False(gpu.HasCudaLibrary);
        Assert.False(gpu.HasAmdGpu);
        Assert.False(gpu.HasRocmLibrary);
        Assert.False(gpu.HasRenderDevice);
        Assert.False(gpu.HasVulkanLibrary);
        Assert.False(gpu.HasOpenMP);
        Assert.Equal("cpu", gpu.RecommendedVariant);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var gpu = new GpuInfo
        {
            HasNvidia = true,
            HasCudaLibrary = true,
            HasAmdGpu = true,
            HasRocmLibrary = true,
            HasRenderDevice = true,
            HasVulkanLibrary = true,
            HasOpenMP = true,
            RecommendedVariant = "cuda12"
        };

        Assert.True(gpu.HasNvidia);
        Assert.True(gpu.HasCudaLibrary);
        Assert.True(gpu.HasAmdGpu);
        Assert.True(gpu.HasRocmLibrary);
        Assert.True(gpu.HasRenderDevice);
        Assert.True(gpu.HasVulkanLibrary);
        Assert.True(gpu.HasOpenMP);
        Assert.Equal("cuda12", gpu.RecommendedVariant);
    }
}

public class DownloadProgressTests
{
    [Fact]
    public void DefaultValues()
    {
        var progress = new DownloadProgress();

        Assert.Equal("", progress.Operation);
        Assert.Equal(0, progress.Percent);
        Assert.Equal("", progress.Message);
        Assert.False(progress.IsRunning);
        Assert.Null(progress.Error);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var progress = new DownloadProgress
        {
            Operation = "model",
            Percent = 50.5,
            Message = "Downloading...",
            IsRunning = true,
            Error = "Some error"
        };

        Assert.Equal("model", progress.Operation);
        Assert.Equal(50.5, progress.Percent);
        Assert.Equal("Downloading...", progress.Message);
        Assert.True(progress.IsRunning);
        Assert.Equal("Some error", progress.Error);
    }
}
