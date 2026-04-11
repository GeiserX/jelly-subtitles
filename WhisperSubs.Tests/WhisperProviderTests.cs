using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class WhisperProviderTests
{
    [Fact]
    public void ParseLastSrtTimestamp_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, WhisperProvider.ParseLastSrtTimestamp(""));
        Assert.Equal(0, WhisperProvider.ParseLastSrtTimestamp(null!));
        Assert.Equal(0, WhisperProvider.ParseLastSrtTimestamp("   "));
    }

    [Fact]
    public void ParseLastSrtTimestamp_SingleEntry_ReturnsEndTimestamp()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,500\nHello world\n";
        var result = WhisperProvider.ParseLastSrtTimestamp(srt);
        Assert.Equal(5.5, result, precision: 1);
    }

    [Fact]
    public void ParseLastSrtTimestamp_MultipleEntries_ReturnsLastEndTimestamp()
    {
        var srt = """
            1
            00:00:01,000 --> 00:00:05,000
            First line

            2
            00:01:30,000 --> 00:02:15,750
            Second line

            3
            01:23:45,123 --> 01:24:00,999
            Last line
            """;
        var result = WhisperProvider.ParseLastSrtTimestamp(srt);
        // 1*3600 + 24*60 + 0 + 0.999 = 5040.999
        Assert.Equal(5040.999, result, precision: 3);
    }

    [Fact]
    public void CountSrtEntries_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, WhisperProvider.CountSrtEntries(""));
        Assert.Equal(0, WhisperProvider.CountSrtEntries(null!));
    }

    [Fact]
    public void CountSrtEntries_MultipleEntries_ReturnsCorrectCount()
    {
        var srt = """
            1
            00:00:01,000 --> 00:00:05,000
            First

            2
            00:00:06,000 --> 00:00:10,000
            Second

            3
            00:00:11,000 --> 00:00:15,000
            Third
            """;
        Assert.Equal(3, WhisperProvider.CountSrtEntries(srt));
    }

    [Fact]
    public void OffsetSrt_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", WhisperProvider.OffsetSrt("", 10, 1));
        Assert.Equal("", WhisperProvider.OffsetSrt(null!, 10, 1));
    }

    [Fact]
    public void OffsetSrt_AppliesOffsetAndRenumbers()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,000\nHello\n";
        var result = WhisperProvider.OffsetSrt(srt, 60.0, 10);

        Assert.Contains("10", result);
        Assert.Contains("00:01:01,000 --> 00:01:05,000", result);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void OffsetSrt_NegativeOffset_ClampsToZero()
    {
        var srt = "1\n00:00:02,000 --> 00:00:05,000\nTest\n";
        var result = WhisperProvider.OffsetSrt(srt, -10.0, 1);

        // 2s - 10s = -8s, clamped to 0
        Assert.Contains("00:00:00,000", result);
    }

    [Fact]
    public void OffsetSrt_LargeOffset_HandlesHoursCorrectly()
    {
        var srt = "1\n00:59:30,000 --> 00:59:59,000\nAlmost an hour\n";
        var result = WhisperProvider.OffsetSrt(srt, 60.0, 1);

        Assert.Contains("01:00:30,000 --> 01:00:59,000", result);
    }

    [Fact]
    public void OffsetSrt_MultipleEntries_RenumbersSequentially()
    {
        var srt = "1\n00:00:01,000 --> 00:00:02,000\nA\n\n2\n00:00:03,000 --> 00:00:04,000\nB\n";
        var result = WhisperProvider.OffsetSrt(srt, 0, 5);

        Assert.Contains("5\n", result);
        Assert.Contains("6\n", result);
    }
}
