using System;
using System.Collections.ObjectModel;
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

namespace AxePower.App.ViewModels.Music;

/// <summary>Loads lyrics for the current track and maps position ticks to one active line.</summary>
public partial class LyricsViewModel : ObservableObject, IDisposable
{
    private readonly IMusicPlayerService _player;
    private readonly ILyricsService _online;
    private readonly ITagReader _tags;
    private readonly SettingsService _settings;
    private readonly MusicDispatcher _dispatcher;
    private CancellationTokenSource? _loadCts;
    private Guid? _currentKey;
    private bool _applyingSettings;

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

    private void OnPlayerStateChanged(object? sender, PlayerState state) =>
        _dispatcher.Post(() => BeginLoad(state.Current));

    private void OnPositionChanged(object? sender, TimeSpan position) =>
        _dispatcher.PostPosition(position, UpdateActiveLine);

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
                    ? $"Lyrics by {result.Provider}"
                    : OnlineEnabled || _player.State.Current?.Track.IsLocal == false
                        ? "No lyrics were found for this track."
                        : "Online lyrics are off. Enable them in Music Settings.");

        OnPropertyChanged(nameof(HasSyncedLyrics));
        OnPropertyChanged(nameof(HasPlainLyrics));
        OnPropertyChanged(nameof(HasAnyLyrics));
        UpdateActiveLine(_player.State.Position);
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
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _player.StateChanged -= OnPlayerStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _settings.OnSettingsChanged -= OnSettingsChanged;
    }
}
