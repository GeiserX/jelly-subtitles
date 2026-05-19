using System.Diagnostics;
using WhisperSubs.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WhisperSubs.Tests;

public class CustomArgsTests
{
    private static WhisperProvider CreateProvider(string customArgs = "")
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
        return new WhisperProvider(logger, "/tmp/model.bin", "", 0, customArgs);
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        return new ProcessStartInfo { FileName = "whisper-cli", UseShellExecute = false };
    }

    [Fact]
    public void AppendCustomArgs_EmptyString_AddsNothing()
    {
        var provider = CreateProvider("");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_WhitespaceOnly_AddsNothing()
    {
        var provider = CreateProvider("   \t  ");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_ValidArgs_AllAppended()
    {
        var provider = CreateProvider("--max-len 47 --split-on-word");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Equal(new[] { "--max-len", "47", "--split-on-word" }, si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_DeniedFlag_IsSkipped()
    {
        var provider = CreateProvider("-m");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_DeniedFlagWithValue_BothSkipped()
    {
        var provider = CreateProvider("--model /evil/path --beam-size 5");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Equal(new[] { "--beam-size", "5" }, si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_DeniedFlagEqualsStyle_IsSkipped()
    {
        var provider = CreateProvider("--model=/evil.bin --beam-size 8");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Equal(new[] { "--beam-size", "8" }, si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_DeniedFlagCaseInsensitive()
    {
        var provider = CreateProvider("-M --MODEL --File");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_MixedValidAndDenied_OnlyValidPass()
    {
        var provider = CreateProvider("--max-len 47 -osrt --split-on-word -f /tmp/x");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Equal(new[] { "--max-len", "47", "--split-on-word" }, si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_DeniedFlagValueStartsWithDash_OnlyFlagSkipped()
    {
        // If the "value" starts with -, it's treated as its own flag, not a value
        var provider = CreateProvider("--translate --beam-size 5");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Equal(new[] { "--beam-size", "5" }, si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_NullCustomArgs_NoException()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
        var provider = new WhisperProvider(logger, "/tmp/model.bin", "", 0, null!);
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }
}
