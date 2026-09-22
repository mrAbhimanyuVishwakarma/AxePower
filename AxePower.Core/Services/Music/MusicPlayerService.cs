using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The AxePower Music player. Owns the <see cref="PlaybackQueue"/>, drives the
/// <see cref="IAudioEngine"/> and publishes immutable <see cref="PlayerState"/> snapshots.
///
/// All operations — user commands and engine events alike — run one at a time behind a single
/// gate, so a track ending while the user presses Next cannot interleave.
/// </summary>
public sealed class MusicPlayerService : IMusicPlayerService, IDisposable
{
    public const int MaxConsecutiveFailures = 3;

    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PeriodicSave = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EditSaveDelay = TimeSpan.FromSeconds(2);

    private readonly IAudioEngine _engine;
    private readonly PlaybackStateStore _store;
    private readonly LooseTrackReader _trackReader;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly LogService _log;
    private readonly IClock _clock;
    private readonly PlaybackQueue _queue;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _positionTimer;
    private readonly Timer _saveTimer;

    private PlayerState _state = PlayerState.Empty;
    private IReadOnlyList<QueueItem> _queueSnapshot = Array.Empty<QueueItem>();
    private bool _queueDirty;

    private bool _playing;
    private bool _completed;
    private TimeSpan _position;
    private TimeSpan _duration;
    private double _volume = 1.0;
    private bool _muted;
    private double _speed = 1.0;
    private double _pitch;
    private OutputInfo? _output;
    private string? _error;
    private bool _allowBoost;
    private bool _settingsApplied;

    private Guid? _loadedKey;          // what the engine has loaded, if anything
    private Guid? _repeatOneKey;       // the engine's key for a preloaded Repeat One replay
    private int _consecutiveFailures;
    private DateTime _lastSaveUtc;
    private CancellationTokenSource? _loadCts;
    private bool _shutDown;

    public MusicPlayerService(
        IAudioEngine engine,
        PlaybackStateStore store,
        LooseTrackReader trackReader,
        SettingsService settings,
        NotificationService notifications,
        LogService log,
        IClock clock)
        : this(engine, store, trackReader, settings, notifications, log, clock, new PlaybackQueue())
    {
    }

