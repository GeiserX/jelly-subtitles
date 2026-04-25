using System.Reflection;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Tests for the NormalizeLanguageCode method in SubtitleManager (ISO 639-2/3 to 639-1 mapping).
/// </summary>
public class NormalizeLanguageCodeTests
{
    private static string CallNormalizeLanguageCode(string code)
    {
        var method = typeof(SubtitleManager).GetMethod(
            "NormalizeLanguageCode",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { code })!;
    }

    [Theory]
    [InlineData("spa", "es")]
    [InlineData("eng", "en")]
    [InlineData("fra", "fr")]
    [InlineData("fre", "fr")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("ita", "it")]
    [InlineData("por", "pt")]
    [InlineData("rus", "ru")]
    [InlineData("jpn", "ja")]
    [InlineData("zho", "zh")]
    [InlineData("chi", "zh")]
    [InlineData("kor", "ko")]
    [InlineData("ara", "ar")]
    [InlineData("hin", "hi")]
    [InlineData("pol", "pl")]
    [InlineData("nld", "nl")]
    [InlineData("dut", "nl")]
    [InlineData("tur", "tr")]
    [InlineData("swe", "sv")]
    [InlineData("dan", "da")]
    [InlineData("fin", "fi")]
    [InlineData("nor", "no")]
    [InlineData("ces", "cs")]
    [InlineData("cze", "cs")]
    [InlineData("ron", "ro")]
    [InlineData("rum", "ro")]
    [InlineData("hun", "hu")]
    [InlineData("ell", "el")]
    [InlineData("gre", "el")]
    [InlineData("heb", "he")]
    [InlineData("tha", "th")]
    [InlineData("ukr", "uk")]
    [InlineData("vie", "vi")]
    [InlineData("ind", "id")]
    [InlineData("cat", "ca")]
    [InlineData("eus", "eu")]
    [InlineData("baq", "eu")]
    [InlineData("glg", "gl")]
    public void KnownThreeLetterCodes_MapToTwoLetterCodes(string input, string expected)
    {
        Assert.Equal(expected, CallNormalizeLanguageCode(input));
    }

    [Theory]
    [InlineData("SPA", "es")]
    [InlineData("Eng", "en")]
    [InlineData("FRA", "fr")]
    public void CaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, CallNormalizeLanguageCode(input));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("es", "es")]
    [InlineData("fr", "fr")]
    [InlineData("unknown", "unknown")]
    public void AlreadyShortOrUnknown_PassesThrough(string input, string expected)
    {
        Assert.Equal(expected, CallNormalizeLanguageCode(input));
    }
}

/// <summary>
/// Tests for the NormalizeLangName method in WhisperProvider (full language names to 639-1 codes).
/// </summary>
public class NormalizeLangNameTests
{
    private static string CallNormalizeLangName(string lang)
    {
        var method = typeof(WhisperProvider).GetMethod(
            "NormalizeLangName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { lang })!;
    }

    [Theory]
    [InlineData("english", "en")]
    [InlineData("spanish", "es")]
    [InlineData("french", "fr")]
    [InlineData("german", "de")]
    [InlineData("italian", "it")]
    [InlineData("portuguese", "pt")]
    [InlineData("russian", "ru")]
    [InlineData("japanese", "ja")]
    [InlineData("chinese", "zh")]
    [InlineData("korean", "ko")]
    [InlineData("arabic", "ar")]
    [InlineData("hindi", "hi")]
    [InlineData("dutch", "nl")]
    [InlineData("polish", "pl")]
    [InlineData("turkish", "tr")]
    [InlineData("swedish", "sv")]
    [InlineData("danish", "da")]
    [InlineData("finnish", "fi")]
    [InlineData("norwegian", "no")]
    [InlineData("czech", "cs")]
    [InlineData("romanian", "ro")]
    [InlineData("hungarian", "hu")]
    [InlineData("greek", "el")]
    [InlineData("hebrew", "he")]
    [InlineData("thai", "th")]
    [InlineData("ukrainian", "uk")]
    [InlineData("vietnamese", "vi")]
    [InlineData("indonesian", "id")]
    [InlineData("catalan", "ca")]
    [InlineData("basque", "eu")]
    [InlineData("galician", "gl")]
    [InlineData("haitian creole", "ht")]
    public void FullNames_MapToIsoCodes(string input, string expected)
    {
        Assert.Equal(expected, CallNormalizeLangName(input));
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData("SPANISH", "es")]
    [InlineData("French", "fr")]
    public void CaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, CallNormalizeLangName(input));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("es", "es")]
    [InlineData("xyz", "xyz")]
    public void ShortOrUnknown_PassesThrough(string input, string expected)
    {
        Assert.Equal(expected, CallNormalizeLangName(input));
    }
}

/// <summary>
/// Tests for the GetLanguagePrompt method in WhisperProvider.
/// </summary>
public class GetLanguagePromptTests
{
    private static string CallGetLanguagePrompt(string language)
    {
        var method = typeof(WhisperProvider).GetMethod(
            "GetLanguagePrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { language })!;
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("es", "español")]
    [InlineData("fr", "français")]
    [InlineData("de", "Deutsch")]
    [InlineData("it", "italiano")]
    [InlineData("pt", "português")]
    [InlineData("ja", "日本語")]
    [InlineData("zh", "中文")]
    [InlineData("ko", "한국어")]
    [InlineData("ru", "русском")]
    public void KnownLanguages_ReturnPrompt(string language, string expectedSubstring)
    {
        var result = CallGetLanguagePrompt(language);
        Assert.Contains(expectedSubstring, result);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("pl")]
    [InlineData("nl")]
    [InlineData("unknown")]
    public void UnknownLanguages_ReturnEmptyString(string language)
    {
        var result = CallGetLanguagePrompt(language);
        Assert.Equal("", result);
    }
}

/// <summary>
/// Tests for the OffsetTimestamp private method in WhisperProvider.
/// </summary>
public class OffsetTimestampTests
{
    private static string CallOffsetTimestamp(string timestamp, double offsetSeconds)
    {
        var method = typeof(WhisperProvider).GetMethod(
            "OffsetTimestamp",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { timestamp, offsetSeconds })!;
    }

    [Fact]
    public void ZeroOffset_ReturnsUnchanged()
    {
        Assert.Equal("00:01:30,500", CallOffsetTimestamp("00:01:30,500", 0));
    }

    [Fact]
    public void PositiveOffset_AddsTime()
    {
        Assert.Equal("00:02:00,000", CallOffsetTimestamp("00:01:00,000", 60.0));
    }

    [Fact]
    public void NegativeOffset_ClampsToZero()
    {
        Assert.Equal("00:00:00,000", CallOffsetTimestamp("00:00:05,000", -10.0));
    }

    [Fact]
    public void CrossesHourBoundary()
    {
        Assert.Equal("01:00:30,000", CallOffsetTimestamp("00:59:30,000", 60.0));
    }

    [Fact]
    public void InvalidFormat_ReturnsOriginal()
    {
        Assert.Equal("invalid", CallOffsetTimestamp("invalid", 10.0));
    }

    [Fact]
    public void MillisecondPrecision()
    {
        Assert.Equal("00:00:01,500", CallOffsetTimestamp("00:00:01,000", 0.5));
    }
}
