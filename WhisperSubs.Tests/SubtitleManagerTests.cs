using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class SubtitleManagerTests
{
    [Fact]
    public void ConvertSrtToLrc_EmptyInput_ReturnsHeaderOnly()
    {
        var result = SubtitleManager.ConvertSrtToLrc("", "Test Song");
        Assert.Contains("[ti:Test Song]", result);
        Assert.Contains("[by:WhisperSubs]", result);
    }

    [Fact]
    public void ConvertSrtToLrc_SingleEntry_ProducesCorrectLrc()
    {
        var srt = "1\n00:01:23,456 --> 00:01:25,789\nHello world\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt, "Track");

        Assert.Contains("[ti:Track]", result);
        Assert.Contains("[by:WhisperSubs]", result);
        Assert.Contains("[01:23.45]Hello world", result);
    }

    [Fact]
    public void ConvertSrtToLrc_MultipleEntries_AllConverted()
    {
        var srt = """
            1
            00:00:05,000 --> 00:00:10,000
            First line

            2
            00:00:15,500 --> 00:00:20,000
            Second line
            """;
        var result = SubtitleManager.ConvertSrtToLrc(srt);

        Assert.Contains("[00:05.00]First line", result);
        Assert.Contains("[00:15.50]Second line", result);
    }

    [Fact]
    public void ConvertSrtToLrc_HoursConvertedToMinutes()
    {
        // 01:30:45,678 = 90 minutes + 45 seconds
        var srt = "1\n01:30:45,678 --> 01:31:00,000\nLate entry\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);

        Assert.Contains("[90:45.67]Late entry", result);
    }

    [Fact]
    public void ConvertSrtToLrc_MultilineText_JoinedWithSpace()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,000\nLine one\nLine two\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);

        Assert.Contains("[00:01.00]Line one Line two", result);
    }

    [Fact]
    public void ConvertSrtToLrc_NullTitle_NoTitleTag()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,000\nHello\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt, null);

        Assert.DoesNotContain("[ti:", result);
        Assert.Contains("[by:WhisperSubs]", result);
    }

    [Fact]
    public void ConvertSrtToLrc_EmptyTextLines_Skipped()
    {
        var srt = "1\n00:00:01,000 --> 00:00:05,000\n   \n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);

        // The empty text line should not produce an LRC entry
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(lines, l => l.StartsWith("[00:01"));
    }

    [Fact]
    public void ConvertSrtToLrc_DotSeparator_AlsoWorks()
    {
        // Some SRT files use . instead of , for milliseconds
        var srt = "1\n00:00:01.500 --> 00:00:05.000\nDot format\n";
        var result = SubtitleManager.ConvertSrtToLrc(srt);

        Assert.Contains("[00:01.50]Dot format", result);
    }
}
