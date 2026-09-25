using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxePower.App.Services;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// Controls which tab is active in the right-side panel: current playback queue or library songs list.
/// </summary>
public enum RightPanelTab
{
    Queued,
    Songs,
    Lyrics
}

public enum MusicBrowseMode
{
    Local,
    YouTube
}

/// <summary>
/// The Music page view model. Hosts Now Playing, the dynamic Library section (4 columns with queue open,
/// 6 columns with queue closed), right-side dual Queued / Songs drawer, and playlist creation.
/// </summary>
public partial class MusicViewModel : ObservableObject
{
    private readonly TagLibTagReader _tagReader = new();
    private readonly SettingsService? _settingsService;
    private readonly ProviderRegistry? _providers;
    private readonly IStreamResolver? _resolver;
    private readonly ICoverArtService? _covers;

    /// <summary>Cancels a search that a newer one has replaced.</summary>
    private CancellationTokenSource? _searchCts;

    public MusicViewModel(
        NowPlayingViewModel nowPlaying,
        SettingsService? settingsService = null,
        ProviderRegistry? providers = null,
        IStreamResolver? resolver = null,
        LyricsViewModel? lyrics = null,
        AmbientModeViewModel? ambient = null,
        ICoverArtService? covers = null)
    {
        NowPlaying = nowPlaying;
        var services = (Application.Current as App)?.Services;
        _settingsService = settingsService ?? services?.GetService<SettingsService>();
        _providers = providers ?? services?.GetService<ProviderRegistry>();
        _resolver = resolver ?? services?.GetService<IStreamResolver>();
        _covers = covers ?? services?.GetService<ICoverArtService>();
        Sources = services?.GetService<SourcesViewModel>();
        Shortcuts = services?.GetService<ShortcutsViewModel>();
        Lyrics = lyrics ?? services?.GetService<LyricsViewModel>();
        Ambient = ambient ?? services?.GetService<AmbientModeViewModel>();

        NowPlaying.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.QueueSummary))
                OnPropertyChanged(nameof(RightPanelSubtitle));
        };

        var savedPath = _settingsService?.GetSettings().Music.PlaylistFolder;
        _selectedPlaylistPath = !string.IsNullOrWhiteSpace(savedPath) && Directory.Exists(savedPath)
            ? savedPath
            : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        InitializeLibrary();
    }

    public NowPlayingViewModel NowPlaying { get; }

    /// <summary>The Sources list shown in Music Settings. Null only in a design-time view.</summary>
    public SourcesViewModel? Sources { get; }

    /// <summary>The Shortcuts list shown in Music Settings. Null only in a design-time view.</summary>
    public ShortcutsViewModel? Shortcuts { get; }

    public LyricsViewModel? Lyrics { get; }

    public AmbientModeViewModel? Ambient { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueToggleLabel), nameof(LibraryColumns))]
    private bool _isQueueOpen = true;

    public string QueueToggleLabel => IsQueueOpen ? "Hide queue" : "Show queue";

    /// <summary>
    /// 4 columns when queue is open, 6 columns when queue is closed.
    /// </summary>
    public int LibraryColumns => IsQueueOpen ? 4 : 6;

    [RelayCommand]
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    // ── Right Panel Tab Selector (Queued vs Songs) ─────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueueTabSelected), nameof(IsSongsTabSelected), nameof(IsLyricsTabSelected), nameof(RightPanelSubtitle))]
    private RightPanelTab _currentTab = RightPanelTab.Queued;

    public bool IsQueueTabSelected
    {
        get => CurrentTab == RightPanelTab.Queued;
        set { if (value) CurrentTab = RightPanelTab.Queued; }
    }

    public bool IsSongsTabSelected
    {
        get => CurrentTab == RightPanelTab.Songs;
        set { if (value) CurrentTab = RightPanelTab.Songs; }
    }

    public bool IsLyricsTabSelected
    {
        get => CurrentTab == RightPanelTab.Lyrics;
        set { if (value) CurrentTab = RightPanelTab.Lyrics; }
    }

    public string RightPanelSubtitle
    {
        get
        {
            if (CurrentTab == RightPanelTab.Queued)
                return NowPlaying.QueueSummary;
            if (CurrentTab == RightPanelTab.Lyrics)
                return "Synchronized lyrics";
            if (SelectedLibraryItem != null)
                return $"{SelectedLibraryItem.Title} • {SelectedLibraryItem.SongCount} track{(SelectedLibraryItem.SongCount == 1 ? "" : "s")}";
            return "Select a playlist or folder";
        }
    }

    // ── Library Collections & Selected Songs ───────────────────────────────

    private readonly ObservableCollection<LibraryItemViewModel> _localLibraryItems = new();
    private readonly ObservableCollection<LibraryItemViewModel> _youtubeLibraryItems = new();

    /// <summary>Only the collections for the selected source mode are exposed to the view.</summary>
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalMode), nameof(IsYouTubeMode), nameof(BrowseTitle),
        nameof(BrowseEmptyMessage), nameof(LibraryItemsSummary))]
    private MusicBrowseMode _browseMode = MusicBrowseMode.Local;

    public bool IsLocalMode
    {
        get => BrowseMode == MusicBrowseMode.Local;
        set { if (value) BrowseMode = MusicBrowseMode.Local; }
    }

    public bool IsYouTubeMode
    {
        get => BrowseMode == MusicBrowseMode.YouTube;
        set { if (value) BrowseMode = MusicBrowseMode.YouTube; }
    }

    public string BrowseTitle => IsLocalMode ? "Local Library & Playlists" : "YouTube Music Results";

    public string BrowseEmptyMessage => IsLocalMode
        ? "No local music yet. Add a folder or create a playlist."
        : "Search YouTube Music to see songs here.";

    public bool HasLibraryItems => LibraryItems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPanelSubtitle))]
    private LibraryItemViewModel? _selectedLibraryItem;

    public ObservableCollection<SongItemViewModel> SelectedSongs { get; } = new();

    public string LibraryItemsSummary => $"{LibraryItems.Count} collection{(LibraryItems.Count == 1 ? "" : "s")}";

    partial void OnBrowseModeChanged(MusicBrowseMode value) => ShowBrowseMode(value);

    [RelayCommand]
    private void ShowLocalMode() => BrowseMode = MusicBrowseMode.Local;

    [RelayCommand]
    private void ShowYouTubeMode() => BrowseMode = MusicBrowseMode.YouTube;

    private void ShowBrowseMode(MusicBrowseMode mode)
    {
        foreach (var item in LibraryItems) item.IsSelected = false;
        LibraryItems.Clear();

        var source = mode == MusicBrowseMode.Local ? _localLibraryItems : _youtubeLibraryItems;
        foreach (var item in source) LibraryItems.Add(item);

        SelectedLibraryItem = null;
        SelectedSongs.Clear();
        OnPropertyChanged(nameof(HasLibraryItems));
        OnPropertyChanged(nameof(LibraryItemsSummary));
        OnPropertyChanged(nameof(RightPanelSubtitle));
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    public void SelectLibraryItem(LibraryItemViewModel? item)
    {
        if (item == null) return;

        foreach (var it in LibraryItems)
            it.IsSelected = it == item;

        SelectedLibraryItem = item;
        SelectedSongs.Clear();
        foreach (var s in item.Songs)
            SelectedSongs.Add(s);

        // Switch to Songs tab in the right panel so user immediately sees the songs list
        CurrentTab = RightPanelTab.Songs;
        if (!IsQueueOpen) IsQueueOpen = true;

        OnPropertyChanged(nameof(RightPanelSubtitle));
    }

    [RelayCommand]
    public async Task PlayLibraryItemDirect(LibraryItemViewModel? item)
    {
        if (item == null || item.Songs.Count == 0) return;
        SelectLibraryItem(item);
        await PlayListAsync(item.Songs, item.Songs[0]);
    }

    [RelayCommand]
    public Task PlaySelectedSongs() =>
        SelectedSongs.Count == 0 ? Task.CompletedTask : PlayListAsync(SelectedSongs, SelectedSongs[0]);

    [RelayCommand]
    public Task QueueSelectedSongs()
    {
        var tracks = ToTracks(SelectedSongs);
        if (tracks.Count > 0) NowPlaying.QueueTracks(tracks);
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task PlaySong(SongItemViewModel? song) =>
        song == null ? Task.CompletedTask : PlayListAsync(SelectedSongs, song);

    /// <summary>
    /// Plays a list starting at one row. The whole list becomes the queue, so "play this one"
    /// still leaves the rest of the album or the search results behind it.
    /// </summary>
    private async Task PlayListAsync(IReadOnlyList<SongItemViewModel> list, SongItemViewModel start)
    {
        NowPlaying.ErrorMessage = null;

        var rows = list.Contains(start) ? list.ToList() : new List<SongItemViewModel> { start };
        var tracks = ToTracks(rows);
        if (tracks.Count == 0)
        {
            NowPlaying.ErrorMessage = "These tracks are no longer available.";
            return;
        }

        // The index has to be found in the list that survived filtering, not the original one.
        int index = Math.Max(0, rows.Where(Playable).ToList().IndexOf(start));
        await NowPlaying.OpenTracksAsync(tracks, index);
    }

    /// <summary>Rows that can actually be played: a file that still exists, or a source track.</summary>
    private static bool Playable(SongItemViewModel song) =>
        song.Track is { IsLocal: false } || File.Exists(song.Path);

    private List<Track> ToTracks(IEnumerable<SongItemViewModel> songs)
    {
        var tracks = new List<Track>();
        foreach (var song in songs)
        {
            if (song.Track is { IsLocal: false } remote) { tracks.Add(remote); continue; }
            if (!File.Exists(song.Path)) continue;

            // A local row only carries its path; the player reads the file's own tags when it
            // opens it, so a placeholder track here would be overwritten anyway.
            tracks.Add(song.Track ?? ReadLocalTrack(song));
        }
        return tracks;
    }

    private Track ReadLocalTrack(SongItemViewModel song) => new(
        Id: 0,
        Path: song.Path,
        Title: song.Title,
        Artists: new[] { song.Artist },
        Album: song.Album,
        AlbumArtist: null,
        TrackNumber: song.Number,
        DiscNumber: null,
        Year: null,
        Genres: Array.Empty<string>(),
        Duration: song.Duration,
        Format: new AudioFormatInfo(Path.GetExtension(song.Path).TrimStart('.').ToUpperInvariant(), 0, 0, 2, 0, false),
        ReplayGain: null,
        CoverKey: null,
        Liked: false);

    [RelayCommand]
    public Task QueueSong(SongItemViewModel? song)
    {
        if (song == null) return Task.CompletedTask;

        var tracks = ToTracks(new[] { song });
        if (tracks.Count > 0) NowPlaying.QueueTracks(tracks);
        return Task.CompletedTask;
    }

    // ── Playlist Path & Creation ─────────────────────────────────────────────

    [ObservableProperty]
    private string _selectedPlaylistPath = string.Empty;

    [ObservableProperty]
    private bool _isCreatePlaylistOpen;

    [ObservableProperty]
    private string _newPlaylistName = "My Playlist";

    [RelayCommand]
    public void OpenCreatePlaylistDialog()
    {
        int count = _localLibraryItems.Count(i => i.Path?.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) == true);
        NewPlaylistName = count == 0 ? "My Playlist" : $"Playlist {count + 1}";
        IsCreatePlaylistOpen = true;
    }

    [RelayCommand]
    public void CancelCreatePlaylist()
    {
        IsCreatePlaylistOpen = false;
    }

    [RelayCommand]
    public void ConfirmCreatePlaylist()
    {
        try
        {
            string name = string.IsNullOrWhiteSpace(NewPlaylistName) ? "New Playlist" : NewPlaylistName.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            string dir = SelectedPlaylistPath;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string filePath = Path.Combine(dir, $"{name}.m3u");
            if (!File.Exists(filePath))
            {
                File.WriteAllText(filePath, "#EXTM3U\n");
            }

            var item = new LibraryItemViewModel
            {
                Title = name,
                Subtitle = "0 tracks • Local Playlist",
                Path = filePath,
                IconGlyph = "\uE8FD",
                GradientBrush = CreateGradient("#0EA5E9", "#0369A1")
            };

            _localLibraryItems.Insert(0, item);
            IsCreatePlaylistOpen = false;
            BrowseMode = MusicBrowseMode.Local;
            ShowBrowseMode(BrowseMode);
            SelectLibraryItem(item);
        }
        catch (Exception ex)
        {
            NowPlaying.ErrorMessage = $"Failed to create playlist: {ex.Message}";
        }
    }

    // ── Music Settings Dialog ───────────────────────────────────────────────

    [ObservableProperty]
    private bool _isMusicSettingsOpen;

    [RelayCommand]
    public void ToggleMusicSettings() => IsMusicSettingsOpen = !IsMusicSettingsOpen;

    [RelayCommand]
    public void CloseMusicSettings() => IsMusicSettingsOpen = false;

    [RelayCommand]
    public void ChangePlaylistPath()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Music & Playlist Folder",
            InitialDirectory = Directory.Exists(SelectedPlaylistPath) ? SelectedPlaylistPath : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            SelectedPlaylistPath = dlg.FolderName;
            if (_settingsService != null)
            {
                var s = _settingsService.GetSettings();
                s.Music.PlaylistFolder = dlg.FolderName;
                if (!s.Music.LibraryFolders.Contains(dlg.FolderName))
                    s.Music.LibraryFolders.Add(dlg.FolderName);
                _settingsService.SaveSettings(s);
            }
            RefreshLibrary();
        }
    }

    // ── Online sources ─────────────────────────────────────────────

    [ObservableProperty]
    private string _searchYouTubeText = string.Empty;

    [ObservableProperty]
    private bool _isSearchingYouTube;

    /// <summary>The source answering searches right now, for the search box placeholder.</summary>
    public string SearchSourceName =>
        _providers?.Active<IMetadataProvider>(ProviderKind.Metadata)?.Info.Name ?? "no source";

    /// <summary>
    /// Searches whichever source is active. The command keeps its old name so the existing
    /// bindings and the search box carry on working.
    /// </summary>
    [RelayCommand]
    public async Task SearchYouTubeAsync(string? query = null)
    {
        BrowseMode = MusicBrowseMode.YouTube;
        var provider = _providers?.ById("youtube") as IMetadataProvider;
        if (provider == null)
        {
            NowPlaying.ErrorMessage = "YouTube Music is unavailable.";
            return;
        }

        string q = string.IsNullOrWhiteSpace(query) ? SearchYouTubeText : query;
        if (string.IsNullOrWhiteSpace(q)) q = "Top Trending Songs";

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsSearchingYouTube = true;
        try
        {
            var tracks = await provider.SearchTracksAsync(q, limit: 30, cts.Token);
            if (cts.IsCancellationRequested) return;

            string name = provider.Info.Name;
            var card = _youtubeLibraryItems.FirstOrDefault(i => i.Id == "youtube-search");
            if (card == null)
            {
                card = new LibraryItemViewModel
                {
                    Id = "youtube-search",
                    Title = name,
                    IconGlyph = GlyphFor(provider.Info.Id),
                    GradientBrush = GradientFor(provider.Info.Id)
                };
                _youtubeLibraryItems.Insert(0, card);
            }

            Fill(card, tracks, name);
            card.Subtitle = Summary(card.Songs.Count, name);
            ShowBrowseMode(MusicBrowseMode.YouTube);
            SelectLibraryItem(card);
            _ = UpdateRemoteCardCoverAsync(card, tracks.FirstOrDefault(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NowPlaying.ErrorMessage = provider.Info.Name + " search failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                IsSearchingYouTube = false;
            }
            cts.Dispose();
        }
    }

    private static void Fill(LibraryItemViewModel card, IReadOnlyList<Track> tracks, string fallbackAlbum)
    {
        card.Songs.Clear();
        int number = 1;
        foreach (var track in tracks)
        {
            card.Songs.Add(new SongItemViewModel
            {
                Number = number++,
                Title = track.Title,
                Artist = track.Artists.Count > 0 ? track.Artists[0] : string.Empty,
                Album = track.Album ?? fallbackAlbum,
                Duration = track.Duration,
                Path = string.Empty,
                Track = track
            });
        }
    }

    private async Task UpdateRemoteCardCoverAsync(
        LibraryItemViewModel card,
        Track? track,
        CancellationToken ct)
    {
        if (_covers is null || track is null)
        {
            card.CoverImage = null;
            return;
        }

        try
        {
            string? path = await _covers.GetCoverFileAsync(track, 256, ct);
            if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(path)) return;
            if (card.Songs.FirstOrDefault()?.Track?.Source?.Id != track.Source?.Id) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            card.CoverImage = image;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The gradient tile is the deliberate fallback when remote artwork is unavailable.
        }
    }

    private static string Summary(int count, string sourceName) =>
        count + " track" + (count == 1 ? "" : "s") + " \u2022 " + sourceName;

    private static string GlyphFor(string providerId) => providerId switch
    {
        "youtube" => "\uE768",
        _ => "\uE8D6"
    };

    private static Brush GradientFor(string providerId) => providerId switch
    {
        "youtube" => CreateGradient("#DC2626", "#7F1D1D"),
        _ => CreateGradient("#6B7280", "#374151")
    };

    [RelayCommand]
    public async Task AddFolderToLibrary()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select any audio file in the folder to add to Library",
            Filter = "Audio files (*.mp3;*.m4a;*.flac;*.wav;*.wma;*.aac)|*.mp3;*.m4a;*.flac;*.wav;*.wma;*.aac|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;
        string? folder = Path.GetDirectoryName(dialog.FileName);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        await ScanFolderAsync(folder, Path.GetFileName(folder));
    }

    [RelayCommand]
    public void RefreshLibrary()
    {
        InitializeLibrary();
    }

    /// <summary>Files dropped on the page replace the queue; files dropped on the queue are appended.</summary>
    public Task HandleDropAsync(IReadOnlyList<string> paths, bool onQueue) =>
        onQueue ? NowPlaying.AddPathsAsync(paths) : NowPlaying.OpenPathsAsync(paths);

    // ── Library Initialization & Scanning ──────────────────────────────────

    private void InitializeLibrary()
    {
        _localLibraryItems.Clear();

        // 1. Scan Local Music folder
        string musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var localSongs = LoadSongsFromDirectory(musicFolder);

        var localItem = new LibraryItemViewModel
        {
            Title = "Local Music",
            Subtitle = $"{localSongs.Count} track{(localSongs.Count == 1 ? "" : "s")}",
            IconGlyph = "\uE8D6",
            GradientBrush = CreateGradient("#3B82F6", "#1D4ED8"),
            IsSelected = true
        };
        foreach (var s in localSongs) localItem.Songs.Add(s);
        _localLibraryItems.Add(localItem);

        // Only real user-created playlists are shown. Empty synthetic collections are not
        // generated; the Create Playlist command is the sole way to add a new empty playlist.
        ScanPlaylistsInDirectory(SelectedPlaylistPath);
        if (!string.Equals(SelectedPlaylistPath, musicFolder, StringComparison.OrdinalIgnoreCase))
            ScanPlaylistsInDirectory(musicFolder);

        // Default selection to Local Music or first item
        BrowseMode = MusicBrowseMode.Local;
        ShowBrowseMode(BrowseMode);
        var defaultItem = LibraryItems.FirstOrDefault(i => i.IsSelected) ?? LibraryItems.FirstOrDefault();
        if (defaultItem != null) SelectLibraryItem(defaultItem);
    }

    private void ScanPlaylistsInDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        try
        {
            var playlistFiles = Directory.EnumerateFiles(dir, "*.m3u", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(dir, "*.m3u8", SearchOption.TopDirectoryOnly))
                .ToList();

            foreach (var pl in playlistFiles)
            {
                string plName = Path.GetFileNameWithoutExtension(pl);
                if (_localLibraryItems.Any(i => string.Equals(i.Title, plName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var item = new LibraryItemViewModel
                {
                    Title = plName,
                    Path = pl,
                    IconGlyph = "\uE8FD",
                    GradientBrush = CreateGradient("#0EA5E9", "#0369A1")
                };

                try
                {
                    var lines = File.ReadAllLines(pl);
                    int num = 1;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

                        string trackPath = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(dir, trimmed);
                        if (File.Exists(trackPath))
                        {
                            item.Songs.Add(new SongItemViewModel
                            {
                                Number = num++,
                                Title = Path.GetFileNameWithoutExtension(trackPath),
                                Artist = "Playlist Track",
                                Path = trackPath,
                                Duration = TimeSpan.FromMinutes(3)
                            });
                        }
                    }
                }
                catch { }

                item.Subtitle = $"{item.Songs.Count} track{(item.Songs.Count == 1 ? "" : "s")} • Local Playlist";
                _localLibraryItems.Insert(0, item);
            }
        }
        catch { }
    }

    private Task ScanFolderAsync(string folderPath, string folderName)
    {
        var songs = LoadSongsFromDirectory(folderPath);
        if (songs.Count == 0) return Task.CompletedTask;

        var item = new LibraryItemViewModel
        {
            Title = folderName,
            Subtitle = $"{songs.Count} tracks",
            IconGlyph = "\uE8B7",
            GradientBrush = CreateGradient("#0284C7", "#0369A1")
        };
        foreach (var s in songs) item.Songs.Add(s);
        _localLibraryItems.Insert(0, item);
        BrowseMode = MusicBrowseMode.Local;
        ShowBrowseMode(BrowseMode);
        SelectLibraryItem(item);
        return Task.CompletedTask;
    }

    private List<SongItemViewModel> LoadSongsFromDirectory(string directory)
    {
        var songs = new List<SongItemViewModel>();
        if (!Directory.Exists(directory)) return songs;

        try
        {
            var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => DecoderFactory.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Take(200)
                .ToList();

            int number = 1;
            foreach (var file in files)
            {
                try
                {
                    var tags = _tagReader.Read(file);
                    songs.Add(new SongItemViewModel
                    {
                        Number = number++,
                        Title = string.IsNullOrWhiteSpace(tags.Title) ? Path.GetFileNameWithoutExtension(file) : tags.Title,
                        Artist = string.IsNullOrWhiteSpace(tags.Artists) ? "Unknown Artist" : tags.Artists,
                        Album = tags.Album ?? string.Empty,
                        Duration = tags.Duration,
                        Path = file
                    });
                }
                catch
                {
                    songs.Add(new SongItemViewModel
                    {
                        Number = number++,
                        Title = Path.GetFileNameWithoutExtension(file),
                        Artist = "Local Music",
                        Duration = TimeSpan.FromMinutes(3),
                        Path = file
                    });
                }
            }
        }
        catch { }

        return songs;
    }

    private static LinearGradientBrush CreateGradient(string hexStart, string hexEnd)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hexStart), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hexEnd), 1));
        brush.Freeze();
        return brush;
    }
}
