using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxePower.App.Services;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// The one Now Playing model, shared by the header player, the Music page and (later) the tray,
/// dashboard and mini player. Singleton on purpose: every surface shows the same state.
/// </summary>
public partial class NowPlayingViewModel : ObservableObject
{
    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphRepeatAll = "";
    private const string GlyphRepeatOne = "";
    private const string GlyphVolume = "";
    private const string GlyphMute = "";

    private readonly IMusicPlayerService _player;
    private readonly ICoverArtService _covers;
    private readonly SettingsService _settings;
    private readonly MusicDispatcher _dispatcher;

    private PlayerState _state = PlayerState.Empty;
    private IReadOnlyList<QueueItem>? _shownQueue;
    private Guid? _coverKey;
    private CancellationTokenSource? _coverCts;
    private bool _isDraggingSeek;
    private double _dragSeekSeconds;
    private bool _applying;

    public NowPlayingViewModel(IMusicPlayerService player, ICoverArtService covers, SettingsService settings, MusicDispatcher dispatcher)
    {
        _player = player;
        _covers = covers;
        _settings = settings;
        _dispatcher = dispatcher;

        _isHeaderPlayerOpen = settings.GetSettings().Music.DashboardPlayerExpanded;
        _allowBoost = settings.GetSettings().Music.AllowVolumeBoost;

        _player.StateChanged += (_, state) => _dispatcher.Post(() => Apply(state));
        _player.PositionChanged += (_, position) => _dispatcher.PostPosition(position, ApplyPosition);
        _settings.OnSettingsChanged += (_, s) => _dispatcher.Post(() => AllowBoost = s.Music.AllowVolumeBoost);

        Apply(_player.State);
    }

    /// <summary>Set by the shell so "Open Music" works from any surface.</summary>
    public Action? NavigateToMusic { get; set; }

    // ── Track ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _hasCurrent;
    [ObservableProperty] private string _title = "Nothing playing";
    [ObservableProperty] private string _artist = "Open files or drop them on the Music page";
    [ObservableProperty] private string _album = string.Empty;
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _upNext = string.Empty;

    public bool HasCover => Cover != null;
    partial void OnCoverChanged(ImageSource? value) => OnPropertyChanged(nameof(HasCover));

    public string ArtistAndAlbum => string.IsNullOrEmpty(Album) ? Artist : $"{Artist} — {Album}";
    partial void OnArtistChanged(string value) => OnPropertyChanged(nameof(ArtistAndAlbum));
    partial void OnAlbumChanged(string value) => OnPropertyChanged(nameof(ArtistAndAlbum));

    // ── Transport ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph), nameof(PlayPauseLabel))]
    private bool _isPlaying;

