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

            // In ForcedOnly/FullAndForced modes, items with full subtitles but no forced
            // subtitles must still be considered. Only filter by HasSubtitles in Full mode.
            var needsForced = config.SubtitleMode == Configuration.SubtitleMode.ForcedOnly
                || config.SubtitleMode == Configuration.SubtitleMode.FullAndForced;

            var includeKinds = new List<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Episode };
            if (config.EnableLyricsGeneration)
            {
                includeKinds.Add(BaseItemKind.Audio);
            }

            var allItems = new List<BaseItem>();
            foreach (var libraryId in enabledLibraryIds)
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = libraryId,
                    IncludeItemTypes = includeKinds.ToArray(),
                    Recursive = true
                });

                foreach (var queryItem in items)
                {
                    if (queryItem is Video video)
                    {
                        if (!needsForced && video.HasSubtitles) continue;
                        allItems.Add(video);
                    }
                    else if (queryItem is MediaBrowser.Controller.Entities.Audio.Audio)
                    {
                        allItems.Add(queryItem);
                    }
                }
            }

            _logger.LogInformation("Found {Count} candidate items across {LibCount} libraries",
                allItems.Count, enabledLibraryIds.Count);

            if (allItems.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var completed = 0;
            var failed = 0;
            queue.ReportTaskProgress(null, 0, allItems.Count, 0);

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

                // For Audio items (lyrics), skip if .lrc already exists
                if (item is MediaBrowser.Controller.Entities.Audio.Audio)
                {
                    try
                    {
                        var audioPath = item.Path;
                        if (!string.IsNullOrEmpty(audioPath))
                        {
                            var audioDir = System.IO.Path.GetDirectoryName(audioPath);
                            var audioBase = System.IO.Path.GetFileNameWithoutExtension(audioPath);
                            if (audioDir != null)
                            {
                                // Check Jellyfin-standard track.lrc and language-tagged track.*.lrc
                                var exactLrc = System.IO.Path.Combine(audioDir, audioBase + ".lrc");
                                if (System.IO.File.Exists(exactLrc) || System.IO.Directory.GetFiles(audioDir, audioBase + ".*.lrc").Length > 0)
                                {
                                    completed++;
                                    queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                                    progress.Report((double)completed / allItems.Count * 100);
                                    continue;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error checking lyrics for {ItemName}, will attempt generation", item.Name);
                    }
                }

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

                        // Also check for user-provided external subtitle files (non-forced, non-generated)
                        if (!hasFullSrt)
                        {
                            var subtitleExts = new[] { ".srt", ".ass", ".ssa", ".sub", ".vtt" };
                            hasFullSrt = System.IO.Directory.GetFiles(dir, baseName + ".*")
                                .Any(f =>
                                {
                                    var name = System.IO.Path.GetFileName(f);
                                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                                    return subtitleExts.Contains(ext)
                                        && !name.Contains(".forced.")
                                        && !name.Contains(".generated.");
                                });
                        }
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
                            queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                            progress.Report((double)completed / allItems.Count * 100);
                            continue;
                        }
                    }
                }

                try
                {
                    _logger.LogInformation("[{Current}/{Total}] Processing {ItemName}",
                        completed + 1, allItems.Count, item.Name);
                    queue.ResetFileProgress();
                    queue.ReportTaskProgress(item.Name, completed, allItems.Count, failed);

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
                queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                progress.Report((double)completed / allItems.Count * 100);
            }

            queue.ReportTaskProgress(null, completed, allItems.Count, failed);
            queue.ReportTaskComplete();
            _logger.LogInformation("Subtitle generation task complete. Processed: {Processed}, Failed: {Failed}",
                completed, failed);
        }
    }
}
