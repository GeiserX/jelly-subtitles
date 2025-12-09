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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Api
{
    [ApiController]
    [Route("Plugins/JellySubtitles")]
    [Authorize(Policy = "DefaultAuthorization")]
    public class SubtitleController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SubtitleController> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public SubtitleController(
            ILibraryManager libraryManager,
            ILogger<SubtitleController> logger,
            ILoggerFactory loggerFactory)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
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
                var rootFolder = _libraryManager.RootFolder;
                var libraries = rootFolder.GetChildren(null, true)
                    .Where(item => item is MediaBrowser.Controller.Entities.CollectionFolder)
                    .Select(item => new LibraryInfo
                    {
                        Id = item.Id.ToString(),
                        Name = item.Name
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
        /// Gets items in a library.
        /// </summary>
        [HttpGet("Libraries/{libraryId}/Items")]
        public ActionResult<IEnumerable<ItemInfo>> GetLibraryItems([FromRoute] string libraryId)
        {
            try
            {
                var library = _libraryManager.GetItemById(Guid.Parse(libraryId));
                if (library == null)
                {
                    return NotFound(new { error = "Library not found" });
                }

                var includeTypes = GetBaseItemKinds("Movie,Episode,Series,Season");
                var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = library.Id,
                    IncludeItemTypes = includeTypes,
                    Recursive = true
                })
                .Select(item => new ItemInfo
                {
                    Id = item.Id.ToString(),
                    Name = item.Name,
                    Type = item.GetType().Name,
                    Path = item.Path,
                    HasSubtitles = item is Video video && video.HasSubtitles
                })
                .ToList();

                return Ok(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting library items");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Generates subtitles for a specific item.
        /// </summary>
        [HttpPost("Items/{itemId}/Generate")]
        public async Task<ActionResult> GenerateSubtitle(
            [FromRoute] string itemId,
            [FromQuery] string? language = "en",
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
                
                // Create logger factory for provider
                var loggerFactory = HttpContext.RequestServices.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                if (loggerFactory == null)
                {
                    throw new InvalidOperationException("Logger factory not available");
                }
                
                ISubtitleProvider provider = config.SelectedProvider switch
                {
                    "Whisper" => new WhisperProvider(
                        loggerFactory.CreateLogger<WhisperProvider>(),
                        config.WhisperModelPath),
                    _ => throw new NotSupportedException($"Provider {config.SelectedProvider} is not supported")
                };

                var subtitleManager = GetSubtitleManager();
                await subtitleManager.GenerateSubtitleAsync(video, provider, cancellationToken);

                return Ok(new { message = "Subtitle generation started" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating subtitle for item {ItemId}", itemId);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Gets the status of subtitle generation for an item.
        /// </summary>
        [HttpGet("Items/{itemId}/Status")]
        public ActionResult<SubtitleStatus> GetSubtitleStatus([FromRoute] string itemId)
        {
            try
            {
                var item = _libraryManager.GetItemById(Guid.Parse(itemId));
                if (item == null)
                {
                    return NotFound(new { error = "Item not found" });
                }

                // Check if subtitle file exists
                var subtitlePath = System.IO.Path.ChangeExtension(item.Path, ".en.generated.srt");
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
}

