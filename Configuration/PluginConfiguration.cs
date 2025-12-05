using MediaBrowser.Model.Plugins;

namespace JellySubtitles.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string SelectedProvider { get; set; } = "Whisper";
        public string WhisperModelPath { get; set; } = "";
        public bool EnableAutoGeneration { get; set; } = false;
        
        // List of library IDs to monitor
        public List<string> EnabledLibraries { get; set; } = new List<string>();

        public PluginConfiguration()
        {
        }
    }
}
