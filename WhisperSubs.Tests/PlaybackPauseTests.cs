using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

public class PlaybackPauseTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly ILogger<SubtitleGenerationTask> _logger = NullLogger<SubtitleGenerationTask>.Instance;
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    private SubtitleGenerationTask CreateTask() =>
        new(_libraryManager.Object, _sessionManager.Object, _logger, _loggerFactory);

    [Fact]
    public async Task WaitForPlaybackIdle_NoActiveSessions_ReturnsImmediately()
    {
        _sessionManager.Setup(s => s.Sessions)
            .Returns(Array.Empty<SessionInfo>());

        var task = CreateTask();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await task.WaitForPlaybackIdleAsync(cts.Token);
        // Should not throw or timeout
    }

    [Fact]
    public async Task WaitForPlaybackIdle_NoPlayingItem_ReturnsImmediately()
    {
        var session = new SessionInfo(null!, null!) { NowPlayingItem = null };
        _sessionManager.Setup(s => s.Sessions)
            .Returns(new[] { session });

        var task = CreateTask();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await task.WaitForPlaybackIdleAsync(cts.Token);
    }

    [Fact]
    public async Task WaitForPlaybackIdle_ActivePlayback_WaitsUntilStopped()
    {
        var playingItem = new BaseItemDto { Name = "Test Movie" };
        var session = new SessionInfo(null!, null!) { NowPlayingItem = playingItem };

        int callCount = 0;
        _sessionManager.Setup(s => s.Sessions)
            .Returns(() =>
            {
                callCount++;
                if (callCount >= 2)
                    session.NowPlayingItem = null;
                return new[] { session };
            });

        var task = CreateTask();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await task.WaitForPlaybackIdleAsync(cts.Token);

        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task WaitForPlaybackIdle_CancellationToken_ThrowsOnCancel()
    {
        var playingItem = new BaseItemDto { Name = "Test Movie" };
        var session = new SessionInfo(null!, null!) { NowPlayingItem = playingItem };
        _sessionManager.Setup(s => s.Sessions)
            .Returns(new[] { session });

        var task = CreateTask();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitForPlaybackIdleAsync(cts.Token));
    }

    [Fact]
    public async Task WaitForPlaybackIdle_EmptySessions_ReturnsImmediately()
    {
        _sessionManager.Setup(s => s.Sessions)
            .Returns(new List<SessionInfo>());

        var task = CreateTask();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await task.WaitForPlaybackIdleAsync(cts.Token);
    }
}
