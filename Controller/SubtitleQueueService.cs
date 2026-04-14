using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    public class SubtitleWorkItem
    {
        public required BaseItem Item { get; init; }
        public required string Language { get; init; }
        public TaskCompletionSource<bool>? Completion { get; init; }
    }

    public class QueueEntry
    {
        public string ItemId { get; set; } = "";
        public string Language { get; set; } = "";
    }

    public class SubtitleQueueService
    {
        private static SubtitleQueueService? _instance;
        public static SubtitleQueueService Instance => _instance ??= new SubtitleQueueService();

        private readonly ConcurrentQueue<SubtitleWorkItem> _priorityQueue = new();
        private int _isDraining;
        private string? _currentItemName;
        private int _processedCount;
        private static readonly object _fileLock = new();

        /// <summary>
        /// Global lock — only one whisper transcription at a time.
        /// Must be acquired by both the drain loop and the scheduled task.
        /// </summary>
        public static readonly SemaphoreSlim TranscriptionLock = new(1, 1);

        // ── Scheduled task progress tracking ─────────────────────
        private string? _taskCurrentItemName;
        private int _taskTotal;
        private int _taskProcessed;
        private int _taskFailed;
        private int _taskIsRunning;

        public int PriorityCount => _priorityQueue.Count;
        public string? CurrentItemName => _currentItemName ?? _taskCurrentItemName;
        public bool IsDraining => _isDraining == 1;
        public int ProcessedCount => _processedCount;

        // ── Per-file progress (updated by WhisperProvider stderr) ──
        private int _currentFileProgress;

        /// <summary>Current file transcription progress (0-100), parsed from whisper stderr.</summary>
        public int CurrentFileProgress => _currentFileProgress;

        /// <summary>Updates the current file's transcription progress.</summary>
        public void ReportFileProgress(int percent)
        {
            Interlocked.Exchange(ref _currentFileProgress, Math.Clamp(percent, 0, 100));
        }

        /// <summary>Resets file progress to 0 (call when starting a new item).</summary>
        public void ResetFileProgress()
        {
            Interlocked.Exchange(ref _currentFileProgress, 0);
        }

        /// <summary>Whether the scheduled auto-generation task is running.</summary>
        public bool IsTaskRunning => _taskIsRunning == 1;
        private string? _taskCurrentItemType;
        private string? _taskCurrentItemLibrary;
        private string? _currentPhase;

        public string? TaskCurrentItemName => _taskCurrentItemName;
        public string? TaskCurrentItemType => _taskCurrentItemType;
        public string? TaskCurrentItemLibrary => _taskCurrentItemLibrary;
        public string? CurrentPhase => _currentPhase;
        public int TaskTotal => _taskTotal;
        public int TaskProcessed => _taskProcessed;
        public int TaskFailed => _taskFailed;

        /// <summary>Reports progress from the scheduled task so the Queue endpoint can expose it.</summary>
        public void ReportTaskProgress(string? itemName, int processed, int total, int failed,
            string? itemType = null, string? libraryName = null)
        {
            _taskCurrentItemName = itemName;
            _taskProcessed = processed;
            _taskTotal = total;
            _taskFailed = failed;
            _taskCurrentItemType = itemType;
            _taskCurrentItemLibrary = libraryName;
            Interlocked.CompareExchange(ref _taskIsRunning, 1, 0);
        }

        /// <summary>Reports the current processing phase (e.g. "Extracting audio", "Transcribing").</summary>
        public void ReportPhase(string phase)
        {
            _currentPhase = phase;
        }

        /// <summary>Marks the scheduled task as complete.</summary>
        public void ReportTaskComplete()
        {
            _taskCurrentItemName = null;
            _taskCurrentItemType = null;
            _taskCurrentItemLibrary = null;
            _currentPhase = null;
            Interlocked.Exchange(ref _taskIsRunning, 0);
        }

        private static string QueueFilePath
        {
            get
            {
                var pluginDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(pluginDir)) return "";
                Directory.CreateDirectory(pluginDir);
                return Path.Combine(pluginDir, "queue.json");
            }
        }

        public void Enqueue(BaseItem item, string language)
        {
            _priorityQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = null
            });
            PersistQueue();
        }

        public Task EnqueuePriorityAsync(BaseItem item, string language)
        {
            var tcs = new TaskCompletionSource<bool>();
            _priorityQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = tcs
            });
            PersistQueue();
            return tcs.Task;
        }

        public bool TryDequeuePriority(out SubtitleWorkItem? item)
        {
            var result = _priorityQueue.TryDequeue(out item);
            if (result) PersistQueue();
            return result;
        }

        /// <summary>
        /// Restores queue from disk on startup. Call after Jellyfin library is available.
        /// </summary>
        public int RestoreQueue(ILibraryManager libraryManager, ILogger logger)
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            try
            {
                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<QueueEntry>>(json);
                if (entries == null || entries.Count == 0) return 0;

                int restored = 0;
                foreach (var entry in entries)
                {
                    if (!Guid.TryParse(entry.ItemId, out var guid)) continue;
                    var item = libraryManager.GetItemById(guid);
                    if (item == null) continue;

                    _priorityQueue.Enqueue(new SubtitleWorkItem
                    {
                        Item = item,
                        Language = entry.Language,
                        Completion = null
                    });
                    restored++;
                }

                logger.LogInformation("[Queue] Restored {Count} items from disk (of {Total} saved)", restored, entries.Count);
                return restored;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Queue] Failed to restore queue from {Path}", path);
                return 0;
            }
        }

        private void PersistQueue()
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var entries = _priorityQueue.Select(item => new QueueEntry
                {
                    ItemId = item.Item.Id.ToString("N"),
                    Language = item.Language
                }).ToList();

                var json = JsonSerializer.Serialize(entries);
                lock (_fileLock)
                {
                    File.WriteAllText(path, json);
                }
            }
            catch
            {
                // Non-critical — best effort persistence
            }
        }

        /// <summary>
        /// Starts the background drain loop if not already running.
        /// Safe to call multiple times — only one drain runs at a time.
        /// Re-checks queue after draining to avoid race with late enqueues.
        /// </summary>
        public void EnsureDraining(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _isDraining, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DrainLoopAsync(manager, provider, logger, cancellationToken);
                    }
                    finally
                    {
                        _currentItemName = null;
                        Interlocked.Exchange(ref _isDraining, 0);

                        // Re-check: if items were enqueued during the finally block,
                        // restart the drain to avoid stuck items.
                        if (!_priorityQueue.IsEmpty)
                        {
                            EnsureDraining(manager, provider, logger, cancellationToken);
                        }
                    }
                }, cancellationToken);
            }
        }

        private async Task DrainLoopAsync(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            while (TryDequeuePriority(out var workItem) && workItem != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _currentItemName = workItem.Item.Name;
                    logger.LogInformation("[Queue] Processing {ItemName} ({Remaining} remaining)",
                        workItem.Item.Name, _priorityQueue.Count);

                    await TranscriptionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await manager.GenerateSubtitleAsync(
                            workItem.Item, provider, workItem.Language, cancellationToken);
                    }
                    finally
                    {
                        TranscriptionLock.Release();
                    }

                    Interlocked.Increment(ref _processedCount);
                    workItem.Completion?.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    workItem.Completion?.TrySetCanceled();
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _processedCount);
                    workItem.Completion?.TrySetException(ex);
                    logger.LogError(ex, "[Queue] Failed: {ItemName}", workItem.Item.Name);
                }
            }
            logger.LogInformation("[Queue] Drain complete. Processed {Count} items total.", _processedCount);
        }

        /// <summary>
        /// Process all pending priority items. Called by scheduled task.
        /// </summary>
        public async Task DrainPriorityAsync(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            while (TryDequeuePriority(out var workItem) && workItem != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _currentItemName = workItem.Item.Name;
                    logger.LogInformation("[Priority] Processing {ItemName}", workItem.Item.Name);

                    await TranscriptionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await manager.GenerateSubtitleAsync(
                            workItem.Item, provider, workItem.Language, cancellationToken);
                    }
                    finally
                    {
                        TranscriptionLock.Release();
                    }

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
                    logger.LogError(ex, "[Priority] Failed: {ItemName}", workItem.Item.Name);
                }
            }
            _currentItemName = null;
        }
    }
}
