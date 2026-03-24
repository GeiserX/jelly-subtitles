using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Controller;
using JellySubtitles.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.ScheduledTasks
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
        public string Key => "JellySubtitlesGenerator";
        public string Description => "Scans enabled libraries and generates subtitles for items that lack them.";
        public string Category => "JellySubtitles";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks // Daily at 2 AM
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

            var language = config.DefaultLanguage;

            // Collect all video items from enabled libraries
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

            // Enqueue all items into the normal (low-priority) queue
            var queue = SubtitleQueueService.Instance;
            foreach (var item in allItems)
            {
                queue.EnqueueNormal(item, language);
            }

            _logger.LogInformation("Enqueued {Count} items for subtitle generation", allItems.Count);

            // Process the queue (manual/priority items will be interleaved first)
            await queue.ProcessQueueAsync(_libraryManager, _loggerFactory, progress, cancellationToken);

            _logger.LogInformation("Subtitle generation task complete");
        }
    }
}
