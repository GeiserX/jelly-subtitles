using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Controller
{
    public class SubtitleWorkItem
    {
        public required BaseItem Item { get; init; }
        public required string Language { get; init; }
        public TaskCompletionSource<bool>? Completion { get; init; }
    }

    /// <summary>
    /// Singleton service that serialises all subtitle generation through a single queue.
    /// Manual (UI) requests are placed in a high-priority queue that is drained first.
    /// </summary>
    public class SubtitleQueueService
    {
        private static SubtitleQueueService? _instance;
        public static SubtitleQueueService Instance => _instance ??= new SubtitleQueueService();

        private readonly ConcurrentQueue<SubtitleWorkItem> _priorityQueue = new();
        private readonly ConcurrentQueue<SubtitleWorkItem> _normalQueue = new();
        private readonly SemaphoreSlim _workerLock = new(1, 1);
        private int _isProcessing;

        /// <summary>
        /// Enqueue a manual (UI) request. These are processed before auto-generation items.
        /// Returns a Task that completes when the item has been processed.
        /// </summary>
        public Task EnqueuePriorityAsync(BaseItem item, string language)
        {
            var tcs = new TaskCompletionSource<bool>();
            _priorityQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = tcs
            });
            return tcs.Task;
        }

        /// <summary>
        /// Enqueue an auto-generation item (from the scheduled task).
        /// </summary>
        public void EnqueueNormal(BaseItem item, string language)
        {
            _normalQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language
            });
        }

        /// <summary>
        /// Dequeues the next item, prioritising manual requests.
        /// Returns null when both queues are empty.
        /// </summary>
        public SubtitleWorkItem? Dequeue()
        {
            if (_priorityQueue.TryDequeue(out var priority))
            {
                return priority;
            }

            if (_normalQueue.TryDequeue(out var normal))
            {
                return normal;
            }

            return null;
        }

        public int PriorityCount => _priorityQueue.Count;
        public int NormalCount => _normalQueue.Count;
        public int TotalCount => _priorityQueue.Count + _normalQueue.Count;

        /// <summary>
        /// Process all queued items. Called by both the scheduled task and the API.
        /// Only one worker runs at a time.
        /// </summary>
        public async Task ProcessQueueAsync(
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            {
                // Already processing — priority items will be picked up by the running worker
                return;
            }

            try
            {
                var manager = new SubtitleManager(libraryManager, loggerFactory.CreateLogger<SubtitleManager>());
                var config = Plugin.Instance.Configuration;
                var provider = new WhisperProvider(
                    loggerFactory.CreateLogger<WhisperProvider>(),
                    config.WhisperModelPath,
                    config.WhisperBinaryPath);

                var processed = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var workItem = Dequeue();
                    if (workItem == null) break;

                    try
                    {
                        await manager.GenerateSubtitleAsync(
                            workItem.Item, provider, workItem.Language, cancellationToken);
                        workItem.Completion?.TrySetResult(true);
                    }
                    catch (OperationCanceledException)
                    {
                        workItem.Completion?.TrySetCanceled();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        workItem.Completion?.TrySetException(ex);
                        loggerFactory.CreateLogger<SubtitleQueueService>()
                            .LogError(ex, "Failed to generate subtitle for {ItemName}", workItem.Item.Name);
                    }

                    processed++;
                    progress?.Report(TotalCount > 0
                        ? (double)processed / (processed + TotalCount) * 100
                        : 100);
                }

                progress?.Report(100);
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }
    }
}
