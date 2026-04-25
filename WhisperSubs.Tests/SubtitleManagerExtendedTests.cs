using System.Reflection;
using WhisperSubs.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MediaBrowser.Controller.Library;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Extended tests for SubtitleManager covering private helpers and edge cases.
/// </summary>
public class SubtitleManagerExtendedTests
{
    private static SubtitleManager CreateManager()
    {
        var libraryManager = new Mock<ILibraryManager>();
        var logger = new NullLogger<SubtitleManager>();
        return new SubtitleManager(libraryManager.Object, logger);
    }

    // ── ResolveMediaPath ──
    // Note: BaseItem.Path/Name/Id are non-virtual so Moq cannot mock them.
    // We use MediaBrowser.Controller.Entities.Video (concrete) and set Path directly.

    private static string? CallResolveMediaPath(SubtitleManager manager, MediaBrowser.Controller.Entities.BaseItem item)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "ResolveMediaPath", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (string?)method!.Invoke(manager, new object[] { item });
    }

    private static MediaBrowser.Controller.Entities.Video CreateVideoItem(string? path, string name = "Test Item")
    {
        var item = new MediaBrowser.Controller.Entities.Video
        {
            Path = path!,
            Name = name
        };
        return item;
    }

    [Fact]
    public void ResolveMediaPath_NullPath_ReturnsNull()
    {
        var manager = CreateManager();
        var item = CreateVideoItem(null);
        var result = CallResolveMediaPath(manager, item);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveMediaPath_EmptyPath_ReturnsNull()
    {
        var manager = CreateManager();
        var item = CreateVideoItem("");
        var result = CallResolveMediaPath(manager, item);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveMediaPath_ExistingFile_ReturnsPath()
    {
        var manager = CreateManager();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_media_{Guid.NewGuid()}.mkv");
        File.WriteAllText(tempFile, "fake media");
        try
        {
            var item = CreateVideoItem(tempFile);
            var result = CallResolveMediaPath(manager, item);
            Assert.Equal(tempFile, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveMediaPath_NonExistentFile_ReturnsNull()
    {
        var manager = CreateManager();
        var item = CreateVideoItem("/nonexistent/path/movie.mkv");
        var result = CallResolveMediaPath(manager, item);
        Assert.Null(result);
    }

    // ── FindFfmpegExecutable / FindFfprobeExecutable ──

    private static string? CallFindFfmpegExecutable(SubtitleManager manager)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "FindFfmpegExecutable", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (string?)method!.Invoke(manager, null);
    }

    private static string? CallFindFfprobeExecutable(SubtitleManager manager)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "FindFfprobeExecutable", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (string?)method!.Invoke(manager, null);
    }

    [Fact]
    public void FindFfmpegExecutable_ReturnsPathOrNull()
    {
        var manager = CreateManager();
        var result = CallFindFfmpegExecutable(manager);
        // On macOS with ffmpeg installed, this returns a path; otherwise null
        // Either way, the method should not throw
        Assert.True(result == null || result.Length > 0);
    }

    [Fact]
    public void FindFfprobeExecutable_ReturnsPathOrNull()
    {
        var manager = CreateManager();
        var result = CallFindFfprobeExecutable(manager);
        Assert.True(result == null || result.Length > 0);
    }

    // ── FindExecutable ──

    private static string? CallFindExecutable(SubtitleManager manager, string[] candidates)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "FindExecutable", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (string?)method!.Invoke(manager, new object[] { candidates });
    }

    [Fact]
    public void FindExecutable_NoValidCandidates_ReturnsNull()
    {
        var manager = CreateManager();
        var result = CallFindExecutable(manager, new[]
        {
            "/nonexistent/binary1",
            "/nonexistent/binary2"
        });
        Assert.Null(result);
    }

    [Fact]
    public void FindExecutable_ExistingAbsolutePath_ReturnsIt()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_exec_{Guid.NewGuid()}");
        File.WriteAllText(tempFile, "fake executable");
        try
        {
            var manager = CreateManager();
            var result = CallFindExecutable(manager, new[] { tempFile });
            Assert.Equal(tempFile, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void FindExecutable_NonExistentPath_Fails()
    {
        var manager = CreateManager();
        var result = CallFindExecutable(manager, new[]
        {
            "/nonexistent/fake_binary_that_does_not_exist"
        });
        Assert.Null(result);
    }

    [Fact]
    public void FindExecutable_InvalidCommand_ReturnsNull()
    {
        var manager = CreateManager();
        // A non-rooted name that won't exist
        var result = CallFindExecutable(manager, new[]
        {
            "this_binary_definitely_does_not_exist_anywhere_in_path_12345"
        });
        Assert.Null(result);
    }

    // ── GenerateSubtitleAsync null item ──

    [Fact]
    public async Task GenerateSubtitleAsync_NullItem_ThrowsArgumentNull()
    {
        var manager = CreateManager();
        var provider = new Mock<WhisperSubs.Providers.ISubtitleProvider>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => manager.GenerateSubtitleAsync(null!, provider.Object, "en", CancellationToken.None));
    }

    // ── ConvertSrtToLrc additional edge cases ──

    [Fact]
    public void ConvertSrtToLrc_MalformedTimestamp_Skipped()
    {
        var srt = "1\nNOT_A_TIMESTAMP --> ALSO_NOT\nText here\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);
        // Should have header but no LRC entries
        Assert.Contains("[by:WhisperSubs]", result);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(lines, l => l.StartsWith("[") && !l.StartsWith("[by:") && !l.StartsWith("[ti:"));
    }

    [Fact]
    public void ConvertSrtToLrc_TooFewLines_Skipped()
    {
        // An entry with only 2 lines (number + timestamp, no text)
        var srt = "1\n00:00:01,000 --> 00:00:05,000\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);
        // With exactly line count = 2 after split, it's < 3 so skipped
        Assert.Contains("[by:WhisperSubs]", result);
    }

    [Fact]
    public void ConvertSrtToLrc_WindowsLineEndings()
    {
        var srt = "1\r\n00:00:01,000 --> 00:00:05,000\r\nHello\r\n\r\n2\r\n00:00:06,000 --> 00:00:10,000\r\nWorld\r\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);
        Assert.Contains("[00:01.00]Hello", result);
        Assert.Contains("[00:06.00]World", result);
    }

    [Fact]
    public void ConvertSrtToLrc_ZeroTimestamp()
    {
        var srt = "1\n00:00:00,000 --> 00:00:03,000\nStart\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);
        Assert.Contains("[00:00.00]Start", result);
    }

    // ── GroupSpeechIntoChunks additional ──

    private static List<(double Start, double End)> CallGroupSpeechIntoChunks(
        List<(double Start, double End)> segments, double target = 30.0)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "GroupSpeechIntoChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (List<(double Start, double End)>)method!.Invoke(null, new object[] { segments, target })!;
    }

    [Fact]
    public void GroupSpeechIntoChunks_ExactlyAtTarget_NoSplit()
    {
        var segments = new List<(double, double)>
        {
            (0, 15), (15, 30)
        };
        var result = CallGroupSpeechIntoChunks(segments, 30.0);
        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(30, result[0].End);
    }

    [Fact]
    public void GroupSpeechIntoChunks_ManySmallSegments()
    {
        var segments = new List<(double, double)>();
        for (int i = 0; i < 100; i++)
        {
            segments.Add((i * 1.0, i * 1.0 + 0.5));
        }
        var result = CallGroupSpeechIntoChunks(segments, 30.0);
        Assert.True(result.Count > 1);
        // Each chunk should span roughly 30s
        foreach (var chunk in result.Take(result.Count - 1))
        {
            Assert.True(chunk.End - chunk.Start <= 31);
        }
    }

    // ── GenerateFixedChunks additional ──

    private static List<(double Start, double End)> CallGenerateFixedChunks(
        double totalDuration, double chunkDuration)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "GenerateFixedChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (List<(double Start, double End)>)method!.Invoke(null, new object[] { totalDuration, chunkDuration })!;
    }

    [Fact]
    public void GenerateFixedChunks_VeryShortDuration()
    {
        var result = CallGenerateFixedChunks(5, 30);
        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(5, result[0].End);
    }

    [Fact]
    public void GenerateFixedChunks_LargeDuration()
    {
        var result = CallGenerateFixedChunks(7200, 30);
        Assert.Equal(240, result.Count);
    }

    // ── MergeForeignChunks additional ──

    private static List<(double Start, double End, string Language)> CallMergeForeignChunks(
        List<(double Start, double End, string Language)> chunks)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "MergeForeignChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (List<(double Start, double End, string Language)>)method!.Invoke(null, new object[] { chunks })!;
    }

    [Fact]
    public void MergeForeignChunks_SingleChunk_ReturnsSame()
    {
        var chunks = new List<(double, double, string)> { (10, 20, "fr") };
        var result = CallMergeForeignChunks(chunks);
        Assert.Single(result);
        Assert.Equal(10, result[0].Start);
        Assert.Equal(20, result[0].End);
        Assert.Equal("fr", result[0].Language);
    }

    [Fact]
    public void MergeForeignChunks_ExactlyAtThreshold_NotMerged()
    {
        // Gap is exactly 5.0s which is NOT < 5.0, so should NOT merge
        var chunks = new List<(double, double, string)>
        {
            (10, 20, "fr"),
            (25, 35, "fr"),
        };
        var result = CallMergeForeignChunks(chunks);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeForeignChunks_MixedLanguages()
    {
        var chunks = new List<(double, double, string)>
        {
            (10, 20, "fr"),
            (21, 30, "fr"),
            (31, 40, "de"),
            (41, 50, "de"),
            (55, 60, "ja"),
        };
        var result = CallMergeForeignChunks(chunks);
        Assert.Equal(3, result.Count);
        Assert.Equal("fr", result[0].Language);
        Assert.Equal("de", result[1].Language);
        Assert.Equal("ja", result[2].Language);
    }

    // ── ResolveLanguagesAsync ──

    [Fact]
    public async Task ResolveLanguagesAsync_SpecificLanguage_ReturnsSingleItem()
    {
        var manager = CreateManager();
        var result = await manager.ResolveLanguagesAsync("/fake/path.mkv", "es", CancellationToken.None);
        Assert.Single(result);
        Assert.Equal("es", result[0]);
    }

    [Fact]
    public async Task ResolveLanguagesAsync_SpecificLanguage_NotAuto()
    {
        var manager = CreateManager();
        var result = await manager.ResolveLanguagesAsync("/fake/path.mkv", "fr", CancellationToken.None);
        Assert.Single(result);
        Assert.Equal("fr", result[0]);
    }
}
