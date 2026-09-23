using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>Works out which hardware encoders this PC can really use.</summary>
public interface IShareEncoderProbe
{
    /// <summary>
    /// Encoders FFmpeg both advertises and manages to initialise. A verified list matters because
    /// FFmpeg happily lists NVENC on a machine whose driver will refuse it, and offering the user
    /// a choice that fails at Start is worse than not offering it.
    /// </summary>
    Task<IReadOnlyList<ShareEncoder>> ProbeAsync(string ffmpegPath, CancellationToken ct);

    /// <summary>
    /// The encoder to use for a codec when the user has not forced one: NVENC, then AMF, then QSV,
    /// then software. Returns null when nothing can encode that codec.
    /// </summary>
    ShareEncoder? SelectBest(IReadOnlyList<ShareEncoder> available, ShareVideoCodec codec, string? preferredFfmpegName);
}
