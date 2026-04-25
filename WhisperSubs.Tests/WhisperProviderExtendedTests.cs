using System.Reflection;
using WhisperSubs.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Extended tests for WhisperProvider covering constructor, properties,
/// and edge cases in public static methods.
/// </summary>
public class WhisperProviderExtendedTests
{
    private static WhisperProvider CreateProvider(
        string modelPath = "/tmp/model.bin",
        string binaryPath = "",
        int threadCount = 0)
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
        return new WhisperProvider(logger, modelPath, binaryPath, threadCount);
    }

    [Fact]
    public void Name_ReturnsWhisper()
    {
        var provider = CreateProvider();
        Assert.Equal("Whisper", provider.Name);
    }

    [Fact]
    public void Constructor_StoresParameters()
    {
        var provider = CreateProvider("/models/test.bin", "/bin/whisper-cli", 8);
        Assert.Equal("Whisper", provider.Name);

        // Verify internal state via reflection
        var modelField = typeof(WhisperProvider).GetField("_modelPath", BindingFlags.NonPublic | BindingFlags.Instance);
        var binaryField = typeof(WhisperProvider).GetField("_binaryPath", BindingFlags.NonPublic | BindingFlags.Instance);
        var threadField = typeof(WhisperProvider).GetField("_threadCount", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.Equal("/models/test.bin", modelField?.GetValue(provider));
        Assert.Equal("/bin/whisper-cli", binaryField?.GetValue(provider));
        Assert.Equal(8, threadField?.GetValue(provider));
    }

    [Fact]
    public async Task TranscribeAsync_MissingModel_ThrowsFileNotFound()
    {
        var provider = CreateProvider("/nonexistent/model.bin");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranscribeAsync("/tmp/audio.wav", "en", CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_EmptyModel_ThrowsFileNotFound()
    {
        var provider = CreateProvider("");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranscribeAsync("/tmp/audio.wav", "en", CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_MissingAudio_ThrowsFileNotFound()
    {
        // Create a temp file to act as model
        var modelPath = Path.Combine(Path.GetTempPath(), $"test_model_{Guid.NewGuid()}.bin");
        File.WriteAllText(modelPath, "fake model");
        try
        {
            var provider = CreateProvider(modelPath);
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => provider.TranscribeAsync("/nonexistent/audio.wav", "en", CancellationToken.None));
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public async Task DetectLanguageAsync_MissingModel_ThrowsFileNotFound()
    {
        var provider = CreateProvider("/nonexistent/model.bin");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.DetectLanguageAsync("/tmp/audio.wav", CancellationToken.None));
    }

    [Fact]
    public async Task DetectLanguageAsync_EmptyModel_ThrowsFileNotFound()
    {
        var provider = CreateProvider("");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.DetectLanguageAsync("/tmp/audio.wav", CancellationToken.None));
    }

    [Fact]
    public async Task DetectLanguageAsync_MissingAudio_ThrowsFileNotFound()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"test_model_{Guid.NewGuid()}.bin");
        File.WriteAllText(modelPath, "fake model");
        try
        {
            var provider = CreateProvider(modelPath);
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => provider.DetectLanguageAsync("/nonexistent/audio.wav", CancellationToken.None));
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    // ── ParseLastSrtTimestamp edge cases ──

    [Fact]
    public void ParseLastSrtTimestamp_NoTimestampLines_ReturnsZero()
    {
        var srt = "This is not\na valid SRT file\n";
        Assert.Equal(0, WhisperProvider.ParseLastSrtTimestamp(srt));
    }

    [Fact]
    public void ParseLastSrtTimestamp_ZeroTimestamp()
    {
        var srt = "1\n00:00:00,000 --> 00:00:00,000\nEmpty\n";
        Assert.Equal(0, WhisperProvider.ParseLastSrtTimestamp(srt));
    }

    [Fact]
    public void ParseLastSrtTimestamp_LargeTimestamp()
    {
        var srt = "1\n00:00:00,000 --> 23:59:59,999\nLong movie\n";
        var result = WhisperProvider.ParseLastSrtTimestamp(srt);
        Assert.Equal(23 * 3600 + 59 * 60 + 59 + 0.999, result, precision: 3);
    }

    // ── CountSrtEntries edge cases ──

    [Fact]
    public void CountSrtEntries_WhitespaceOnly_ReturnsZero()
    {
        Assert.Equal(0, WhisperProvider.CountSrtEntries("   \n\n  "));
    }

    [Fact]
    public void CountSrtEntries_SingleEntry()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,000\nHello\n";
        Assert.Equal(1, WhisperProvider.CountSrtEntries(srt));
    }

    // ── OffsetSrt edge cases ──

    [Fact]
    public void OffsetSrt_WhitespaceOnly_ReturnsEmpty()
    {
        Assert.Equal("", WhisperProvider.OffsetSrt("   \n\n  ", 10, 1));
    }

    [Fact]
    public void OffsetSrt_NoTimestampLines_ReturnsEmpty()
    {
        var result = WhisperProvider.OffsetSrt("just some text\n", 10, 1);
        // No valid SRT entries, so no output
        Assert.Equal("", result.Trim());
    }

    [Fact]
    public void OffsetSrt_PreservesMultilineSubtitleText()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,000\nLine one\nLine two\n\n2\n00:00:06,000 --> 00:00:10,000\nThird\n";
        var result = WhisperProvider.OffsetSrt(srt, 0, 1);
        Assert.Contains("Line one", result);
        Assert.Contains("Line two", result);
        Assert.Contains("Third", result);
    }

    [Fact]
    public void OffsetSrt_ZeroOffset_PreservesTimestamps()
    {
        var srt = "1\n00:01:30,500 --> 00:02:00,000\nText\n";
        var result = WhisperProvider.OffsetSrt(srt, 0, 1);
        Assert.Contains("00:01:30,500 --> 00:02:00,000", result);
    }

    [Fact]
    public void OffsetSrt_MillisecondPrecision()
    {
        var srt = "1\n00:00:00,000 --> 00:00:01,000\nHello\n";
        var result = WhisperProvider.OffsetSrt(srt, 0.5, 1);
        Assert.Contains("00:00:00,500 --> 00:00:01,500", result);
    }

    // ── FindWhisperExecutable ──

    [Fact]
    public void FindWhisperExecutable_WithConfiguredBinaryPath_NonExistent_ReturnsNull()
    {
        var provider = CreateProvider("/tmp/model.bin", "/nonexistent/whisper-cli");
        var method = typeof(WhisperProvider).GetMethod(
            "FindWhisperExecutable", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(provider, null);
        Assert.Null(result);
    }

    [Fact]
    public void FindWhisperExecutable_WithConfiguredBinaryPath_Existing_ReturnsPath()
    {
        var tempBinary = Path.Combine(Path.GetTempPath(), $"whisper-cli-test-{Guid.NewGuid()}");
        File.WriteAllText(tempBinary, "fake binary");
        try
        {
            var provider = CreateProvider("/tmp/model.bin", tempBinary);
            var method = typeof(WhisperProvider).GetMethod(
                "FindWhisperExecutable", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var result = (string?)method!.Invoke(provider, null);
            Assert.Equal(tempBinary, result);
        }
        finally
        {
            File.Delete(tempBinary);
        }
    }

    [Fact]
    public void FindWhisperExecutable_CachesResult()
    {
        var tempBinary = Path.Combine(Path.GetTempPath(), $"whisper-cli-cache-{Guid.NewGuid()}");
        File.WriteAllText(tempBinary, "fake binary");
        try
        {
            var provider = CreateProvider("/tmp/model.bin", tempBinary);
            var method = typeof(WhisperProvider).GetMethod(
                "FindWhisperExecutable", BindingFlags.NonPublic | BindingFlags.Instance);

            // First call
            var result1 = (string?)method!.Invoke(provider, null);
            Assert.Equal(tempBinary, result1);

            // Second call should return cached value (even if file is deleted)
            File.Delete(tempBinary);
            var result2 = (string?)method.Invoke(provider, null);
            Assert.Equal(tempBinary, result2);
        }
        finally
        {
            if (File.Exists(tempBinary)) File.Delete(tempBinary);
        }
    }
}
