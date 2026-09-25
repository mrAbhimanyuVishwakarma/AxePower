using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Turns the timed caption track attached to a YouTube video into seekable lyrics.</summary>
public interface IYouTubeCaptionLyricsProvider
{
    Task<LyricsResult?> GetLyricsAsync(string videoId, CancellationToken ct = default);
}
