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
                return new RemoteWhisperProvider(
                    loggerFactory.CreateLogger<RemoteWhisperProvider>(),
                    config.RemoteWhisperApiUrl,
                    config.RemoteWhisperModel);
            }

            return new WhisperProvider(
                loggerFactory.CreateLogger<WhisperProvider>(),
                config.WhisperModelPath,
                config.WhisperBinaryPath,
                config.WhisperThreadCount);
        }
    }
}
