using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class BinaryCatalogTests
{
    [Fact]
    public void Variants_ContainsExpectedEntries()
    {
        Assert.Equal(5, BinaryCatalog.Variants.Length);
        Assert.Contains(BinaryCatalog.Variants, v => v.Id == "cpu");
        Assert.Contains(BinaryCatalog.Variants, v => v.Id == "noavx");
        Assert.Contains(BinaryCatalog.Variants, v => v.Id == "cuda12");
        Assert.Contains(BinaryCatalog.Variants, v => v.Id == "vulkan");
        Assert.Contains(BinaryCatalog.Variants, v => v.Id == "rocm");
    }

    [Fact]
    public void Variants_CpuIsDefault()
    {
        var cpu = Assert.Single(BinaryCatalog.Variants, v => v.Id == "cpu");
        Assert.True(cpu.IsDefault);
    }

    [Fact]
    public void Variants_NoavxIsNotDefault()
    {
        var noavx = Assert.Single(BinaryCatalog.Variants, v => v.Id == "noavx");
        Assert.Equal("CPU (Compatibility)", noavx.DisplayName);
        Assert.False(noavx.IsDefault);
    }

    [Fact]
    public void Variants_NonCpuAreNotDefault()
    {
        foreach (var v in BinaryCatalog.Variants.Where(v => v.Id != "cpu"))
        {
            Assert.False(v.IsDefault);
        }
    }

    [Theory]
    [InlineData("linux-x64", "cpu", "whisper-cli-linux-x64")]
    [InlineData("linux-x64", "noavx", "whisper-cli-linux-x64-noavx")]
    [InlineData("linux-x64", "cuda12", "whisper-cli-linux-x64-cuda12")]
    [InlineData("linux-x64", "vulkan", "whisper-cli-linux-x64-vulkan")]
    [InlineData("linux-x64", "rocm", "whisper-cli-linux-x64-rocm")]
    [InlineData("linux-arm64", "cpu", "whisper-cli-linux-arm64")]
    [InlineData("linux-arm64", "noavx", "whisper-cli-linux-arm64-noavx")]
    [InlineData("win-x64", "cpu", "whisper-cli-win-x64")]
    [InlineData("osx-arm64", "cpu", "whisper-cli-osx-arm64")]
    public void GetAssetName_ReturnsCorrectName(string platform, string variant, string expected)
    {
        Assert.Equal(expected, BinaryCatalog.GetAssetName(platform, variant));
    }

    [Fact]
    public void GetAvailableVariants_LinuxX64_ReturnsAll()
    {
        var variants = BinaryCatalog.GetAvailableVariants("linux-x64");
        Assert.Equal(5, variants.Length);
    }

    [Fact]
    public void GetAvailableVariants_LinuxArm64_ReturnsCpuAndNoavx()
    {
        var variants = BinaryCatalog.GetAvailableVariants("linux-arm64");
        Assert.Equal(2, variants.Length);
        Assert.Contains(variants, v => v.Id == "cpu");
        Assert.Contains(variants, v => v.Id == "noavx");
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    [InlineData("unknown")]
    public void GetAvailableVariants_UnsupportedPlatform_ReturnsEmpty(string platform)
    {
        var variants = BinaryCatalog.GetAvailableVariants(platform);
        Assert.Empty(variants);
    }
}

public class BinaryVariantTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var variant = new BinaryVariant("test-id", "Test Display", "Test description", true);

        Assert.Equal("test-id", variant.Id);
        Assert.Equal("Test Display", variant.DisplayName);
        Assert.Equal("Test description", variant.Description);
        Assert.True(variant.IsDefault);
    }

    [Fact]
    public void Constructor_NotDefault()
    {
        var variant = new BinaryVariant("other", "Other", "Desc", false);
        Assert.False(variant.IsDefault);
    }

    [Fact]
    public void AllVariants_HaveNonEmptyProperties()
    {
        foreach (var v in BinaryCatalog.Variants)
        {
            Assert.False(string.IsNullOrEmpty(v.Id));
            Assert.False(string.IsNullOrEmpty(v.DisplayName));
            Assert.False(string.IsNullOrEmpty(v.Description));
        }
    }
}
