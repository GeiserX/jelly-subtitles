using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Configuration;
using JellySubtitles.Controller;
using JellySubtitles.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Api
{
    [ApiController]
    [Route("Plugins/JellySubtitles")]
    [Authorize]
    public class SubtitleController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SubtitleController> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ITaskManager _taskManager;

        public SubtitleController(
            ILibraryManager libraryManager,
            ILogger<SubtitleController> logger,
            ILoggerFactory loggerFactory,
            ITaskManager taskManager)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
            _taskManager = taskManager;
        }

        private SubtitleManager GetSubtitleManager()
        {
            return new SubtitleManager(_libraryManager, _loggerFactory.CreateLogger<SubtitleManager>());
        }

        /// <summary>
        /// Gets all libraries.
        /// </summary>
        [HttpGet("Libraries")]
        public ActionResult<IEnumerable<LibraryInfo>> GetLibraries()
        {
            try
            {
                var libraries = _libraryManager.GetVirtualFolders()
                    .Select(vf => new LibraryInfo
                    {
                        Id = vf.ItemId,
                        Name = vf.Name
                    })
                    .ToList();

                return Ok(libraries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting libraries");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets items in a library with pagination.
        /// </summary>
        [HttpGet("Libraries/{libraryId}/Items")]
        public ActionResult<PagedItemResult> GetLibraryItems(
            [FromRoute] string libraryId,
            [FromQuery] int startIndex = 0,
            [FromQuery] int limit = 50)
        {
            try
            {
                var library = _libraryManager.GetItemById(Guid.Parse(libraryId));
                if (library == null)
                {
                    return NotFound(new { error = "Library not found" });
                }

                var includeTypes = GetBaseItemKinds("Movie,Episode");
                var allItems = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = library.Id,
                    IncludeItemTypes = includeTypes,
                    Recursive = true
                });

                var totalCount = allItems.Count;

                var items = allItems
                    .Skip(startIndex)
                    .Take(limit)
                    .Select(item => new ItemInfo
                    {
                        Id = item.Id.ToString(),
                        Name = item.Name,
                        Type = item.GetType().Name,
                        Path = item.Path,
                        HasSubtitles = item is Video video && video.HasSubtitles
                    })
                    .ToList();

                return Ok(new PagedItemResult
                {
                    Items = items,
                    TotalCount = totalCount,
                    StartIndex = startIndex
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting library items");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Generates subtitles for a specific item.
        /// Manual requests are placed at the front of the queue, ahead of auto-generation.
        /// Language defaults to the plugin's configured default (typically "auto").
        /// "auto" detects the audio language from the file metadata.
        /// </summary>
        [HttpPost("Items/{itemId}/Generate")]
        public async Task<ActionResult> GenerateSubtitle(
            [FromRoute] string itemId,
            [FromQuery] string? language = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                if (!(item is Video video))
                {
                    return BadRequest(new { error = "Item is not a video" });
                }

                var config = Plugin.Instance.Configuration;
                var targetLanguage = language ?? config.DefaultLanguage;

                // Enqueue as priority (ahead of auto-generation items)
                var queue = SubtitleQueueService.Instance;
                var completionTask = queue.EnqueuePriorityAsync(video, targetLanguage);
                _logger.LogInformation("Queued priority subtitle generation for {ItemName} [{Language}]", item.Name, targetLanguage);

                // Kick off queue processing if not already running
                var loggerFactory = HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                if (loggerFactory != null)
                {
                    _ = Task.Run(() => queue.ProcessQueueAsync(
                        _libraryManager, loggerFactory, null, cancellationToken), cancellationToken);
                }

                // Wait for this specific item to complete
                await completionTask;

                return Ok(new { message = "Subtitle generation complete", language = targetLanguage });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating subtitle for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Detects audio languages present in a media file.
        /// </summary>
        [HttpGet("Items/{itemId}/AudioLanguages")]
        public async Task<ActionResult<IEnumerable<string>>> GetAudioLanguages(
            [FromRoute] string itemId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                if (string.IsNullOrEmpty(item.Path) || !System.IO.File.Exists(item.Path))
                {
                    return BadRequest(new { error = "Media file not found" });
                }

                var subtitleManager = GetSubtitleManager();
                var languages = await subtitleManager.DetectAudioLanguagesAsync(item.Path, cancellationToken);
                return Ok(languages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting audio languages for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets the status of subtitle generation for an item.
        /// When language is omitted or "auto", checks for any generated .srt file.
        /// </summary>
        [HttpGet("Items/{itemId}/Status")]
        public ActionResult<SubtitleStatus> GetSubtitleStatus(
            [FromRoute] string itemId,
            [FromQuery] string? language = null)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                var lang = language ?? Plugin.Instance.Configuration.DefaultLanguage;

                if (string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    // Look for any generated subtitle file
                    var dir = System.IO.Path.GetDirectoryName(item.Path);
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(item.Path);
                    var found = new List<string>();

                    if (dir != null)
                    {
                        foreach (var f in System.IO.Directory.GetFiles(dir, $"{baseName}.*.generated.srt"))
                        {
                            found.Add(f);
                        }
                    }

                    return Ok(new SubtitleStatus
                    {
                        ItemId = itemId,
                        HasGeneratedSubtitle = found.Count > 0,
                        SubtitlePath = found.Count > 0 ? string.Join("; ", found) : null
                    });
                }

                var subtitlePath = System.IO.Path.ChangeExtension(item.Path, $".{lang}.generated.srt");
                var hasGeneratedSubtitle = System.IO.File.Exists(subtitlePath);

                return Ok(new SubtitleStatus
                {
                    ItemId = itemId,
                    HasGeneratedSubtitle = hasGeneratedSubtitle,
                    SubtitlePath = hasGeneratedSubtitle ? subtitlePath : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting subtitle status for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Lists available whisper model files.
        /// Scans the directory of the currently configured model path for .bin files.
        /// </summary>
        [HttpGet("Models")]
        public ActionResult<IEnumerable<ModelInfo>> GetAvailableModels()
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var modelsDir = !string.IsNullOrEmpty(config.WhisperModelPath)
                    ? System.IO.Path.GetDirectoryName(config.WhisperModelPath)
                    : null;

                if (string.IsNullOrEmpty(modelsDir) || !System.IO.Directory.Exists(modelsDir))
                {
                    return Ok(Array.Empty<ModelInfo>());
                }

                var models = System.IO.Directory.GetFiles(modelsDir, "*.bin")
                    .Select(path => new ModelInfo
                    {
                        Path = path,
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        SizeMB = new System.IO.FileInfo(path).Length / (1024.0 * 1024.0),
                        IsActive = string.Equals(path, config.WhisperModelPath, StringComparison.OrdinalIgnoreCase)
                    })
                    .OrderBy(m => m.SizeMB)
                    .ToList();

                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing models");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Triggers the subtitle generation scheduled task immediately.
        /// </summary>
        [HttpPost("RunTask")]
        public ActionResult RunTask()
        {
            try
            {
                _taskManager.QueueScheduledTask<JellySubtitles.ScheduledTasks.SubtitleGenerationTask>();
                return Ok(new { message = "Subtitle generation task queued" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering subtitle generation task");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private BaseItemKind[] GetBaseItemKinds(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Array.Empty<BaseItemKind>();
            }

            var parts = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var converter = TypeDescriptor.GetConverter(typeof(BaseItemKind));
            var result = new List<BaseItemKind>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (converter.IsValid(trimmed) && Enum.TryParse<BaseItemKind>(trimmed, true, out var kind))
                {
                    result.Add(kind);
                }
            }

            return result.ToArray();
        }
    }

    public class LibraryInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class ItemInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? Path { get; set; }
        public bool HasSubtitles { get; set; }
    }

    public class SubtitleStatus
    {
        public string ItemId { get; set; } = string.Empty;
        public bool HasGeneratedSubtitle { get; set; }
        public string? SubtitlePath { get; set; }
    }

    public class ModelInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double SizeMB { get; set; }
        public bool IsActive { get; set; }
    }

    public class PagedItemResult
    {
        public List<ItemInfo> Items { get; set; } = new List<ItemInfo>();
        public int TotalCount { get; set; }
        public int StartIndex { get; set; }
    }
}

