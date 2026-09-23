using System;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>Where a required external tool ended up, and whether it is usable.</summary>
/// <param name="Path">Full path to the executable, or empty when it is not installed.</param>
public sealed record ShareToolStatus(string Name, string Path, string Version, bool IsAvailable)
{
    public static ShareToolStatus Missing(string name) => new(name, string.Empty, string.Empty, false);
}

/// <summary>
/// Locates (and, on request, downloads) the two binaries Share Screen depends on: FFmpeg for
/// capture and encoding, and MediaMTX for fanning one uplink out to several viewers.
///
/// Both are fetched into the per-user tools directory, so nothing here needs administrator rights
/// and nothing is written outside <c>%LOCALAPPDATA%\AxePower</c>.
/// </summary>
public interface IShareToolProvider
{
    /// <summary>Looks for FFmpeg without downloading anything.</summary>
    ShareToolStatus FindFfmpeg();

    /// <summary>Looks for MediaMTX without downloading anything.</summary>
    ShareToolStatus FindMediaMtx();

    /// <summary>
    /// Downloads FFmpeg if it is not already present. Reports 0.0-1.0 progress; returns the status
    /// afterwards so the caller can tell success from a failed download.
    /// </summary>
    Task<ShareToolStatus> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken ct);

    /// <summary>Downloads MediaMTX if it is not already present.</summary>
    Task<ShareToolStatus> EnsureMediaMtxAsync(IProgress<double>? progress, CancellationToken ct);
}
