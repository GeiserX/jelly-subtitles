using System.Text.Json;
using WhisperSubs.Configuration;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Extended tests for SubtitleModeConverter covering Read and Write paths.
/// </summary>
public class SubtitleModeConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new SubtitleModeConverter() }
    };

    // ── Read (deserialization) ──

    [Theory]
    [InlineData("0", SubtitleMode.Full)]
    [InlineData("1", SubtitleMode.ForcedOnly)]
    [InlineData("2", SubtitleMode.FullAndForced)]
    public void Read_ValidIntegers(string value, SubtitleMode expected)
    {
        var json = value;
        var result = JsonSerializer.Deserialize<SubtitleMode>(json, Options);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Read_Null_ReturnsFull()
    {
        var json = "null";
        var result = JsonSerializer.Deserialize<SubtitleMode>(json, Options);
        Assert.Equal(SubtitleMode.Full, result);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("100")]
    public void Read_OutOfRangeInteger_ReturnsFull(string value)
    {
        var result = JsonSerializer.Deserialize<SubtitleMode>(value, Options);
        Assert.Equal(SubtitleMode.Full, result);
    }

    [Theory]
    [InlineData("\"Full\"", SubtitleMode.Full)]
    [InlineData("\"ForcedOnly\"", SubtitleMode.ForcedOnly)]
    [InlineData("\"FullAndForced\"", SubtitleMode.FullAndForced)]
    [InlineData("\"full\"", SubtitleMode.Full)]
    [InlineData("\"forcedonly\"", SubtitleMode.ForcedOnly)]
    public void Read_ValidStrings(string json, SubtitleMode expected)
    {
        var result = JsonSerializer.Deserialize<SubtitleMode>(json, Options);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Read_InvalidString_ReturnsFull()
    {
        var json = "\"NotAMode\"";
        var result = JsonSerializer.Deserialize<SubtitleMode>(json, Options);
        Assert.Equal(SubtitleMode.Full, result);
    }

    [Fact]
    public void Read_Boolean_ReturnsFull()
    {
        // Booleans are an unsupported token type, should fall through to default
        var json = "true";
        var result = JsonSerializer.Deserialize<SubtitleMode>(json, Options);
        Assert.Equal(SubtitleMode.Full, result);
    }

    // ── Write (serialization) ──

    [Theory]
    [InlineData(SubtitleMode.Full, "0")]
    [InlineData(SubtitleMode.ForcedOnly, "1")]
    [InlineData(SubtitleMode.FullAndForced, "2")]
    public void Write_ProducesInteger(SubtitleMode mode, string expected)
    {
        var json = JsonSerializer.Serialize(mode, Options);
        Assert.Equal(expected, json);
    }

    // ── Roundtrip ──

    [Theory]
    [InlineData(SubtitleMode.Full)]
    [InlineData(SubtitleMode.ForcedOnly)]
    [InlineData(SubtitleMode.FullAndForced)]
    public void Roundtrip_AllValues(SubtitleMode original)
    {
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<SubtitleMode>(json, Options);
        Assert.Equal(original, deserialized);
    }

    // ── HandleNull property ──

    [Fact]
    public void HandleNull_IsTrue()
    {
        var converter = new SubtitleModeConverter();
        Assert.True(converter.HandleNull);
    }

    // ── Full PluginConfiguration roundtrip ──

    [Fact]
    public void PluginConfiguration_Roundtrip_PreservesSubtitleMode()
    {
        var config = new PluginConfiguration
        {
            SubtitleMode = SubtitleMode.FullAndForced,
            DefaultLanguage = "es",
            EnableAutoGeneration = true
        };

        var json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<PluginConfiguration>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(SubtitleMode.FullAndForced, deserialized!.SubtitleMode);
        Assert.Equal("es", deserialized.DefaultLanguage);
        Assert.True(deserialized.EnableAutoGeneration);
    }

    [Fact]
    public void PluginConfiguration_NullSubtitleMode_DefaultsToFull()
    {
        var json = """
        {
            "WhisperModelPath": "/test/model.bin",
            "SubtitleMode": null,
            "DefaultLanguage": "en"
        }
        """;

        var config = JsonSerializer.Deserialize<PluginConfiguration>(json);
        Assert.NotNull(config);
        Assert.Equal(SubtitleMode.Full, config!.SubtitleMode);
        Assert.Equal("/test/model.bin", config.WhisperModelPath);
    }

    [Fact]
    public void PluginConfiguration_MissingSubtitleMode_DefaultsToFull()
    {
        var json = """{"DefaultLanguage": "fr"}""";
        var config = JsonSerializer.Deserialize<PluginConfiguration>(json);
        Assert.NotNull(config);
        Assert.Equal(SubtitleMode.Full, config!.SubtitleMode);
    }

    [Fact]
    public void PluginConfiguration_AllPropertiesRoundtrip()
    {
        var config = new PluginConfiguration
        {
            WhisperModelPath = "/models/test.bin",
            WhisperBinaryPath = "/bin/whisper-cli",
            EnableAutoGeneration = true,
            DefaultLanguage = "fr",
            SubtitleMode = SubtitleMode.ForcedOnly,
            EnableLyricsGeneration = true,
            EnableTranslation = true,
            WhisperThreadCount = 8,
            EnabledLibraries = new List<string> { "lib1", "lib2" }
        };

        var json = JsonSerializer.Serialize(config);
        var result = JsonSerializer.Deserialize<PluginConfiguration>(json);

        Assert.NotNull(result);
        Assert.Equal("/models/test.bin", result!.WhisperModelPath);
        Assert.Equal("/bin/whisper-cli", result.WhisperBinaryPath);
        Assert.True(result.EnableAutoGeneration);
        Assert.Equal("fr", result.DefaultLanguage);
        Assert.Equal(SubtitleMode.ForcedOnly, result.SubtitleMode);
        Assert.True(result.EnableLyricsGeneration);
        Assert.True(result.EnableTranslation);
        Assert.Equal(8, result.WhisperThreadCount);
        Assert.Equal(2, result.EnabledLibraries.Count);
    }
}
