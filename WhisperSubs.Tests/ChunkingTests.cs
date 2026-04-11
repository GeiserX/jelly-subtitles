using System.Reflection;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Tests for the VAD chunking and merging helpers in SubtitleManager.
/// These are private/static methods accessed via reflection for thorough unit testing.
/// </summary>
public class ChunkingTests
{
    private static List<(double Start, double End)> CallGroupSpeechIntoChunks(
        List<(double Start, double End)> segments, double target = 30.0)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "GroupSpeechIntoChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (List<(double Start, double End)>)method!.Invoke(null, new object[] { segments, target })!;
    }

    private static List<(double Start, double End)> CallGenerateFixedChunks(
        double totalDuration, double chunkDuration)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "GenerateFixedChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (List<(double Start, double End)>)method!.Invoke(null, new object[] { totalDuration, chunkDuration })!;
    }

    private static List<(double Start, double End, string Language)> CallMergeForeignChunks(
        List<(double Start, double End, string Language)> chunks)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "MergeForeignChunks",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (List<(double Start, double End, string Language)>)method!.Invoke(null, new object[] { chunks })!;
    }

    [Fact]
    public void GroupSpeechIntoChunks_EmptyInput_ReturnsEmpty()
    {
        var result = CallGroupSpeechIntoChunks(new List<(double, double)>());
        Assert.Empty(result);
    }

    [Fact]
    public void GroupSpeechIntoChunks_SingleSegment_ReturnsSingleChunk()
    {
        var segments = new List<(double, double)> { (0, 10) };
        var result = CallGroupSpeechIntoChunks(segments);
        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(10, result[0].End);
    }

    [Fact]
    public void GroupSpeechIntoChunks_ShortSegments_GroupedTogether()
    {
        var segments = new List<(double, double)>
        {
            (0, 5), (6, 10), (12, 18), (20, 25)
        };
        var result = CallGroupSpeechIntoChunks(segments, 30.0);
        Assert.Single(result);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(25, result[0].End);
    }

    [Fact]
    public void GroupSpeechIntoChunks_LongSegments_SplitAtBoundaries()
    {
        var segments = new List<(double, double)>
        {
            (0, 10), (15, 25), (35, 45), (50, 60)
        };
        var result = CallGroupSpeechIntoChunks(segments, 30.0);
        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Start);
        Assert.Equal(25, result[0].End);
        Assert.Equal(35, result[1].Start);
        Assert.Equal(60, result[1].End);
    }

    [Fact]
    public void GenerateFixedChunks_ProducesCorrectChunks()
    {
        var result = CallGenerateFixedChunks(100, 30);
        Assert.Equal(4, result.Count);
        Assert.Equal((0, 30.0), result[0]);
        Assert.Equal((30, 60.0), result[1]);
        Assert.Equal((60, 90.0), result[2]);
        Assert.Equal((90, 100.0), result[3]);
    }

    [Fact]
    public void GenerateFixedChunks_ExactMultiple_NoPartialChunk()
    {
        var result = CallGenerateFixedChunks(90, 30);
        Assert.Equal(3, result.Count);
        Assert.Equal(90.0, result[2].End);
    }

    [Fact]
    public void GenerateFixedChunks_ZeroDuration_ReturnsEmpty()
    {
        var result = CallGenerateFixedChunks(0, 30);
        Assert.Empty(result);
    }

    [Fact]
    public void MergeForeignChunks_EmptyInput_ReturnsEmpty()
    {
        var result = CallMergeForeignChunks(new List<(double, double, string)>());
        Assert.Empty(result);
    }

    [Fact]
    public void MergeForeignChunks_SameLanguageCloseGap_Merged()
    {
        var chunks = new List<(double, double, string)>
        {
            (10, 20, "fr"),
            (22, 30, "fr"),  // gap of 2s < 5s threshold
        };
        var result = CallMergeForeignChunks(chunks);
        Assert.Single(result);
        Assert.Equal(10, result[0].Start);
        Assert.Equal(30, result[0].End);
        Assert.Equal("fr", result[0].Language);
    }

    [Fact]
    public void MergeForeignChunks_SameLanguageLargeGap_NotMerged()
    {
        var chunks = new List<(double, double, string)>
        {
            (10, 20, "fr"),
            (26, 35, "fr"),  // gap of 6s > 5s threshold
        };
        var result = CallMergeForeignChunks(chunks);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeForeignChunks_DifferentLanguages_NotMerged()
    {
        var chunks = new List<(double, double, string)>
        {
            (10, 20, "fr"),
            (21, 30, "de"),
        };
        var result = CallMergeForeignChunks(chunks);
        Assert.Equal(2, result.Count);
        Assert.Equal("fr", result[0].Language);
        Assert.Equal("de", result[1].Language);
    }

    [Fact]
    public void MergeForeignChunks_MultipleConsecutiveSameLanguage_AllMerged()
    {
        var chunks = new List<(double, double, string)>
        {
            (10, 15, "ja"),
            (16, 20, "ja"),
            (21, 25, "ja"),
            (26, 30, "ja"),
        };
        var result = CallMergeForeignChunks(chunks);
        Assert.Single(result);
        Assert.Equal(10, result[0].Start);
        Assert.Equal(30, result[0].End);
    }
}
