using MediaBrowser.Model.Plugins;

namespace WhisperSubs.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string SelectedProvider { get; set; } = "Whisper";
        public string WhisperModelPath { get; set; } = "";
        public string WhisperBinaryPath { get; set; } = "";
        public bool EnableAutoGeneration { get; set; } = false;

        /// <summary>
        /// Default language for subtitle generation.
        /// "auto" = detect from audio stream metadata, fall back to whisper auto-detection.
        /// Any ISO 639-1 code (e.g. "es", "en", "fr") forces that language.
        /// </summary>
        public string DefaultLanguage { get; set; } = "auto";

        /// <summary>
        /// Controls whether to generate full subtitles, forced-only subtitles, or both.
        /// </summary>
        public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.Full;

        /// <summary>
        /// Number of threads for whisper.cpp inference. 0 = whisper default (4).
        /// Higher values use more CPU cores for faster transcription.
        /// </summary>
        public int WhisperThreadCount { get; set; } = 0;

        public List<string> EnabledLibraries { get; set; } = new List<string>();

        public PluginConfiguration()
        {
        }
    }
}
