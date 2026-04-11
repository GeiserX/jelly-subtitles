using WhisperSubs.Configuration;
using Xunit;

namespace WhisperSubs.Tests;

public class ConfigurationTests
{
    [Fact]
    public void PluginConfiguration_DefaultValues()
    {
        var config = new PluginConfiguration();

        Assert.Equal("Whisper", config.SelectedProvider);
        Assert.Equal("", config.WhisperModelPath);
        Assert.Equal("", config.WhisperBinaryPath);
        Assert.False(config.EnableAutoGeneration);
        Assert.Equal("auto", config.DefaultLanguage);
        Assert.Equal(SubtitleMode.Full, config.SubtitleMode);
        Assert.False(config.EnableLyricsGeneration);
        Assert.Equal(0, config.WhisperThreadCount);
        Assert.NotNull(config.EnabledLibraries);
        Assert.Empty(config.EnabledLibraries);
    }

    [Fact]
    public void SubtitleMode_HasExpectedValues()
    {
        Assert.Equal(0, (int)SubtitleMode.Full);
        Assert.Equal(1, (int)SubtitleMode.ForcedOnly);
        Assert.Equal(2, (int)SubtitleMode.FullAndForced);
    }

    [Fact]
    public void SubtitleMode_AllValuesAreDefined()
    {
        var values = Enum.GetValues<SubtitleMode>();
        Assert.Equal(3, values.Length);
    }
}
