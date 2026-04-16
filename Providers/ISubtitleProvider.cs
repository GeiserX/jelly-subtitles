using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    public interface ISubtitleProvider
    {
        string Name { get; }
        Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false);

        /// <summary>
        /// Detects the language spoken in an audio file.
        /// Returns the ISO 639-1 language code and confidence probability.
        /// </summary>
        Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken);
    }
}
