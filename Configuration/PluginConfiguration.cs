using System.Text.Json.Serialization;
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
        [JsonConverter(typeof(SubtitleModeConverter))]
        public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.Full;

        /// <summary>
        /// When enabled, music libraries are scanned and audio tracks receive
        /// .lrc lyrics files generated via whisper transcription.
        /// Experimental: whisper models are optimized for speech, not singing.
        /// </summary>
        public bool EnableLyricsGeneration { get; set; } = false;

        /// <summary>
        /// When enabled, generates English subtitles via whisper's --translate flag
        /// for media that lacks an English audio track.
        /// Only applies when SubtitleMode includes Full subtitles.
        /// </summary>
        public bool EnableTranslation { get; set; } = false;

        /// <summary>
        /// Number of threads for whisper.cpp inference. 0 = whisper default (4).
        /// Higher values use more CPU cores for faster transcription.
        /// </summary>
        public int WhisperThreadCount { get; set; } = 0;

        /// <summary>
        /// Optional URL of an OpenAI-compatible Whisper API server (e.g. faster-whisper-server/speaches).
        /// When set, audio is sent to this endpoint instead of running whisper-cli locally.
        /// Example: http://192.168.1.100:8000
        /// </summary>
        public string RemoteWhisperApiUrl { get; set; } = "";

        /// <summary>
        /// Model name to request from the remote API.
        /// For speaches/faster-whisper-server: a Hugging Face model ID (e.g. "Systran/faster-whisper-large-v3").
        /// For OpenAI: "whisper-1".
        /// </summary>
        public string RemoteWhisperModel { get; set; } = "Systran/faster-whisper-large-v3";

        /// <summary>
        /// Optional API key for the remote Whisper API. When set, the value is
        /// sent as `Authorization: Bearer &lt;key&gt;` on every request. Required by
        /// OpenAI-compatible servers that gate access (OpenAI, hosted speaches,
        /// pfrankov/whisper-server when configured with auth, etc.). Leave
        /// empty for unauthenticated local servers.
        /// </summary>
        public string RemoteWhisperApiKey { get; set; } = "";

        public List<string> EnabledLibraries { get; set; } = new List<string>();

        public PluginConfiguration()
        {
        }
    }
}
