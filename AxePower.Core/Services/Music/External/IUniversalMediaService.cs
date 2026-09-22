using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Shows and controls other apps' media through Windows media sessions. Every command returns
/// whether the app accepted it; a refusal is reported, never hidden.
/// </summary>
public interface IUniversalMediaService : IDisposable
{
    Task InitializeAsync(CancellationToken ct);

    /// <summary>The best external session. AxePower's own session is never returned.</summary>
    ExternalMediaSession? Current { get; }

    event EventHandler<ExternalMediaSession?>? CurrentChanged;

    Task<bool> TogglePlayPauseAsync();
    Task<bool> NextAsync();
    Task<bool> PreviousAsync();
    Task<bool> SeekAsync(TimeSpan position);
    Task<bool> PauseAsync();
}
