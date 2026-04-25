using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void Models_IsNotEmpty()
    {
        Assert.NotEmpty(ModelCatalog.Models);
    }

    [Fact]
    public void Models_HasExpectedCount()
    {
        Assert.Equal(7, ModelCatalog.Models.Length);
    }

    [Fact]
    public void Models_ExactlyOneRecommended()
    {
        var recommended = ModelCatalog.Models.Where(m => m.IsRecommended).ToList();
        Assert.Single(recommended);
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", recommended[0].FileName);
    }

    [Fact]
    public void Models_AllHaveNonEmptyProperties()
    {
        foreach (var model in ModelCatalog.Models)
        {
            Assert.False(string.IsNullOrEmpty(model.FileName));
            Assert.False(string.IsNullOrEmpty(model.DisplayName));
            Assert.False(string.IsNullOrEmpty(model.Description));
            Assert.True(model.SizeMB > 0);
        }
    }

    [Fact]
    public void Models_AllFileNamesEndWithBin()
    {
        foreach (var model in ModelCatalog.Models)
        {
            Assert.EndsWith(".bin", model.FileName);
        }
    }

    [Fact]
    public void HuggingFaceBaseUrl_IsValid()
    {
        Assert.False(string.IsNullOrEmpty(ModelCatalog.HuggingFaceBaseUrl));
        Assert.StartsWith("https://", ModelCatalog.HuggingFaceBaseUrl);
        Assert.Contains("huggingface.co", ModelCatalog.HuggingFaceBaseUrl);
    }

    [Fact]
    public void Models_SortedBySize_TinyIsSmallest()
    {
        var tiny = ModelCatalog.Models.First(m => m.FileName.Contains("tiny"));
        var largest = ModelCatalog.Models.OrderByDescending(m => m.SizeMB).First();
        Assert.True(tiny.SizeMB < largest.SizeMB);
    }
}

public class ModelEntryTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var entry = new ModelEntry("test.bin", "Test Model", 100, true, "A test model");

        Assert.Equal("test.bin", entry.FileName);
        Assert.Equal("Test Model", entry.DisplayName);
        Assert.Equal(100, entry.SizeMB);
        Assert.True(entry.IsRecommended);
        Assert.Equal("A test model", entry.Description);
    }

    [Fact]
    public void Constructor_NotRecommended()
    {
        var entry = new ModelEntry("other.bin", "Other", 50, false, "Desc");
        Assert.False(entry.IsRecommended);
    }
}
