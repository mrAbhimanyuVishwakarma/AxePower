using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Resolves and caches cover art: embedded image first, then a folder image.</summary>
public interface ICoverArtService
{
    /// <summary>Path of a cached JPEG at the requested size (128, 256 or 512), or null when there is no cover.</summary>
    Task<string?> GetCoverFileAsync(Track track, int size, CancellationToken ct);

    /// <summary>The cover's dominant colour, or null when no colour qualifies (e.g. a grey cover).</summary>
    Task<(byte R, byte G, byte B)?> GetDominantColorAsync(Track track, CancellationToken ct);
}
