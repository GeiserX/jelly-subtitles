using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Providers
{
    public class RemoteWhisperProvider : ISubtitleProvider
    {
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        private readonly ILogger _logger;
        private readonly string _apiUrl;
        private readonly string _model;

        public string Name => "RemoteWhisper";

        public RemoteWhisperProvider(ILogger logger, string apiUrl, string model)
        {
            _logger = logger;
            _apiUrl = apiUrl.TrimEnd('/');
            _model = model;
        }

        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            var endpoint = translate
                ? $"{_apiUrl}/v1/audio/translations"
                : $"{_apiUrl}/v1/audio/transcriptions";

            _logger.LogInformation("Sending audio to remote Whisper API: {Endpoint} [lang={Language}, translate={Translate}]",
                endpoint, language, translate);

            using var content = new MultipartFormDataContent();

            var audioBytes = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(audioBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("srt"), "response_format");

            if (!string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new StringContent(language), "language");
            }

            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"Remote Whisper API returned {(int)response.StatusCode}: {errorBody}");
            }

            var srt = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(srt))
            {
                throw new InvalidOperationException("Remote Whisper API returned empty response");
            }

            _logger.LogInformation("Remote transcription complete, received {Length} characters of SRT", srt.Length);
            return srt;
        }

        public async Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            _logger.LogInformation("Detecting language via remote Whisper API for {AudioPath}", audioPath);

            using var content = new MultipartFormDataContent();

            var audioBytes = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(audioBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("verbose_json"), "response_format");

            var endpoint = $"{_apiUrl}/v1/audio/transcriptions";
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"Remote Whisper API returned {(int)response.StatusCode}: {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var language = root.TryGetProperty("language", out var langProp)
                ? (langProp.GetString() ?? "auto")
                : "auto";

            language = NormalizeLangName(language);

            _logger.LogInformation("Remote language detection: {Language}", language);

            return (language, 0.0f);
        }

        private static string NormalizeLangName(string lang)
        {
            if (lang.Length <= 3) return lang;

            return lang.ToLowerInvariant() switch
            {
                "english" => "en",
                "spanish" => "es",
                "french" => "fr",
                "german" => "de",
                "italian" => "it",
                "portuguese" => "pt",
                "russian" => "ru",
                "japanese" => "ja",
                "chinese" => "zh",
                "korean" => "ko",
                "dutch" => "nl",
                "polish" => "pl",
                "turkish" => "tr",
                "arabic" => "ar",
                "hindi" => "hi",
                "czech" => "cs",
                "greek" => "el",
                "hungarian" => "hu",
                "romanian" => "ro",
                "swedish" => "sv",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "catalan" => "ca",
                "ukrainian" => "uk",
                "vietnamese" => "vi",
                "thai" => "th",
                "indonesian" => "id",
                "malay" => "ms",
                "hebrew" => "he",
                _ => lang,
            };
        }
    }
}
