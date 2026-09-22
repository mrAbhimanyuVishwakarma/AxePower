using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The one AxePower Music player. Owns the queue and drives <see cref="IAudioEngine"/>; every
/// surface (Music page, mini player, tray, dashboard, phone) reads <see cref="State"/>.
/// </summary>
public interface IMusicPlayerService
{
    PlayerState State { get; }

    /// <summary>Any change except plain position ticks.</summary>
    event EventHandler<PlayerState>? StateChanged;

    /// <summary>Throttled by the active power profile (4 Hz, or 1 Hz in Eco).</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    Task InitializeAsync(CancellationToken ct);
    Task OpenAsync(IReadOnlyList<Track> tracks, int startIndex = 0, bool play = true, bool shuffle = false, CancellationToken ct = default);
    Task OpenPathsAsync(IEnumerable<string> filesOrFolders, bool play = true, CancellationToken ct = default);
    Task PlayAsync();
    Task PauseAsync();
    Task TogglePlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task JumpAsync(int index);
    Task SeekAsync(TimeSpan position);
    void SetVolume(double volume);
    void ToggleMute();
    void SetShuffle(bool on);
    void SetRepeat(RepeatMode mode);
    void SetSpeed(double speed);
    void SetPitch(double semitones);
    void PlayNext(IEnumerable<Track> tracks);
    void AddToQueue(IEnumerable<Track> tracks);
    void Insert(int index, Track track);
    void Move(int from, int to);
    void Remove(int index);
    void ClearQueue();

    /// <summary>Auto-extends the queue from the local library (Phase 6).</summary>
    void SetMix(bool on);

    /// <summary>Used by the sleep timer.</summary>
    Task FadeOutAndPauseAsync(TimeSpan duration);

    /// <summary>Used by automations; level is a 0–1 fraction of the current volume.</summary>
    Task DuckAsync(double level, TimeSpan fade);

    Task UnduckAsync(TimeSpan fade);
    Task ShutdownAsync();
}