    public string PlayPauseGlyph => IsPlaying ? GlyphPause : GlyphPlay;
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    [ObservableProperty] private bool _canPrevious;
    [ObservableProperty] private bool _canNext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleLabel))]
    private bool _isShuffle;

    public string ShuffleLabel => IsShuffle ? "Shuffle: On" : "Shuffle: Off";

    // A user click on the shuffle toggle; state coming from the player is not echoed back
    partial void OnIsShuffleChanged(bool value)
    {
        if (!_applying) _player.SetShuffle(value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(RepeatGlyph), nameof(RepeatLabel))]
    private RepeatMode _repeat;

    /// <summary>Bound two-way to the repeat toggle: every click steps Off → All → One → Off.</summary>
    public bool IsRepeatOn
    {
        get => Repeat != RepeatMode.Off;
        set
        {
            if (!_applying) CycleRepeat();
        }
    }

    public string RepeatGlyph => Repeat == RepeatMode.One ? GlyphRepeatOne : GlyphRepeatAll;
    public string RepeatLabel => Repeat switch
    {
        RepeatMode.All => "Repeat: All",
        RepeatMode.One => "Repeat: One",
        _ => "Repeat: Off"
    };

    // ── Position ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(SeekMaximum))]
    private double _durationSeconds;

    /// <summary>Never 0, so an empty player shows the seek thumb at the start rather than the end.</summary>
    public double SeekMaximum => Math.Max(1, DurationSeconds);

    public string DurationText => FormatTime(TimeSpan.FromSeconds(DurationSeconds));

    private double _positionSeconds;

    /// <summary>
    /// Bound two-way to the seek slider. Player updates never call this setter (WPF does not
    /// push a source update back), so a set here is always the user: a click or arrow key seeks
    /// at once, a drag seeks only when released.
    /// </summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(_positionSeconds - value) < 0.001) return;
            _positionSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionText));

            if (_isDraggingSeek) _dragSeekSeconds = value;
            else _ = _player.SeekAsync(TimeSpan.FromSeconds(value));
        }
    }

    public string PositionText => FormatTime(TimeSpan.FromSeconds(PositionSeconds));

    public void BeginSeekDrag() => _isDraggingSeek = true;

    public void EndSeekDrag()
    {
        if (!_isDraggingSeek) return;
        _isDraggingSeek = false;
        _ = _player.SeekAsync(TimeSpan.FromSeconds(_dragSeekSeconds));
    }

    // ── Volume ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeMaximum))]
    private bool _allowBoost;

    public double VolumeMaximum => AllowBoost ? 200 : 100;

    private double _volumePercent = 100;

    /// <summary>0–100, or 0–200 with boost allowed. Bound two-way to the volume slider.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (Math.Abs(_volumePercent - value) < 0.01) return;
            _volumePercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeLabel));
            _player.SetVolume(value / 100.0);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph), nameof(VolumeLabel))]
    private bool _isMuted;

    public string VolumeGlyph => IsMuted || VolumePercent <= 0 ? GlyphMute : GlyphVolume;
    public string VolumeLabel => IsMuted ? "Muted" : $"Volume {VolumePercent:0}%";

    // ── Queue ───────────────────────────────────────────────────────────────

    [ObservableProperty] private IReadOnlyList<QueueEntryViewModel> _queue = Array.Empty<QueueEntryViewModel>();
    [ObservableProperty] private bool _hasQueue;
    [ObservableProperty] private string _queueSummary = "Queue is empty";

    // ── Header player ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderToggleGlyph))]
    private bool _isHeaderPlayerOpen;

    public string HeaderToggleGlyph => IsHeaderPlayerOpen ? "" : "";

    private bool _headerOnDashboard;
    private bool _applyingHeaderPage;

    /// <summary>
    /// Called on every navigation. The card opens on the Dashboard as the user last left it
    /// there (open by default) and starts closed on every other page; opening or closing it
    /// away from the Dashboard lasts until the next navigation and is not saved.
    /// </summary>
    public void SetHeaderPlayerPage(bool isDashboard)
    {
        _headerOnDashboard = isDashboard;
        _applyingHeaderPage = true;
        try { IsHeaderPlayerOpen = isDashboard && _settings.GetSettings().Music.DashboardPlayerExpanded; }
        finally { _applyingHeaderPage = false; }
    }

    partial void OnIsHeaderPlayerOpenChanged(bool value)
    {
        if (_applyingHeaderPage || !_headerOnDashboard) return;
        var settings = _settings.GetSettings();
        if (settings.Music.DashboardPlayerExpanded == value) return;
        settings.Music.DashboardPlayerExpanded = value;
        // UI-only state: no service needs to react, so listeners are not notified
        _settings.SaveSettings(settings, notifyListeners: false);
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task PlayPause() => _player.TogglePlayPauseAsync();

    [RelayCommand]
    private Task Next() => _player.NextAsync();

    [RelayCommand]
    private Task Previous() => _player.PreviousAsync();

    [RelayCommand]
    private void ToggleShuffle() => _player.SetShuffle(!_state.Shuffle);

    [RelayCommand]
    private void CycleRepeat() => _player.SetRepeat(_state.Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off
    });

    [RelayCommand]
    private void ToggleMute() => _player.ToggleMute();

    [RelayCommand]
    private void ChangeVolume(string? step)
    {
        if (double.TryParse(step, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delta))
            VolumePercent = Math.Clamp(VolumePercent + delta, 0, VolumeMaximum);
    }

    [RelayCommand]
    private void ToggleHeaderPlayer() => IsHeaderPlayerOpen = !IsHeaderPlayerOpen;

    [RelayCommand]
    private void OpenMusicPage() => NavigateToMusic?.Invoke();

    [RelayCommand]
    private Task OpenFiles() => PickFilesAsync(append: false);

    [RelayCommand]
    private Task AddFiles() => PickFilesAsync(append: true);

    [RelayCommand]
    private Task OpenFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a music folder", Multiselect = true };
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (Directory.Exists(music)) dialog.InitialDirectory = music;

        return dialog.ShowDialog() == true
            ? _player.OpenPathsAsync(dialog.FolderNames)
            : Task.CompletedTask;
    }

    private Task PickFilesAsync(bool append)
    {
        var patterns = string.Join(";", DecoderFactory.SupportedExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = append ? "Add to queue" : "Open music files",
            Filter = $"Audio files ({patterns})|{patterns}|All files (*.*)|*.*",
            Multiselect = true
        };
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (Directory.Exists(music)) dialog.InitialDirectory = music;

        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        return append ? _player.AddPathsAsync(dialog.FileNames) : _player.OpenPathsAsync(dialog.FileNames);
    }

    [RelayCommand]
    private Task Jump(QueueEntryViewModel? entry) =>
        entry == null ? Task.CompletedTask : _player.JumpAsync(entry.Index);

    [RelayCommand]
    private void Remove(QueueEntryViewModel? entry)
    {
        if (entry != null) _player.Remove(entry.Index);
    }

    [RelayCommand]
    private void MoveUp(QueueEntryViewModel? entry)
    {
        if (entry != null && entry.Index > 0) _player.Move(entry.Index, entry.Index - 1);
    }

    [RelayCommand]
    private void MoveDown(QueueEntryViewModel? entry)
    {
        if (entry != null && entry.Index < _state.Queue.Count - 1) _player.Move(entry.Index, entry.Index + 1);
    }

    [RelayCommand]
    private void ClearQueue() => _player.ClearQueue();

    public void MoveEntry(int from, int to) => _player.Move(from, to);

    public Task OpenPathsAsync(IEnumerable<string> paths) => _player.OpenPathsAsync(paths);

    public Task AddPathsAsync(IEnumerable<string> paths) => _player.AddPathsAsync(paths);

    // ── State → view ────────────────────────────────────────────────────────

    private void Apply(PlayerState state)
    {
        _applying = true;
        try
        {
            ApplyCore(state);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyCore(PlayerState state)
    {
        _state = state;
        var current = state.Current;

        HasCurrent = current != null;
        IsPlaying = state.Playing;
        IsShuffle = state.Shuffle;
        Repeat = state.Repeat;
        IsMuted = state.Muted;
        ErrorMessage = state.Error;

        if (Math.Abs(_volumePercent - state.Volume * 100) > 0.01)
        {
            _volumePercent = state.Volume * 100;
            OnPropertyChanged(nameof(VolumePercent));
            OnPropertyChanged(nameof(VolumeLabel));
            OnPropertyChanged(nameof(VolumeGlyph));
        }

        bool canWrap = state.Repeat == RepeatMode.All && state.Queue.Count > 1;
        CanPrevious = current != null;
        CanNext = current != null && (state.Index < state.Queue.Count - 1 || canWrap);

        if (current != null)
        {
            Title = current.Track.Title;
            Artist = string.Join(", ", current.Track.Artists);
            Album = current.Track.Album ?? string.Empty;
        }
        else
        {
            Title = "Nothing playing";
            Artist = "Open files or drop them on the Music page";
            Album = string.Empty;
        }

        DurationSeconds = Math.Max(0, state.Duration.TotalSeconds);
        ApplyPosition(state.Position);
        UpdateQueue(state);
        UpdateCover(current);
    }

    private void ApplyPosition(TimeSpan position)
    {
        if (_isDraggingSeek) return;

        double seconds = Math.Clamp(position.TotalSeconds, 0, Math.Max(DurationSeconds, position.TotalSeconds));
        if (Math.Abs(_positionSeconds - seconds) < 0.05) return;

        _positionSeconds = seconds;
        OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PositionText));
    }

    private void UpdateQueue(PlayerState state)
    {
        if (!ReferenceEquals(_shownQueue, state.Queue))
        {
            _shownQueue = state.Queue;
            // Replaced wholesale: one notification for the list, not one per row
            Queue = state.Queue.Select((item, i) => new QueueEntryViewModel(item, i)).ToArray();
        }

        foreach (var entry in Queue) entry.IsCurrent = entry.Index == state.Index;

        HasQueue = state.Queue.Count > 0;
        QueueSummary = state.Queue.Count switch
        {
            0 => "Queue is empty",
            1 => "1 track",
            var n => $"{n:N0} tracks"
        };

        var next = state.Index >= 0 && state.Index < state.Queue.Count - 1 ? state.Queue[state.Index + 1] : null;
        UpNext = next == null ? string.Empty : $"Up next: {next.Track.Title} – {string.Join(", ", next.Track.Artists)}";
    }

    private void UpdateCover(QueueItem? current)
    {
        if (current?.Key == _coverKey) return;
        _coverKey = current?.Key;

        _coverCts?.Cancel();
        if (current == null)
        {
            Cover = null;
            return;
        }

        var cts = _coverCts = new CancellationTokenSource();
        var track = current.Track;
        _ = Task.Run(async () =>
        {
            string? path = await _covers.GetCoverFileAsync(track, 256, cts.Token);
            ImageSource? image = path == null ? null : LoadFrozen(path, 256);
            _dispatcher.Post(() =>
            {
                if (!cts.IsCancellationRequested) Cover = image;
            });
        }, cts.Token);
    }

    private static ImageSource? LoadFrozen(string path, int size)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = size;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
