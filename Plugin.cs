using System;
using System.Collections.Generic;
using System.IO;
using WhisperSubs.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace WhisperSubs
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly IApplicationPaths _appPaths;

        public override string Name => "WhisperSubs";
        public override Guid Id => Guid.Parse("97124bd9-c8cd-4a53-a213-e593aa3fef52"); // Using a static GUID

        // Store data outside /plugins/ to avoid Jellyfin treating the data dir as a plugin folder
        public new string DataFolderPath => Path.Combine(_appPaths.DataPath, "WhisperSubs");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            _appPaths = applicationPaths;
            Instance = this;
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
                }
            };
        }
    }
}
