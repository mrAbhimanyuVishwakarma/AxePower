using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Live signal-processing settings. Neutral values make each stage a pass-through.</summary>
public sealed record DspSettings(
    ReplayGainMode ReplayGain,
    double PreampDb,
    bool PreventClipping,
    double FallbackGainDb,
    TimeSpan Crossfade,
    bool CrossfadeSkipSameAlbum,
    double Speed,
    double PitchSemitones,
    bool AllowBoost)
{
    public static DspSettings Neutral { get; } = new(ReplayGainMode.Off, 0, true, 0, TimeSpan.Zero, true, 1.0, 0, false);
}

/// <summary>Output device configuration. A null device id follows the Windows default device.</summary>
public sealed record OutputSettings(
    string? DeviceId,
    bool Exclusive,
    bool MatchSourceRate,
    int SharedLatencyMs);

/// <summary>A decode or output failure for one queue entry.</summary>
public sealed record EngineError(Guid Key, string Message, Exception? Exception);

/// <summary>
/// Decodes and plays audio. Implementations raise every event on background threads and never
/// while holding a lock; callers marshal to the UI thread themselves.
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    /// <summary>The queue entry whose audio just began, including gapless and crossfade boundaries.</summary>
    event EventHandler<Guid>? TrackStarted;

    /// <summary>Natural end of a track when no next entry was preloaded.</summary>
    event EventHandler<Guid>? TrackEnded;

    event EventHandler<EngineError>? Error;
    event EventHandler<OutputInfo>? OutputChanged;
    event EventHandler? DeviceLost;

    Task LoadAsync(QueueItem item, TimeSpan startAt, bool play, CancellationToken ct);

    /// <summary>What to preload for gapless or crossfade playback; null means nothing follows.</summary>
    void SetNext(QueueItem? next);

    void Play();
    void Pause();
    Task StopAsync();
    Task SeekAsync(TimeSpan position, CancellationToken ct);

    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }

    /// <summary>Volume 0.0–2.0. Values above 1.0 are honoured only when boost is allowed.</summary>
    void SetVolume(double volume, bool muted);

    /// <summary>
    /// Ramps a gain multiplier on top of the volume (1 = normal) — used for ducking and
    /// fade-outs. Completes when the target is reached, or at once while nothing is playing.
    /// </summary>
    Task FadeToAsync(double gain, TimeSpan duration, CancellationToken ct);

    /// <summary>Applied live; stages turn into pass-throughs when neutral.</summary>
    void SetDsp(DspSettings dsp);

    /// <summary>Applied at the next safe point: while paused, at a track boundary, or with a short fade.</summary>
    Task ConfigureOutputAsync(OutputSettings output, CancellationToken ct);

    /// <summary>Latest 32-bar snapshot (0–1), or null when the spectrum is disabled.</summary>
    float[]? GetSpectrum();

    bool SpectrumEnabled { get; set; }
}
