using MediaBrowser.Model.Plugins;

namespace WhisperSubs.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
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
        /// When enabled, music libraries are scanned and audio tracks receive
        /// .lrc lyrics files generated via whisper transcription.
        /// Experimental: whisper models are optimized for speech, not singing.
        /// </summary>
        public bool EnableLyricsGeneration { get; set; } = false;

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
