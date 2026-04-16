using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using WhisperSubs.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace WhisperSubs
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly IApplicationPaths _appPaths;
        private readonly ILogger<Plugin> _logger;

        private const string ScriptTag = "<script src=\"configurationpage?name=whisperSubs.js\"></script>";

        public override string Name => "WhisperSubs";
        public override Guid Id => Guid.Parse("97124bd9-c8cd-4a53-a213-e593aa3fef52"); // Using a static GUID

        // Store data outside /plugins/ to avoid Jellyfin treating the data dir as a plugin folder
        public new string DataFolderPath => Path.Combine(_appPaths.DataPath, "WhisperSubs");

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _appPaths = applicationPaths;
            _logger = logger;
            Instance = this;

            InjectClientScript();
        }

        public static Plugin Instance { get; private set; } = null!;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
                    EnableInMainMenu = true
                },
                new PluginPageInfo
                {
                    Name = "whisperSubs.js",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.whisperSubs.js"
                }
            };
        }

        /// <summary>
        /// Injects a script tag into Jellyfin's index.html so our JS runs on every page.
        /// Follows the same pattern used by intro-skipper and JellyScrub plugins.
        /// </summary>
        private void InjectClientScript()
        {
            try
            {
                var indexPath = Path.Combine(_appPaths.WebPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    _logger.LogDebug("WhisperSubs: index.html not found at {Path}, skipping script injection", indexPath);
                    return;
                }

                var contents = File.ReadAllText(indexPath);
                if (contents.Contains(ScriptTag, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("WhisperSubs: script tag already present in index.html");
                    return;
                }

                // Insert before </head>
                var headEnd = new Regex("</head>", RegexOptions.IgnoreCase);
                if (!headEnd.IsMatch(contents))
                {
                    _logger.LogWarning("WhisperSubs: could not find </head> in index.html, skipping script injection");
                    return;
                }

                contents = headEnd.Replace(contents, ScriptTag + "\n</head>", 1);
                File.WriteAllText(indexPath, contents);
                _logger.LogInformation("WhisperSubs: injected client script into index.html");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WhisperSubs: failed to inject client script into index.html");
            }
        }
    }
}
