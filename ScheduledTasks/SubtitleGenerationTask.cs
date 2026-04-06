using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.ScheduledTasks
{
    public class SubtitleGenerationTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SubtitleGenerationTask> _logger;

        public SubtitleGenerationTask(
            ILibraryManager libraryManager,
            ILogger<SubtitleGenerationTask> logger,
            ILoggerFactory loggerFactory)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public string Name => "Generate Subtitles";
        public string Key => "WhisperSubsGenerator";
        public string Description => "Scans enabled libraries and generates subtitles for items that lack them. Resumes automatically after restart.";
        public string Category => "WhisperSubs";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.StartupTrigger
                }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting subtitle generation task");

            var config = Plugin.Instance.Configuration;
            if (!config.EnableAutoGeneration)
            {
                _logger.LogInformation("Auto-generation is disabled in configuration");
                return;
            }

            if (string.IsNullOrEmpty(config.WhisperModelPath))
            {
                _logger.LogWarning("Whisper model path is not configured, aborting task");
                return;
            }

            var manager = new SubtitleManager(_libraryManager, _loggerFactory.CreateLogger<SubtitleManager>());
            var provider = new WhisperProvider(
                _loggerFactory.CreateLogger<WhisperProvider>(),
                config.WhisperModelPath,
                config.WhisperBinaryPath,
                config.WhisperThreadCount);
            var language = config.DefaultLanguage;
            var queue = SubtitleQueueService.Instance;

            // Restore persisted queue from disk (survives restarts)
            var restored = queue.RestoreQueue(_libraryManager, _logger);
            if (restored > 0)
            {
                _logger.LogInformation("Draining {Count} restored priority items before auto-generation", restored);
                await queue.DrainPriorityAsync(manager, provider, _logger, cancellationToken);
            }

            // Collect items — the query is fast (DB lookup), no bulk in-memory storage needed
            var enabledLibraryIds = config.EnabledLibraries
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => Guid.Parse(id))
                .ToList();

            if (enabledLibraryIds.Count == 0)
            {
                var allLibraries = _libraryManager.GetVirtualFolders();
                enabledLibraryIds = allLibraries
                    .Select(vf => Guid.Parse(vf.ItemId))
                    .ToList();
                _logger.LogInformation("No libraries explicitly enabled, scanning all {Count} libraries", enabledLibraryIds.Count);
            }

            var allItems = new List<Video>();
            foreach (var libraryId in enabledLibraryIds)
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = libraryId,
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    Recursive = true
                }).OfType<Video>()
                 .Where(v => !v.HasSubtitles)
                 .ToList();

                allItems.AddRange(items);
            }

            _logger.LogInformation("Found {Count} items without subtitles across {LibCount} libraries",
                allItems.Count, enabledLibraryIds.Count);

            if (allItems.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var completed = 0;
            var failed = 0;

            for (int i = 0; i < allItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Drain any priority (manual) requests first
                if (queue.PriorityCount > 0)
                {
                    _logger.LogInformation("Pausing auto-generation to process {Count} priority request(s)", queue.PriorityCount);
                    await queue.DrainPriorityAsync(manager, provider, _logger, cancellationToken);
                }

                var item = allItems[i];

                // Skip if subtitle was already generated (e.g. from a previous run before restart)
                var mediaPath = item.Path;
                if (!string.IsNullOrEmpty(mediaPath))
                {
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(mediaPath);
                    var dir = System.IO.Path.GetDirectoryName(mediaPath);
                    if (dir != null)
                    {
                        var existingFiles = System.IO.Directory.GetFiles(dir, baseName + ".*.generated.srt");
                        var noForeignMarkers = System.IO.Directory.GetFiles(dir, baseName + ".*.forced.noforeignlang");
                        var hasFullSrt = existingFiles.Any(f => !System.IO.Path.GetFileName(f).Contains(".forced."));
                        var hasForcedSrt = existingFiles.Any(f => System.IO.Path.GetFileName(f).Contains(".forced.")) || noForeignMarkers.Length > 0;

                        bool alreadyComplete = config.SubtitleMode switch
                        {
                            Configuration.SubtitleMode.Full => hasFullSrt,
                            Configuration.SubtitleMode.ForcedOnly => hasForcedSrt,
                            Configuration.SubtitleMode.FullAndForced => hasFullSrt && hasForcedSrt,
                            _ => hasFullSrt
                        };

                        if (alreadyComplete)
                        {
                            completed++;
                            progress.Report((double)completed / allItems.Count * 100);
                            continue;
                        }
                    }
                }

                try
                {
                    _logger.LogInformation("[{Current}/{Total}] Processing {ItemName}",
                        completed + 1, allItems.Count, item.Name);

                    await SubtitleQueueService.TranscriptionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await manager.GenerateSubtitleAsync(item, provider, language, cancellationToken);
                    }
                    finally
                    {
                        SubtitleQueueService.TranscriptionLock.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to generate subtitle for {ItemName}", item.Name);
                }

                completed++;
                progress.Report((double)completed / allItems.Count * 100);
            }

            _logger.LogInformation("Subtitle generation task complete. Processed: {Processed}, Failed: {Failed}",
                completed, failed);
        }
    }
}
