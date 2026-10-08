using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.Services;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// Loads lyrics for the current track and keeps one line active in step with the song. The
/// player reports its position only every 250 ms, so between reports the position is carried
/// forward from the last one by the time since it arrived; lines change within ~40 ms of their
/// timestamp instead of up to a quarter second late.
/// </summary>
public partial class LyricsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan LineTickInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>Never carry a position forward further than this without a fresh report.</summary>
    private static readonly TimeSpan MaxExtrapolation = TimeSpan.FromSeconds(1);

    private readonly IMusicPlayerService _player;
    private readonly ILyricsService _online;
    private readonly ITagReader _tags;
    private readonly SettingsService _settings;
    private readonly MusicDispatcher _dispatcher;
    private CancellationTokenSource? _loadCts;
    private Guid? _currentKey;
    private bool _applyingSettings;

    private readonly object _clockLock = new();
    private TimeSpan _reportedPosition;
    private long _reportedAt;
    private bool _advancing;
    private double _speed = 1.0;
    private DispatcherTimer? _lineTimer;

    public LyricsViewModel(
        IMusicPlayerService player,
        ILyricsService online,
        ITagReader tags,
        SettingsService settings,
        MusicDispatcher dispatcher)
    {
        _player = player;
        _online = online;
        _tags = tags;
        _settings = settings;
        _dispatcher = dispatcher;

        ApplySettings(settings.GetSettings());
        _player.StateChanged += OnPlayerStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _settings.OnSettingsChanged += OnSettingsChanged;

        Report(_player.State.Position, _player.State);
        BeginLoad(_player.State.Current);
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncedLyrics), nameof(HasAnyLyrics))]
    private LyricLineViewModel? _activeLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlainLyrics), nameof(HasAnyLyrics))]
    private string? _plainLyrics;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Play a track to see lyrics.";

    [ObservableProperty]
    private string? _providerName;

    [ObservableProperty]
    private bool _onlineEnabled;

    [ObservableProperty]
    private bool _embeddedEnabled;

    public bool HasSyncedLyrics => Lines.Count > 0;
    public bool HasPlainLyrics => !string.IsNullOrWhiteSpace(PlainLyrics);
    public bool HasAnyLyrics => HasSyncedLyrics || HasPlainLyrics;

    partial void OnOnlineEnabledChanged(bool value)
    {
        if (_applyingSettings) return;
        var settings = _settings.GetSettings();
        settings.Music.LyricsOnline = value;
        _settings.SaveSettings(settings);
        Reload();
    }

    partial void OnEmbeddedEnabledChanged(bool value)
    {
        if (_applyingSettings) return;
        var settings = _settings.GetSettings();
        settings.Music.LyricsFromFiles = value;
        _settings.SaveSettings(settings);
        Reload();
    }

    [RelayCommand]
    private Task SeekToLine(LyricLineViewModel? line) =>
        line is null ? Task.CompletedTask : _player.SeekAsync(line.Timestamp);

    [RelayCommand]
    private void Reload() => BeginLoad(_player.State.Current, force: true);

    private void OnPlayerStateChanged(object? sender, PlayerState state)
    {
        Report(state.Position, state);
        _dispatcher.Post(() =>
        {
            BeginLoad(state.Current);
            UpdateLineTimer();
            UpdateActiveLine(EstimatedPosition());
        });
    }

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        Report(position, state: null);
        _dispatcher.PostPosition(position, _ => UpdateActiveLine(EstimatedPosition()));
    }

    /// <summary>Stamps a reported position with when it arrived, on the player's thread.</summary>
    private void Report(TimeSpan position, PlayerState? state)
    {
        lock (_clockLock)
        {
            _reportedPosition = position;
            _reportedAt = Stopwatch.GetTimestamp();
            if (state is not null)
            {
                _advancing = state.Playing && !state.Buffering && state.Current is not null;
                _speed = state.Speed > 0 ? state.Speed : 1.0;
            }
        }
    }

    /// <summary>Where the song is now: the last report, moved on by the time since while playing.</summary>
    internal TimeSpan EstimatedPosition()
    {
        lock (_clockLock)
        {
            if (!_advancing) return _reportedPosition;
            var elapsed = Stopwatch.GetElapsedTime(_reportedAt);
            if (elapsed > MaxExtrapolation) elapsed = MaxExtrapolation;
            return _reportedPosition + TimeSpan.FromTicks((long)(elapsed.Ticks * _speed));
        }
    }

    /// <summary>Runs the fine-grained line clock only while synced lyrics are playing.</summary>
    private void UpdateLineTimer()
    {
        bool needed;
        lock (_clockLock) needed = _advancing && Lines.Count > 0;

        if (!needed)
        {
            _lineTimer?.Stop();
            return;
        }

        if (_lineTimer is null)
        {
            _lineTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = LineTickInterval };
            _lineTimer.Tick += (_, _) => UpdateActiveLine(EstimatedPosition());
        }
        _lineTimer.Start();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        _dispatcher.Post(() => ApplySettings(settings));

    private void ApplySettings(AppSettings settings)
    {
        _applyingSettings = true;
        try
        {
            OnlineEnabled = settings.Music.LyricsOnline;
            EmbeddedEnabled = settings.Music.LyricsFromFiles;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void BeginLoad(QueueItem? current, bool force = false)
    {
        if (!force && current?.Key == _currentKey) return;
        _currentKey = current?.Key;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        Lines.Clear();
        ActiveLine = null;
        PlainLyrics = null;
        ProviderName = null;
        OnPropertyChanged(nameof(HasSyncedLyrics));
        OnPropertyChanged(nameof(HasAnyLyrics));

        if (current is null)
        {
            IsLoading = false;
            StatusMessage = "Play a track to see lyrics.";
            return;
        }

        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        StatusMessage = "Finding synchronized lyrics…";
        _ = LoadAsync(current, cts);
    }

    private async Task LoadAsync(QueueItem current, CancellationTokenSource cts)
    {
        LyricsResult? result = null;
        try
        {
            var track = current.Track;
            if (EmbeddedEnabled && track.IsLocal && File.Exists(track.Path))
            {
                string? embedded = await Task.Run(() => _tags.ReadEmbeddedLyrics(track.Path), cts.Token);
                if (!string.IsNullOrWhiteSpace(embedded))
                {
                    var parsed = LrcParser.Parse(embedded);
                    result = parsed.Count > 0
                        ? new LyricsResult("Embedded", parsed)
                        : new LyricsResult("Embedded", Array.Empty<LyricLine>(), embedded.Trim());
                }
            }

            // YouTube playback is already online and carries the exact video id required for
            // caption fallback, so lyrics remain part of that mode even if catalogue lookups
            // for local files have been disabled in settings.
            if (result is null && (OnlineEnabled || !track.IsLocal))
            {
                result = await _online.GetLyricsAsync(track, cts.Token);
            }

            if (cts.IsCancellationRequested) return;
            _dispatcher.Post(() => ApplyResult(current.Key, result));
        }
        catch (OperationCanceledException)
        {
            // Superseded by the next track.
        }
        catch
        {
            if (!cts.IsCancellationRequested)
                _dispatcher.Post(() => ApplyResult(current.Key, null, "Lyrics could not be loaded."));
        }
    }

    private void ApplyResult(Guid key, LyricsResult? result, string? error = null)
    {
        if (_currentKey != key) return;

        IsLoading = false;
        Lines.Clear();
        if (result is not null)
        {
            foreach (var line in result.Lines)
                Lines.Add(new LyricLineViewModel(line.Timestamp, line.Text));
        }

        PlainLyrics = result?.PlainLyrics;
        ProviderName = result?.Provider;
        StatusMessage = error
            ?? (result?.IsSynchronized == true
                ? $"Synchronized by {result.Provider}"
                : result is not null
                    ? $"Lyrics by {result.Provider}. These have no timings, so they can't follow the song."
                    : OnlineEnabled || _player.State.Current?.Track.IsLocal == false
                        ? "No lyrics were found for this track."
                        : "Online lyrics are off. Enable them in Music Settings.");

        OnPropertyChanged(nameof(HasSyncedLyrics));
        OnPropertyChanged(nameof(HasPlainLyrics));
        OnPropertyChanged(nameof(HasAnyLyrics));
        UpdateActiveLine(EstimatedPosition());
        UpdateLineTimer();
    }

    private void UpdateActiveLine(TimeSpan position)
    {
        if (Lines.Count == 0)
        {
            ActiveLine = null;
            return;
        }

        int low = 0;
        int high = Lines.Count - 1;
        int active = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (Lines[middle].Timestamp <= position)
            {
                active = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var next = active >= 0 ? Lines[active] : null;
        if (ReferenceEquals(next, ActiveLine)) return;
        if (ActiveLine is not null) ActiveLine.IsActive = false;
        ActiveLine = next;
        if (ActiveLine is not null) ActiveLine.IsActive = true;
    }

    public void Dispose()
    {
        _lineTimer?.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _player.StateChanged -= OnPlayerStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _settings.OnSettingsChanged -= OnSettingsChanged;
    }
}
