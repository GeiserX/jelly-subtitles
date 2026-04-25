using System.Reflection;
using System.Runtime.InteropServices;
using WhisperSubs.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Extended tests for WhisperSetupService covering directory paths, binary path,
/// platform detection, and ComputeSha256.
/// </summary>
[Collection("SetupServiceTests")]
public class SetupServiceExtendedTests
{
    private static WhisperSetupService CreateService(string dataPath = "/tmp/test-whisper")
    {
        var logger = new NullLogger<WhisperSetupService>();
        return new WhisperSetupService(logger, dataPath);
    }

    [Fact]
    public void WhisperDirectory_IsSubdirectoryOfDataPath()
    {
        var service = CreateService("/data/plugins");
        Assert.Equal("/data/plugins/whisper", service.WhisperDirectory);
    }

    [Fact]
    public void ModelsDirectory_IsSubdirectoryOfWhisperDir()
    {
        var service = CreateService("/data/plugins");
        Assert.Equal("/data/plugins/whisper/models", service.ModelsDirectory);
    }

    [Fact]
    public void BinaryPath_ContainsWhisperCli()
    {
        var service = CreateService("/data/plugins");
        Assert.Contains("whisper-cli", service.BinaryPath);
        Assert.StartsWith("/data/plugins/whisper/", service.BinaryPath);
    }

    [Fact]
    public void BinaryPath_PlatformSpecificExtension()
    {
        var service = CreateService("/tmp/test");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.EndsWith(".exe", service.BinaryPath);
        }
        else
        {
            Assert.DoesNotContain(".exe", service.BinaryPath);
        }
    }

    [Fact]
    public void GetPlatformIdentifier_MatchesCurrentOs()
    {
        var platform = WhisperSetupService.GetPlatformIdentifier();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.StartsWith("osx-", platform);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.StartsWith("linux-", platform);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.StartsWith("win-", platform);
        }
    }

    [Fact]
    public void GetPlatformIdentifier_IncludesArchitecture()
    {
        var platform = WhisperSetupService.GetPlatformIdentifier();
        var validSuffixes = new[] { "-x64", "-x86", "-arm64" };
        Assert.Contains(validSuffixes, suffix => platform.EndsWith(suffix));
    }

    [Fact]
    public void ComputeSha256_ProducesValidHash()
    {
        var method = typeof(WhisperSetupService).GetMethod(
            "ComputeSha256", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var tempFile = Path.Combine(Path.GetTempPath(), $"sha256test_{Guid.NewGuid()}");
        File.WriteAllText(tempFile, "hello world");
        try
        {
            var hash = (string)method!.Invoke(null, new object[] { tempFile })!;
            Assert.NotNull(hash);
            Assert.Equal(64, hash.Length); // SHA256 = 32 bytes = 64 hex chars
            Assert.Matches("^[0-9a-f]+$", hash);
            // Known SHA256 of "hello world"
            Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void DetectGpu_ReturnsValidResult()
    {
        var gpu = WhisperSetupService.DetectGpu();
        Assert.NotNull(gpu);
        // RecommendedVariant must be one of the known values
        Assert.Contains(gpu.RecommendedVariant, new[] { "cpu", "cuda12", "vulkan", "rocm" });
    }

    [Fact]
    public void DetectGpu_OnMacOS_ExpectsCpu()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        var gpu = WhisperSetupService.DetectGpu();
        // macOS doesn't have /dev/nvidia0, /dev/kfd, or /dev/dri
        Assert.False(gpu.HasNvidia);
        Assert.False(gpu.HasAmdGpu);
        Assert.False(gpu.HasRenderDevice);
        Assert.Equal("cpu", gpu.RecommendedVariant);
    }

    [Fact]
    public void IsWhisperInPath_DoesNotThrow()
    {
        var method = typeof(WhisperSetupService).GetMethod(
            "IsWhisperInPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Should not throw regardless of whether whisper is installed
        var result = method!.Invoke(null, null);
        Assert.IsType<bool>(result);
    }

    // ── TryAcquire thread safety ──

    [Fact]
    public async Task TryAcquire_ConcurrentCalls_OnlyOneSucceeds()
    {
        ForceReleaseDownloadLock();

        int successCount = 0;
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            if (WhisperSetupService.TryAcquire("concurrent", "test"))
            {
                Interlocked.Increment(ref successCount);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1, successCount);

        ForceReleaseDownloadLock();
    }

    [Fact]
    public void TryAcquire_ClearsErrorFromPreviousRun()
    {
        ForceReleaseDownloadLock();

        // Set up error state
        var errorField = typeof(WhisperSetupService).GetField("_error",
            BindingFlags.NonPublic | BindingFlags.Static);
        errorField?.SetValue(null, "previous error");

        Assert.True(WhisperSetupService.TryAcquire("new-op", "Starting..."));
        var progress = WhisperSetupService.CurrentProgress;
        Assert.Null(progress.Error);

        ForceReleaseDownloadLock();
    }

    private static void ForceReleaseDownloadLock()
    {
        var field = typeof(WhisperSetupService).GetField("_isRunning",
            BindingFlags.NonPublic | BindingFlags.Static);
        field?.SetValue(null, false);
    }
}