    internal MusicPlayerService(
        IAudioEngine engine,
        PlaybackStateStore store,
        LooseTrackReader trackReader,
        SettingsService settings,
        NotificationService notifications,
        LogService log,
        IClock clock,
        PlaybackQueue queue)
    {
        _engine = engine;
        _store = store;
        _trackReader = trackReader;
        _settings = settings;
        _notifications = notifications;
        _log = log;
        _clock = clock;
        _queue = queue;

        _positionTimer = new Timer(_ => OnPositionTick(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer = new Timer(_ => _ = SaveNowAsync(), null, Timeout.Infinite, Timeout.Infinite);

        _engine.TrackStarted += (_, key) => _ = RunGatedAsync(() => OnTrackStartedAsync(key));
        _engine.TrackEnded += (_, key) => _ = RunGatedAsync(() => OnTrackEndedAsync(key));
        _engine.Error += (_, error) => _ = RunGatedAsync(() => OnEngineErrorAsync(error));
        _engine.DeviceLost += (_, _) => _ = RunGatedAsync(OnDeviceLostAsync);
        _engine.OutputChanged += (_, info) => _ = RunGatedAsync(() => { _output = info; Publish(); return Task.CompletedTask; });

        _settings.OnSettingsChanged += (_, s) => ApplyMusicSettings(s.Music);
    }

    public PlayerState State => _state;

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;

    // ── Startup / shutdown ──────────────────────────────────────────────────

    public Task InitializeAsync(CancellationToken ct) => RunGatedAsync(async () =>
    {
        ApplyMusicSettings(_settings.GetSettings().Music);

        var saved = _store.Load();
        if (saved == null)
        {
            Publish();
            return;
        }

        // Checking files and reading tags can take a while for a long queue: keep it off the caller's thread
        int dropped = 0;
        var state = await Task.Run(() => saved.WithoutMissingFiles(File.Exists, out dropped), ct);
        if (dropped > 0)
            _log.LogInfo($"[Music] {dropped} saved queue entr{(dropped == 1 ? "y was" : "ies were")} skipped because the file is gone.");

        var tracks = await Task.Run(() => state.Queue.Select(e => _trackReader.Read(e.Path)).ToList(), ct);

        var items = tracks.Select(QueueItem.For).ToList();
        var originalOrder = state.Shuffle && state.OriginalOrder != null
            ? state.OriginalOrder.Where(i => i >= 0 && i < items.Count).Select(i => items[i].Key).ToList()
            : null;

        _queue.Restore(items, state.Index, originalOrder);
        _queue.Repeat = state.Repeat;
        _queueDirty = true;

        _volume = Math.Clamp(state.Volume, 0, _allowBoost ? 2.0 : 1.0);
        _muted = state.Muted;
        _speed = Math.Clamp(state.Speed, 0.5, 2.0);
        _pitch = Math.Clamp(state.PitchSemitones, -12, 12);
        _engine.SetVolume(_volume, _muted);

        if (_queue.Current != null)
        {
            bool resume = _settings.GetSettings().Music.ResumePlaybackOnStartup;
            await LoadCurrentAsync(TimeSpan.FromMilliseconds(Math.Max(0, state.PositionMs)), play: resume, countFailure: true);
        }

        Publish();
    });

    public async Task ShutdownAsync()
    {
        if (_shutDown) return;

        await RunGatedAsync(() =>
        {
            _shutDown = true;
            _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (_loadedKey != null) _position = _engine.Position;
            _store.Save(Snapshot());
            return Task.CompletedTask;
        });

        await _engine.DisposeAsync();
    }

    // ── Opening ─────────────────────────────────────────────────────────────

    public Task OpenAsync(IReadOnlyList<Track> tracks, int startIndex = 0, bool play = true, bool shuffle = false, CancellationToken ct = default) =>
        RunGatedAsync(async () =>
        {
            try
            {
                _queue.Open(tracks, startIndex, shuffle);
            }
            catch (QueueFullException ex)
            {
                _notifications.Show(ex.Message);
                return;
            }

            _queueDirty = true;
            _consecutiveFailures = 0;
            _error = null;

            // LoadCurrentAsync tells the engine what follows once the new track is loaded
            if (_queue.Current != null)
            {
                await LoadCurrentAsync(TimeSpan.Zero, play, countFailure: true);
            }
            else
            {
                await StopEngineAsync();
                UpdateNext();
            }

            Publish();
            ScheduleSave();
        });

    public async Task OpenPathsAsync(IEnumerable<string> filesOrFolders, bool play = true, CancellationToken ct = default)
    {
        var tracks = await ReadPathsAsync(filesOrFolders, ct);
        if (tracks.Count == 0)
        {
            _notifications.Show("No playable audio files were found.");
            return;
        }
        await OpenAsync(tracks, 0, play, shuffle: false, ct);
    }

    public async Task AddPathsAsync(IEnumerable<string> filesOrFolders, CancellationToken ct = default)
    {
        var tracks = await ReadPathsAsync(filesOrFolders, ct);
        if (tracks.Count == 0)
        {
            _notifications.Show("No playable audio files were found.");
            return;
        }
        AddToQueue(tracks);
    }

    /// <summary>Expands folders (recursively) and reads each supported file. Unsupported files are skipped.</summary>
    private Task<List<Track>> ReadPathsAsync(IEnumerable<string> filesOrFolders, CancellationToken ct) => Task.Run(() =>
    {
        var files = new List<string>();
        foreach (var path in filesOrFolders)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                };
                files.AddRange(Directory.EnumerateFiles(path, "*", options)
                    .Where(DecoderFactory.IsSupported)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path) && DecoderFactory.IsSupported(path))
            {
                files.Add(path);
            }

            if (files.Count > PlaybackQueue.MaxItems) break;
        }

        var tracks = new List<Track>(Math.Min(files.Count, PlaybackQueue.MaxItems + 1));
        foreach (var file in files.Take(PlaybackQueue.MaxItems + 1))
        {
            ct.ThrowIfCancellationRequested();
            tracks.Add(_trackReader.Read(file));
        }
        return tracks;
    }, ct);

    // ── Transport ───────────────────────────────────────────────────────────

    public Task PlayAsync() => RunGatedAsync(PlayCoreAsync);

    private async Task PlayCoreAsync()
    {
        if (_queue.Current == null) return;

        _error = null;
        _consecutiveFailures = 0;

        if (_loadedKey != _queue.Current.Key)
        {
            await LoadCurrentAsync(_completed ? TimeSpan.Zero : _position, play: true, countFailure: true);
        }
        else
        {
            if (_completed) await _engine.SeekAsync(TimeSpan.Zero, CancellationToken.None);
            _engine.Play();
            _playing = true;
            _completed = false;
        }

        StartPositionTimer();
        Publish();
    }

    public Task PauseAsync() => RunGatedAsync(PauseCoreAsync);

    private Task PauseCoreAsync()
    {
        if (!_playing) return Task.CompletedTask;

        _engine.Pause();
        _playing = false;
        _position = _engine.Position;
        StopPositionTimer();
        Publish();
        _ = SaveNowAsync();
        return Task.CompletedTask;
    }

    public Task TogglePlayPauseAsync() => RunGatedAsync(() => _playing ? PauseCoreAsync() : PlayCoreAsync());

    public Task NextAsync() => RunGatedAsync(async () =>
    {
        var step = _queue.Next();
        await ApplyStepAsync(step, play: _playing);
        Publish();
        ScheduleSave();
    });

    public Task PreviousAsync() => RunGatedAsync(async () =>
    {
        var position = _loadedKey != null ? _engine.Position : _position;
        var step = _queue.Previous(position);
        await ApplyStepAsync(step, play: _playing);
        Publish();
        ScheduleSave();
    });

    public Task JumpAsync(int index) => RunGatedAsync(async () =>
    {
        var step = _queue.Jump(index);
        await ApplyStepAsync(step, play: true);
        Publish();
        ScheduleSave();
    });

    public Task SeekAsync(TimeSpan position) => RunGatedAsync(async () =>
    {
        if (_queue.Current == null) return;

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (_duration > TimeSpan.Zero && position > _duration) position = _duration;

        if (_loadedKey == _queue.Current.Key)
            await _engine.SeekAsync(position, CancellationToken.None);

        _position = position;
        _completed = false;
        Publish();
        PositionChanged?.Invoke(this, position);
    });

    // ── Levels & modes ──────────────────────────────────────────────────────

    public void SetVolume(double volume) => RunGatedSync(() =>
    {
        _volume = Math.Clamp(double.IsNaN(volume) ? 1.0 : volume, 0, _allowBoost ? 2.0 : 1.0);
        if (_volume > 0) _muted = false;
        _engine.SetVolume(_volume, _muted);
        Publish();
        ScheduleSave();
    });

    public void ToggleMute() => RunGatedSync(() =>
    {
        _muted = !_muted;
        _engine.SetVolume(_volume, _muted);
        Publish();
        ScheduleSave();
    });

    public void SetShuffle(bool on) => RunGatedSync(() =>
    {
        _queue.SetShuffle(on);
        _queueDirty = true;
        UpdateNext();
        Publish();
        ScheduleSave();
    });

    public void SetRepeat(RepeatMode mode) => RunGatedSync(() =>
    {
        _queue.Repeat = mode;
        UpdateNext();
        Publish();
        ScheduleSave();
    });

    /// <summary>Stored now; applied by the tempo stage in Phase 5.</summary>
    public void SetSpeed(double speed) => RunGatedSync(() =>
    {
        _speed = Math.Clamp(speed, 0.5, 2.0);
        Publish();
        ScheduleSave();
    });

    /// <summary>Stored now; applied by the pitch stage in Phase 5.</summary>
    public void SetPitch(double semitones) => RunGatedSync(() =>
    {
        _pitch = Math.Clamp(semitones, -12, 12);
        Publish();
        ScheduleSave();
    });

    /// <summary>Mix arrives in Phase 6.</summary>
    public void SetMix(bool on)
    {
    }

    // ── Queue editing ───────────────────────────────────────────────────────

    public void PlayNext(IEnumerable<Track> tracks) => EditQueue(() => _queue.PlayNext(tracks));

    public void AddToQueue(IEnumerable<Track> tracks) => EditQueue(() => _queue.AddToQueue(tracks));

    public void Insert(int index, Track track) => EditQueue(() => _queue.Insert(index, track));

    public void Move(int from, int to) => EditQueue(() => _queue.Move(from, to));

    public void Remove(int index) => _ = RunGatedAsync(async () =>
    {
        var outcome = _queue.Remove(index);
        _queueDirty = true;

        switch (outcome)
        {
            case RemoveOutcome.CurrentReplaced:
                await LoadCurrentAsync(TimeSpan.Zero, _playing, countFailure: true);
                break;
            case RemoveOutcome.Stopped:
                _playing = false;
                await LoadCurrentAsync(TimeSpan.Zero, play: false, countFailure: true);
                break;
            case RemoveOutcome.Emptied:
                await StopEngineAsync();
                break;
        }

        UpdateNext();
        Publish();
        ScheduleSave();
    });

    public void ClearQueue() => _ = RunGatedAsync(async () =>
    {
        _queue.Clear();
        _queueDirty = true;
        await StopEngineAsync();
        UpdateNext();
        Publish();
        ScheduleSave();
    });

    private void EditQueue(Action edit) => _ = RunGatedAsync(async () =>
    {
        bool wasEmpty = _queue.Current == null;
        try
        {
            edit();
        }
        catch (QueueFullException ex)
        {
            _notifications.Show(ex.Message);
            return;
        }

        _queueDirty = true;

        // Adding to an empty queue selects the first entry, ready to play
        if (wasEmpty && _queue.Count > 0 && _queue.Jump(0) == QueueStep.Play)
            await LoadCurrentAsync(TimeSpan.Zero, play: false, countFailure: true);

        UpdateNext();
        Publish();
        ScheduleSave();
    });

    // ── Automation hooks ────────────────────────────────────────────────────

    public async Task FadeOutAndPauseAsync(TimeSpan duration)
    {
        await _engine.FadeToAsync(0, duration, CancellationToken.None);
        await PauseAsync();
        await _engine.FadeToAsync(1, TimeSpan.Zero, CancellationToken.None);
    }

    public Task DuckAsync(double level, TimeSpan fade) =>
        _engine.FadeToAsync(Math.Clamp(level, 0, 1), fade, CancellationToken.None);

    public Task UnduckAsync(TimeSpan fade) =>
        _engine.FadeToAsync(1, fade, CancellationToken.None);

    // ── Engine events ───────────────────────────────────────────────────────

    private Task OnTrackStartedAsync(Guid key)
    {
        int index = _queue.IndexOfKey(key);
        bool repeatOneReplay = index < 0 && key == _repeatOneKey;
        if (index < 0 && !repeatOneReplay) return Task.CompletedTask; // stale

        bool gaplessAdvance = repeatOneReplay || index != _queue.Index;
        if (!repeatOneReplay) _queue.Jump(index);

        _loadedKey = repeatOneReplay ? _queue.Current?.Key : key;
        if (gaplessAdvance)
        {
            // A joined track begins at 0; a directly loaded one keeps its start position
            _position = TimeSpan.Zero;
            _consecutiveFailures = 0;
        }

        _completed = false;
        _duration = _engine.Duration > TimeSpan.Zero ? _engine.Duration : _queue.Current?.Track.Duration ?? TimeSpan.Zero;
        UpdateNext();
        Publish();
        if (gaplessAdvance) ScheduleSave();
        return Task.CompletedTask;
    }

    private async Task OnTrackEndedAsync(Guid key)
    {
        if (_queue.Current == null || (key != _loadedKey && key != _repeatOneKey)) return;

        var step = _queue.AdvanceAfterEnd();
        await ApplyStepAsync(step, play: true);
        Publish();
        ScheduleSave();
    }

    private async Task OnEngineErrorAsync(EngineError error)
    {
        var current = _queue.Current;
        if (current == null || (error.Key != current.Key && error.Key != _loadedKey && error.Key != Guid.Empty)) return;

        _log.LogWarning($"[Music] Playback error: {error.Message}");
        await HandleFailureAsync(current, play: true);
        Publish();
    }

    private Task OnDeviceLostAsync()
    {
        if (_playing)
        {
            _playing = false;
            _position = _engine.Position;
            StopPositionTimer();
            _notifications.Show("Output device disconnected — music paused.");
        }
        Publish();
        return Task.CompletedTask;
    }

    // ── Core helpers (run inside the gate) ──────────────────────────────────

    private async Task ApplyStepAsync(QueueStep step, bool play)
    {
        switch (step)
        {
            case QueueStep.Play:
                await LoadCurrentAsync(TimeSpan.Zero, play, countFailure: true);
                break;

            case QueueStep.Restart:
                if (_loadedKey == _queue.Current?.Key)
                {
                    await _engine.SeekAsync(TimeSpan.Zero, CancellationToken.None);
                    if (play && !_playing)
                    {
                        _engine.Play();
                        _playing = true;
                    }
                    _position = TimeSpan.Zero;
                    _completed = false;
                    if (_playing) StartPositionTimer();
                }
                else
                {
                    await LoadCurrentAsync(TimeSpan.Zero, play, countFailure: true);
                }
                break;

            case QueueStep.Stop:
                // End of the queue: the last track waits at 0, paused
                await LoadCurrentAsync(TimeSpan.Zero, play: false, countFailure: false);
                _completed = true;
                break;
        }
    }

    private async Task LoadCurrentAsync(TimeSpan start, bool play, bool countFailure)
    {
        var item = _queue.Current;
        if (item == null) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            await _engine.LoadAsync(item, start, play, ct);
            _loadedKey = item.Key;
            _playing = play;
            _completed = false;
            _position = start;
            _duration = _engine.Duration > TimeSpan.Zero ? _engine.Duration : item.Track.Duration;
            UpdateNext();
            if (play) StartPositionTimer(); else StopPositionTimer();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Could not open '{Path.GetFileName(item.Track.Path)}': {ex.Message}");
            if (countFailure) await HandleFailureAsync(item, play);
        }
    }

    /// <summary>
    /// A track that cannot be played is skipped with a notice. After three failures in a row,
    /// playback pauses with an error instead of racing through the queue.
    /// </summary>
    private async Task HandleFailureAsync(QueueItem failed, bool play)
    {
        _consecutiveFailures++;
        _notifications.Show($"Couldn't play '{failed.Track.Title}' — skipped");

        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            _engine.Pause();
            _playing = false;
            StopPositionTimer();
            _error = "Playback paused: several tracks in a row couldn't be played.";
            return;
        }

        var step = _queue.Next();
        if (step == QueueStep.Play)
        {
            await LoadCurrentAsync(TimeSpan.Zero, play, countFailure: true);
        }
        else
        {
            _playing = false;
            _completed = true;
            StopPositionTimer();
        }
    }

    private async Task StopEngineAsync()
    {
        _loadCts?.Cancel();
        await _engine.StopAsync();
        _loadedKey = null;
        _playing = false;
        _completed = false;
        _position = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        StopPositionTimer();
    }

    private void UpdateNext()
    {
        var next = _queue.PeekAfterEnd();
        _repeatOneKey = _queue.Repeat == RepeatMode.One ? next?.Key : null;
        _engine.SetNext(next);
    }

    private void ApplyMusicSettings(MusicSettings music)
    {
        bool allowBoost = music.AllowVolumeBoost;
        if (allowBoost == _allowBoost && _settingsApplied) return;

        _settingsApplied = true;
        _allowBoost = allowBoost;
        _engine.SetDsp(DspSettings.Neutral with { AllowBoost = allowBoost });

        if (!allowBoost && _volume > 1.0)
        {
            _volume = 1.0;
            _engine.SetVolume(_volume, _muted);
            Publish();
        }
    }

    private void Publish()
    {
        if (_queueDirty)
        {
            _queueSnapshot = _queue.Items.ToArray();
            _queueDirty = false;
        }

        _state = new PlayerState(
            _queueSnapshot,
            _queue.Index,
            _playing,
            Buffering: false,
            _completed,
            _position,
            _duration,
            _volume,
            _muted,
            _queue.Shuffle,
            _queue.Repeat,
            _speed,
            _pitch,
            MixOffset: null,
            _output,
            _error);

        StateChanged?.Invoke(this, _state);
    }

    // ── Position & persistence ──────────────────────────────────────────────

    private void StartPositionTimer() => _positionTimer.Change(PositionInterval, PositionInterval);

    private void StopPositionTimer() => _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);

    private void OnPositionTick()
    {
        if (!_playing || _loadedKey == null) return;

        var position = _engine.Position;
        _position = position;
        PositionChanged?.Invoke(this, position);

        if (_clock.UtcNow - _lastSaveUtc >= PeriodicSave) _ = SaveNowAsync();
    }

    private void ScheduleSave() => _saveTimer.Change(EditSaveDelay, Timeout.InfiniteTimeSpan);

    private Task SaveNowAsync() => RunGatedAsync(() =>
    {
        if (_shutDown) return Task.CompletedTask;
        if (_playing && _loadedKey != null) _position = _engine.Position;
        _store.Save(Snapshot());
        return Task.CompletedTask;
    });

    private PersistedPlaybackState Snapshot()
    {
        _lastSaveUtc = _clock.UtcNow;
        var items = _queue.Items;
        var indexByKey = new Dictionary<Guid, int>(items.Count);
        for (int i = 0; i < items.Count; i++) indexByKey[items[i].Key] = i;

        return new PersistedPlaybackState
        {
            Queue = items.Select(i => new PersistedQueueEntry { TrackId = i.Track.Id, Path = i.Track.Path }).ToList(),
            OriginalOrder = _queue.OriginalOrder?.Where(indexByKey.ContainsKey).Select(k => indexByKey[k]).ToList(),
            Index = _queue.Index,
            PositionMs = (long)_position.TotalMilliseconds,
            Volume = _volume,
            Muted = _muted,
            Shuffle = _queue.Shuffle,
            Repeat = _queue.Repeat,
            Speed = _speed,
            PitchSemitones = _pitch
        };
    }

    // ── Gate ────────────────────────────────────────────────────────────────

    /// <summary>Completes once every operation queued before it has finished (used by tests).</summary>
    internal Task WhenIdleAsync() => RunGatedAsync(() => Task.CompletedTask);

    private async Task RunGatedAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _log.LogError("[Music] Player operation failed.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RunGatedSync(Action operation) => _ = RunGatedAsync(() =>
    {
        operation();
        return Task.CompletedTask;
    });

    public void Dispose()
    {
        _positionTimer.Dispose();
        _saveTimer.Dispose();
        _loadCts?.Dispose();
    }
}
