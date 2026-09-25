using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

public interface ILyricsService
{
    /// <summary>
    /// Finds lyrics for a complete track. The source reference is intentionally preserved so
    /// providers such as YouTube can supply timed captions when catalogue lookups have no match.
    /// </summary>
    Task<LyricsResult?> GetLyricsAsync(Track track, CancellationToken ct = default);
}
