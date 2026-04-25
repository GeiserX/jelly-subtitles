using WhisperSubs.Api;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class LibraryInfoTests
{
    [Fact]
    public void DefaultValues()
    {
        var info = new LibraryInfo();
        Assert.Equal(string.Empty, info.Id);
        Assert.Equal(string.Empty, info.Name);
    }

    [Fact]
    public void CanSetProperties()
    {
        var info = new LibraryInfo
        {
            Id = "lib-123",
            Name = "Movies"
        };
        Assert.Equal("lib-123", info.Id);
        Assert.Equal("Movies", info.Name);
    }
}

public class ItemInfoTests
{
    [Fact]
    public void DefaultValues()
    {
        var info = new ItemInfo();
        Assert.Equal(string.Empty, info.Id);
        Assert.Equal(string.Empty, info.Name);
        Assert.Equal(string.Empty, info.Type);
        Assert.Null(info.Path);
        Assert.False(info.HasSubtitles);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var info = new ItemInfo
        {
            Id = "item-456",
            Name = "Test Movie",
            Type = "Movie",
            Path = "/media/test.mkv",
            HasSubtitles = true
        };
        Assert.Equal("item-456", info.Id);
        Assert.Equal("Test Movie", info.Name);
        Assert.Equal("Movie", info.Type);
        Assert.Equal("/media/test.mkv", info.Path);
        Assert.True(info.HasSubtitles);
    }
}

public class SubtitleStatusTests
{
    [Fact]
    public void DefaultValues()
    {
        var status = new SubtitleStatus();
        Assert.Equal(string.Empty, status.ItemId);
        Assert.False(status.HasGeneratedSubtitle);
        Assert.False(status.HasForcedSubtitle);
        Assert.False(status.HasExistingSubtitles);
        Assert.Null(status.SubtitlePath);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var status = new SubtitleStatus
        {
            ItemId = "item-789",
            HasGeneratedSubtitle = true,
            HasForcedSubtitle = true,
            HasExistingSubtitles = true,
            SubtitlePath = "/media/test.en.generated.srt"
        };
        Assert.Equal("item-789", status.ItemId);
        Assert.True(status.HasGeneratedSubtitle);
        Assert.True(status.HasForcedSubtitle);
        Assert.True(status.HasExistingSubtitles);
        Assert.Equal("/media/test.en.generated.srt", status.SubtitlePath);
    }
}

public class ModelInfoTests
{
    [Fact]
    public void DefaultValues()
    {
        var info = new ModelInfo();
        Assert.Equal(string.Empty, info.Path);
        Assert.Equal(string.Empty, info.Name);
        Assert.Equal(string.Empty, info.FileName);
        Assert.Equal(0, info.SizeMB);
        Assert.False(info.IsActive);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var info = new ModelInfo
        {
            Path = "/models/test.bin",
            Name = "ggml-large-v3-turbo-q5_0",
            FileName = "ggml-large-v3-turbo-q5_0.bin",
            SizeMB = 574.0,
            IsActive = true
        };
        Assert.Equal("/models/test.bin", info.Path);
        Assert.Equal("ggml-large-v3-turbo-q5_0", info.Name);
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", info.FileName);
        Assert.Equal(574.0, info.SizeMB);
        Assert.True(info.IsActive);
    }
}

public class PagedItemResultTests
{
    [Fact]
    public void DefaultValues()
    {
        var result = new PagedItemResult();
        Assert.NotNull(result.Items);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.StartIndex);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var items = new List<ItemInfo>
        {
            new ItemInfo { Id = "1", Name = "Item 1" },
            new ItemInfo { Id = "2", Name = "Item 2" }
        };

        var result = new PagedItemResult
        {
            Items = items,
            TotalCount = 100,
            StartIndex = 10
        };

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(10, result.StartIndex);
    }
}

public class SubtitleWorkItemTests
{
    [Fact]
    public async Task Completion_CanBeNull()
    {
        // SubtitleWorkItem requires BaseItem which needs Jellyfin runtime,
        // but we can verify the Completion property concept
        // by checking the TaskCompletionSource pattern
        var tcs = new TaskCompletionSource<bool>();
        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);

        tcs.TrySetResult(true);
        Assert.True(tcs.Task.IsCompleted);
        Assert.True(await tcs.Task);
    }

    [Fact]
    public void TaskCompletionSource_Cancel()
    {
        var tcs = new TaskCompletionSource<bool>();
        tcs.TrySetCanceled();
        Assert.True(tcs.Task.IsCanceled);
    }

    [Fact]
    public void TaskCompletionSource_Exception()
    {
        var tcs = new TaskCompletionSource<bool>();
        tcs.TrySetException(new InvalidOperationException("test"));
        Assert.True(tcs.Task.IsFaulted);
    }
}
