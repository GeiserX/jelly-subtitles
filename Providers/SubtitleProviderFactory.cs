using Microsoft.Extensions.Logging;
using WhisperSubs.Configuration;

namespace WhisperSubs.Providers
{
    internal static class SubtitleProviderFactory
    {
        public static ISubtitleProvider Create(PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            if (!string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl))
            {
                var model = string.IsNullOrWhiteSpace(config.RemoteWhisperModel)
                    ? "Systran/faster-whisper-large-v3"
                    : config.RemoteWhisperModel.Trim();
                return new RemoteWhisperProvider(
                    loggerFactory.CreateLogger<RemoteWhisperProvider>(),
                    config.RemoteWhisperApiUrl,
                    model);
            }

            return new WhisperProvider(
                loggerFactory.CreateLogger<WhisperProvider>(),
                config.WhisperModelPath,
                config.WhisperBinaryPath,
                config.WhisperThreadCount);
        }
    }
}
