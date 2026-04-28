using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Api
{
    [ApiController]
    [Route("Plugins/WhisperSubs")]
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

                var config = Plugin.Instance.Configuration;
                var typeStr = config.EnableLyricsGeneration ? "Movie,Episode,Video,Audio" : "Movie,Episode,Video";
                var includeTypes = GetBaseItemKinds(typeStr);
                var allItems = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = library.Id,
                    IncludeItemTypes = includeTypes,
                    Recursive = true
                }).Where(item => !string.IsNullOrEmpty(item.Path)).ToList();

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
                        HasSubtitles = item is Video v ? v.HasSubtitles : CheckLyricsExist(item.Path)
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
        /// Enqueues subtitle generation for a specific item. Returns 202 immediately.
        /// A single background worker processes the queue sequentially.
        /// </summary>
        [HttpPost("Items/{itemId}/Generate")]
        public ActionResult GenerateSubtitle(
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

                var config = Plugin.Instance.Configuration;
                var isAudio = item is MediaBrowser.Controller.Entities.Audio.Audio;

                if (!(item is Video) && !(isAudio && config.EnableLyricsGeneration))
                {
                    return BadRequest(new { error = "Item is not a supported media type" });
                }

                var targetLanguage = language ?? config.DefaultLanguage;
                var queue = SubtitleQueueService.Instance;

                queue.Enqueue(item, targetLanguage);
                _logger.LogInformation("Queued {Action} generation for {ItemName} [{Language}]. Queue size: {Count}",
                    isAudio ? "lyrics" : "subtitle", item.Name, targetLanguage, queue.PriorityCount);

                // Ensure the background drain worker is running
                var manager = GetSubtitleManager();
                var provider = SubtitleProviderFactory.Create(config, _loggerFactory);
                queue.EnsureDraining(manager, provider, _logger, CancellationToken.None);

                return Accepted(new
                {
                    message = isAudio ? "Queued for lyrics generation" : "Queued for subtitle generation",
                    item = item.Name,
                    language = targetLanguage,
                    queueSize = queue.PriorityCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing subtitle for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Enqueues subtitle generation for all child items of a container (Series, Season, MusicAlbum).
        /// For leaf items (Movie, Episode, Audio), behaves like the single-item endpoint.
        /// </summary>
        [HttpPost("Items/{itemId}/GenerateAll")]
        public ActionResult GenerateAllSubtitles(
            [FromRoute] string itemId,
            [FromQuery] string? language = null)
        {
            try
            {
                var parent = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (parent == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                var config = Plugin.Instance.Configuration;
                var targetLanguage = language ?? config.DefaultLanguage;

                // Resolve leaf items (Video / Audio) from the container
                var typeStr = config.EnableLyricsGeneration ? "Movie,Episode,Video,Audio" : "Movie,Episode,Video";
                var includeTypes = GetBaseItemKinds(typeStr);
                var children = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = parent.Id,
                    IncludeItemTypes = includeTypes,
                    Recursive = true
                }).Where(item => !string.IsNullOrEmpty(item.Path)).ToList();

                // If this IS a leaf item itself, include it directly
                if (children.Count == 0 && !string.IsNullOrEmpty(parent.Path) && (parent is Video || (parent is MediaBrowser.Controller.Entities.Audio.Audio && config.EnableLyricsGeneration)))
                {
                    children = new List<BaseItem> { parent };
                }

                if (children.Count == 0)
                {
                    return BadRequest(new { error = "No supported media items found under this item." });
                }

                var queue = SubtitleQueueService.Instance;
                foreach (var child in children)
                {
                    queue.Enqueue(child, targetLanguage);
                }

                _logger.LogInformation(
                    "Queued {Count} items for subtitle generation under {ParentName} [{Language}]",
                    children.Count, parent.Name, targetLanguage);

                // Ensure the background drain worker is running
                var manager = GetSubtitleManager();
                var provider = SubtitleProviderFactory.Create(config, _loggerFactory);
                queue.EnsureDraining(manager, provider, _logger, CancellationToken.None);

                return Accepted(new
                {
                    message = $"Queued {children.Count} item(s) for subtitle generation",
                    parent = parent.Name,
                    count = children.Count,
                    language = targetLanguage,
                    queueSize = queue.PriorityCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing batch generation for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets the current queue status.
        /// </summary>
        [HttpGet("Queue")]
        public ActionResult GetQueueStatus()
        {
            var queue = SubtitleQueueService.Instance;
            return Ok(new
            {
                isProcessing = queue.IsDraining || queue.IsTaskRunning,
                currentItem = queue.CurrentItemName,
                remaining = queue.IsTaskRunning
                    ? queue.PriorityCount + (queue.TaskTotal - queue.TaskProcessed)
                    : queue.PriorityCount,
                processed = queue.ProcessedCount + queue.TaskProcessed,
                failed = queue.TaskFailed,
                taskTotal = queue.IsTaskRunning ? queue.TaskTotal : 0,
                fileProgress = queue.CurrentFileProgress,
                phase = queue.CurrentPhase,
                itemType = queue.TaskCurrentItemType,
                library = queue.TaskCurrentItemLibrary
            });
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

                    var fullFiles = found.Where(f => !System.IO.Path.GetFileName(f).Contains(".forced.")).ToList();
                    var forcedFiles = found.Where(f => System.IO.Path.GetFileName(f).Contains(".forced.")).ToList();

                    return Ok(new SubtitleStatus
                    {
                        ItemId = itemId,
                        HasGeneratedSubtitle = fullFiles.Count > 0,
                        HasForcedSubtitle = forcedFiles.Count > 0,
                        HasExistingSubtitles = item is Video v1 && v1.HasSubtitles,
                        SubtitlePath = found.Count > 0 ? string.Join("; ", found) : null
                    });
                }

                var subtitlePath = System.IO.Path.ChangeExtension(item.Path, $".{lang}.generated.srt");
                var forcedSubtitlePath = System.IO.Path.ChangeExtension(item.Path, $".{lang}.forced.generated.srt");
                var hasGeneratedSubtitle = System.IO.File.Exists(subtitlePath);
                var hasForcedSubtitle = System.IO.File.Exists(forcedSubtitlePath);

                return Ok(new SubtitleStatus
                {
                    ItemId = itemId,
                    HasGeneratedSubtitle = hasGeneratedSubtitle,
                    HasForcedSubtitle = hasForcedSubtitle,
                    HasExistingSubtitles = item is Video v2 && v2.HasSubtitles,
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

                // Fallback to default models directory
                if (string.IsNullOrEmpty(modelsDir) || !System.IO.Directory.Exists(modelsDir))
                {
                    var dataDir = Plugin.Instance?.DataFolderPath;
                    if (!string.IsNullOrEmpty(dataDir))
                    {
                        var defaultDir = System.IO.Path.Combine(dataDir, "whisper", "models");
                        if (System.IO.Directory.Exists(defaultDir))
                            modelsDir = defaultDir;
                    }
                }

                if (string.IsNullOrEmpty(modelsDir) || !System.IO.Directory.Exists(modelsDir))
                {
                    return Ok(Array.Empty<ModelInfo>());
                }

                var models = System.IO.Directory.GetFiles(modelsDir, "*.bin")
                    .Select(path => new ModelInfo
                    {
                        Path = path,
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        FileName = System.IO.Path.GetFileName(path),
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
                _taskManager.QueueScheduledTask<WhisperSubs.ScheduledTasks.SubtitleGenerationTask>();
                return Ok(new { message = "Subtitle generation task queued" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering subtitle generation task");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── Setup endpoints ──────────────────────────────────────────────

        private WhisperSetupService GetSetupService()
        {
            return new WhisperSetupService(
                _loggerFactory.CreateLogger<WhisperSetupService>(),
                Plugin.Instance.DataFolderPath);
        }

        /// <summary>
        /// Returns whether whisper binary and model are configured and reachable.
        /// </summary>
        [HttpGet("Setup/Status")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetSetupStatus()
        {
            try
            {
                var status = GetSetupService().GetStatus();
                return Ok(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking setup status");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Lists whisper models available for download from HuggingFace.
        /// </summary>
        [HttpGet("Setup/AvailableModels")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetDownloadableModels()
        {
            return Ok(ModelCatalog.Models.Select(m => new
            {
                m.FileName,
                m.DisplayName,
                m.SizeMB,
                m.IsRecommended,
                m.Description
            }));
        }

        /// <summary>
        /// Downloads a whisper model from HuggingFace. Returns 202 immediately;
        /// poll GET Setup/Progress for status.
        /// </summary>
        [HttpPost("Setup/DownloadModel")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DownloadModel([FromQuery] string name)
        {
            if (string.IsNullOrEmpty(name))
                return BadRequest(new { error = "Model name is required." });

            var catalogEntry = ModelCatalog.Models.FirstOrDefault(m =>
                string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase));
            if (catalogEntry == null)
                return BadRequest(new { error = $"Unknown model: {name}" });

            var canonicalName = catalogEntry.FileName;

            if (!WhisperSetupService.TryAcquire("model", $"Starting download of {canonicalName}..."))
                return Conflict(new { error = "A download is already in progress." });

            var service = GetSetupService();

            _ = Task.Run(async () =>
            {
                try { await service.DownloadModelAsync(canonicalName, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Background model download failed"); }
            });

            return Accepted(new { message = $"Download of {canonicalName} started." });
        }

        /// <summary>
        /// Lists available binary variants (CPU, CUDA, Vulkan).
        /// </summary>
        [HttpGet("Setup/BinaryVariants")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetBinaryVariants()
        {
            var platform = WhisperSetupService.GetPlatformIdentifier();
            var variants = BinaryCatalog.GetAvailableVariants(platform);
            return Ok(variants.Select(v => new
            {
                v.Id,
                v.DisplayName,
                v.Description,
                v.IsDefault,
                Platform = platform
            }));
        }

        /// <summary>
        /// Downloads the whisper-cli binary for the current platform from the
        /// whisper-subs GitHub release. Returns 202 immediately.
        /// </summary>
        /// <param name="variant">Binary variant: cpu (default), cuda12, or vulkan.</param>
        [HttpPost("Setup/DownloadBinary")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DownloadBinary([FromQuery] string variant = "cpu")
        {
            var platform = WhisperSetupService.GetPlatformIdentifier();
            var available = BinaryCatalog.GetAvailableVariants(platform);
            var matchedVariant = available.FirstOrDefault(v => string.Equals(v.Id, variant, StringComparison.OrdinalIgnoreCase));
            if (matchedVariant == null)
                return BadRequest(new { error = $"No prebuilt binary for variant '{variant}' on {platform}. Prebuilt binaries are only available for Linux." });

            var canonicalVariant = matchedVariant.Id;

            if (!WhisperSetupService.TryAcquire("binary", $"Starting whisper-cli ({canonicalVariant}) download..."))
                return Conflict(new { error = "A download is already in progress." });

            var service = GetSetupService();

            _ = Task.Run(async () =>
            {
                try { await service.DownloadBinaryAsync(canonicalVariant, CancellationToken.None); }
                catch (Exception ex) { _logger.LogError(ex, "Background binary download failed"); }
            });

            return Accepted(new { message = $"Binary download started (variant: {canonicalVariant})." });
        }

        /// <summary>
        /// Returns the current download progress (model or binary).
        /// </summary>
        [HttpGet("Setup/Progress")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult GetSetupProgress()
        {
            var p = WhisperSetupService.CurrentProgress;
            return Ok(p);
        }

        /// <summary>
        /// Activates a downloaded model by setting it as the configured model path.
        /// </summary>
        [HttpPost("Setup/Models/{filename}/Activate")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult ActivateModel([FromRoute] string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(filename) || filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                    return BadRequest(new { error = "Invalid filename" });

                var dataDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(dataDir))
                    return StatusCode(500, new { error = "Plugin data directory not available" });

                var modelsDir = System.IO.Path.Combine(dataDir, "whisper", "models");
                var modelPath = System.IO.Path.Combine(modelsDir, filename);

                if (!System.IO.File.Exists(modelPath))
                    return NotFound(new { error = "Model file not found" });

                var config = Plugin.Instance!.Configuration;
                config.WhisperModelPath = modelPath;
                Plugin.Instance.SaveConfiguration();

                _logger.LogInformation("Activated model: {Path}", modelPath);
                return Ok(new { message = "Model activated", path = modelPath });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating model {Filename}", filename);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Deletes a downloaded model. Cannot delete the active model or the last remaining model.
        /// </summary>
        [HttpDelete("Setup/Models/{filename}")]
        [Authorize(Policy = "RequiresElevation")]
        public ActionResult DeleteModel([FromRoute] string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(filename) || filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                    return BadRequest(new { error = "Invalid filename" });

                var dataDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(dataDir))
                    return StatusCode(500, new { error = "Plugin data directory not available" });

                var modelsDir = System.IO.Path.Combine(dataDir, "whisper", "models");
                var modelPath = System.IO.Path.Combine(modelsDir, filename);

                if (!System.IO.File.Exists(modelPath))
                    return NotFound(new { error = "Model file not found" });

                var config = Plugin.Instance!.Configuration;
                if (string.Equals(config.WhisperModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { error = "Cannot delete the active model. Activate a different model first." });

                var allModels = System.IO.Directory.GetFiles(modelsDir, "*.bin");
                if (allModels.Length <= 1)
                    return BadRequest(new { error = "Cannot delete the last remaining model." });

                System.IO.File.Delete(modelPath);
                _logger.LogInformation("Deleted model: {Path}", modelPath);
                return Ok(new { message = "Model deleted" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting model {Filename}", filename);
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

        private static bool CheckLyricsExist(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var dir = System.IO.Path.GetDirectoryName(path);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (dir == null) return false;
            try
            {
                // Check Jellyfin-standard track.lrc and language-tagged track.*.lrc
                var exactLrc = System.IO.Path.Combine(dir, baseName + ".lrc");
                return System.IO.File.Exists(exactLrc) || System.IO.Directory.GetFiles(dir, baseName + ".*.lrc").Length > 0;
            }
            catch { return false; }
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
        public bool HasForcedSubtitle { get; set; }
        public bool HasExistingSubtitles { get; set; }
        public string? SubtitlePath { get; set; }
    }

    public class ModelInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
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

