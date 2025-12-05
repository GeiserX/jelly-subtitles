using System.Threading;
using System.Threading.Tasks;

namespace JellySubtitles.Providers
{
    public interface ISubtitleProvider
    {
        string Name { get; }
        Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken);
    }
}
