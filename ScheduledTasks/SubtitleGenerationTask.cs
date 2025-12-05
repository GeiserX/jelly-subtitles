using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Controller;
using JellySubtitles.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.ScheduledTasks
{
    public class SubtitleGenerationTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SubtitleGenerationTask> _logger;
        private readonly ILogger<SubtitleManager> _managerLogger;
        private readonly ILogger<WhisperProvider> _providerLogger;

        public SubtitleGenerationTask(
            ILibraryManager libraryManager, 
            ILogger<SubtitleGenerationTask> logger,
            ILogger<SubtitleManager> managerLogger,
            ILogger<WhisperProvider> providerLogger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _managerLogger = managerLogger;
            _providerLogger = providerLogger;
        }

        public string Name => "Generate Subtitles";
        public string Key => "JellySubtitlesGenerator";
        public string Description => "Scans libraries and generates subtitles for missing items.";
        public string Category => "JellySubtitles";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(2).Ticks }
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting subtitle generation task.");

            var config = Plugin.Instance.Configuration;
            if (!config.EnableAutoGeneration)
            {
                _logger.LogInformation("Auto-generation is disabled in configuration.");
                return;
            }

            var manager = new SubtitleManager(_libraryManager, _managerLogger);
            var provider = new WhisperProvider(_providerLogger, config.WhisperModelPath);

            // TODO: Iterate over enabled libraries and find items
            // var items = _libraryManager.GetItemList(...);
            
            await Task.CompletedTask; // Placeholder for async work
            progress.Report(100);
            _logger.LogInformation("Subtitle generation task completed.");
        }
    }
}
