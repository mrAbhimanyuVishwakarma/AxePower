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
    Songs
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
    private readonly SettingsService? _settingsService;
    private readonly ProviderRegistry? _providers;
    private readonly IStreamResolver? _resolver;
    private readonly ICoverArtService? _covers;
    private readonly RecentlyPlayedService? _recent;
    private readonly RecentSearchesService? _searches;
    private bool _applyingAmbientSettings;

    /// <summary>Cancels a search that a newer one has replaced.</summary>
    private CancellationTokenSource? _searchCts;

    public MusicViewModel(
        NowPlayingViewModel nowPlaying,
        SettingsService? settingsService = null,
        ProviderRegistry? providers = null,
        IStreamResolver? resolver = null,
        LyricsViewModel? lyrics = null,
        AmbientModeViewModel? ambient = null,
        ICoverArtService? covers = null,
        RecentlyPlayedService? recent = null,
        RecentSearchesService? searches = null)
    {
        NowPlaying = nowPlaying;
        var services = (Application.Current as App)?.Services;
        _settingsService = settingsService ?? services?.GetService<SettingsService>();
        _providers = providers ?? services?.GetService<ProviderRegistry>();
        _resolver = resolver ?? services?.GetService<IStreamResolver>();
        _covers = covers ?? services?.GetService<ICoverArtService>();
        _recent = recent ?? services?.GetService<RecentlyPlayedService>();
        _searches = searches ?? services?.GetService<RecentSearchesService>();
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

        var music = _settingsService?.GetSettings().Music;
        _applyingAmbientSettings = true;
        AmbientAutoEnter = music?.AmbientAutoEnter ?? false;
        AmbientIdleMinutes = music?.AmbientIdleMinutes ?? 2;
        _applyingAmbientSettings = false;

        InitializeLibrary();
        LoadRecentSearches();
    }

    /// <summary>The page is on screen: follow the recently played list while it is.</summary>
    public void Activate()
    {
        if (_recent == null) return;
        _recent.Changed -= OnRecentChanged;
        _recent.Changed += OnRecentChanged;
        LoadRecentPlays();
    }

    public void Deactivate()
    {
        if (_recent != null) _recent.Changed -= OnRecentChanged;
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
    [NotifyPropertyChangedFor(nameof(IsQueueTabSelected), nameof(IsSongsTabSelected), nameof(RightPanelSubtitle))]
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

    public string RightPanelSubtitle
    {
        get
        {
            if (CurrentTab == RightPanelTab.Queued)
                return NowPlaying.QueueSummary;
            if (SelectedLibraryItem != null)
                return $"{SelectedLibraryItem.Query ?? SelectedLibraryItem.Title} • {SelectedLibraryItem.SongCount} track{(SelectedLibraryItem.SongCount == 1 ? "" : "s")}";
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
        nameof(BrowseEmptyMessage), nameof(LibraryItemsSummary), nameof(RecentPlaysEmptyMessage))]
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

    partial void OnBrowseModeChanged(MusicBrowseMode value)
    {
        ShowBrowseMode(value);
        LoadRecentPlays();
        if (value == MusicBrowseMode.YouTube) ShowLastSearch();
    }

    // ── Last played ─────────────────────────────────────────────────────────

    /// <summary>Songs played most recently in the mode on show: local files, or YouTube Music.</summary>
    public ObservableCollection<SongItemViewModel> RecentPlays { get; } = new();

    public bool HasRecentPlays => RecentPlays.Count > 0;

    public string RecentPlaysEmptyMessage => IsLocalMode
        ? "Songs you play from your library will show up here."
        : "Songs you play from YouTube Music will show up here.";

    private void OnRecentChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(LoadRecentPlays);

    private void LoadRecentPlays()
    {
        RecentPlays.Clear();
        if (_recent != null)
        {
            int number = 1;
            foreach (var play in _recent.Get(streamed: IsYouTubeMode))
            {
                var track = play.ToTrack();
                RecentPlays.Add(new SongItemViewModel
                {
                    Number = number++,
                    Title = play.Title,
                    Artist = play.Artists.Count > 0 ? string.Join(", ", play.Artists) : "Unknown Artist",
                    Album = play.Album ?? string.Empty,
                    Duration = track.Duration,
                    Path = play.Path,
                    Track = track
                });
            }
        }
        OnPropertyChanged(nameof(HasRecentPlays));
    }

    /// <summary>Plays from a Last played row onward, so the rest of the history follows it.</summary>
    [RelayCommand]
    public Task PlayRecent(SongItemViewModel? song) =>
        song == null ? Task.CompletedTask : PlayListAsync(RecentPlays.ToList(), song);

    [RelayCommand]
    public Task QueueRecent(SongItemViewModel? song) => QueueSong(song);

    // ── Ambient Mode as a screensaver ───────────────────────────────────────

    /// <summary>The idle times offered in Music Settings, in minutes.</summary>
    public IReadOnlyList<int> AmbientIdleChoices { get; } = new[] { 1, 2, 5, 10 };

    [ObservableProperty]
    private bool _ambientAutoEnter;

    [ObservableProperty]
    private int _ambientIdleMinutes = 2;

    partial void OnAmbientAutoEnterChanged(bool value) => SaveAmbientSettings();

    partial void OnAmbientIdleMinutesChanged(int value) => SaveAmbientSettings();

    private void SaveAmbientSettings()
    {
        if (_applyingAmbientSettings || _settingsService == null) return;
        var settings = _settingsService.GetSettings();
        settings.Music.AmbientAutoEnter = AmbientAutoEnter;
        settings.Music.AmbientIdleMinutes = AmbientIdleMinutes;
        _settingsService.SaveSettings(settings);
    }

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
            NowPlaying.ErrorMessage = AxePower.Core.Services.Outcomes.UserMessages.Failed("Create the playlist", ex).Text;
        }
    }

    // ── Music Settings Dialog ───────────────────────────────────────────────

    [ObservableProperty]
    private bool _isMusicSettingsOpen;

    [RelayCommand]
    public void ToggleMusicSettings() => IsMusicSettingsOpen = !IsMusicSettingsOpen;

    [RelayCommand]
    public void CloseMusicSettings() => IsMusicSettingsOpen = false;

    // ── Mobile Connect / QR Remote Dialog ───────────────────────────────────

    [ObservableProperty]
    private bool _isMobileConnectOpen;

    [RelayCommand]
    public void ToggleMobileConnect() => IsMobileConnectOpen = !IsMobileConnectOpen;

    [RelayCommand]
    public void CloseMobileConnect() => IsMobileConnectOpen = false;

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
        IsRecentSearchesOpen = false;
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
            card.Query = q;
            ShowBrowseMode(MusicBrowseMode.YouTube);
            SelectLibraryItem(card);
            _ = UpdateRemoteCardCoverAsync(card, tracks.FirstOrDefault(), CancellationToken.None);

            if (tracks.Count > 0)
            {
                _searches?.Add(q, tracks);
                LoadRecentSearches();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        catch (Exception ex)
        {
            NowPlaying.ErrorMessage = AxePower.Core.Services.Outcomes.UserMessages.Failed("Search " + provider.Info.Name, ex).Text;
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

    // ── Recent searches ─────────────────────────────────────────────────────

    private readonly List<string> _allRecentSearches = new();

    /// <summary>What the listener has typed since opening the list; null shows every search.</summary>
    private string? _recentSearchFilter;

    /// <summary>Set while the app itself writes the search box, so that does not open the list.</summary>
    private bool _settingSearchTextQuietly;

    /// <summary>The recent searches on show, newest first: all of them, or those matching what is typed.</summary>
    public ObservableCollection<string> RecentSearches { get; } = new();

    public bool HasRecentSearches => _allRecentSearches.Count > 0;

    [ObservableProperty]
    private bool _isRecentSearchesOpen;

    /// <summary>Kept by the view: typing only filters the list while the box has the cursor.</summary>
    public bool IsSearchBoxFocused { get; set; }

    /// <summary>A click or focus on the search box: the whole history, whatever is in the box.</summary>
    public void ShowRecentSearches()
    {
        _recentSearchFilter = null;
        ApplyRecentSearchFilter();
        IsRecentSearchesOpen = RecentSearches.Count > 0;
    }

    /// <summary>Typing narrows the list to searches containing the text, and closes it when none do.</summary>
    partial void OnSearchYouTubeTextChanged(string value)
    {
        if (_settingSearchTextQuietly || !IsSearchBoxFocused) return;
        _recentSearchFilter = value;
        ApplyRecentSearchFilter();
        IsRecentSearchesOpen = RecentSearches.Count > 0;
    }

    private void SetSearchTextQuietly(string text)
    {
        _settingSearchTextQuietly = true;
        try { SearchYouTubeText = text; }
        finally { _settingSearchTextQuietly = false; }
    }

    private void ApplyRecentSearchFilter()
    {
        string filter = _recentSearchFilter?.Trim() ?? string.Empty;
        var shown = filter.Length == 0
            ? _allRecentSearches
            : _allRecentSearches.Where(q => q.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (shown.SequenceEqual(RecentSearches)) return;
        RecentSearches.Clear();
        foreach (string query in shown) RecentSearches.Add(query);
    }

    private void LoadRecentSearches()
    {
        _allRecentSearches.Clear();
        if (_searches != null)
            _allRecentSearches.AddRange(_searches.Get().Select(search => search.Query));

        ApplyRecentSearchFilter();
        OnPropertyChanged(nameof(HasRecentSearches));
        if (RecentSearches.Count == 0) IsRecentSearchesOpen = false;
    }

    /// <summary>Shows a remembered search's songs as they were saved, without searching again.</summary>
    [RelayCommand]
    public void OpenRecentSearch(string? query)
    {
        IsRecentSearchesOpen = false;
        var search = query == null ? null : _searches?.Find(query);
        if (search == null) return;

        SetSearchTextQuietly(search.Query);
        if (!IsYouTubeMode) BrowseMode = MusicBrowseMode.YouTube;   // shows the latest search first
        ShowSavedSearch(search);
    }

    [RelayCommand]
    public void RemoveRecentSearch(string? query)
    {
        if (query == null || _searches == null) return;
        _searches.Remove(query);
        LoadRecentSearches();
    }

    /// <summary>
    /// Brings back the songs from the last search when YouTube Music is shown, unless this
    /// visit already has results on screen.
    /// </summary>
    private void ShowLastSearch()
    {
        var card = _youtubeLibraryItems.FirstOrDefault(i => i.Id == "youtube-search");
        if (card is { Songs.Count: > 0 })
        {
            SelectLibraryItem(card);
            return;
        }

        var last = _searches?.Get().FirstOrDefault();
        if (last == null) return;

        if (string.IsNullOrWhiteSpace(SearchYouTubeText)) SetSearchTextQuietly(last.Query);
        ShowSavedSearch(last);
    }

    private void ShowSavedSearch(RecentSearch search)
    {
        var provider = _providers?.ById("youtube") as IMetadataProvider;
        string name = provider?.Info.Name ?? "YouTube";

        var card = _youtubeLibraryItems.FirstOrDefault(i => i.Id == "youtube-search");
        if (card == null)
        {
            card = new LibraryItemViewModel
            {
                Id = "youtube-search",
                Title = name,
                IconGlyph = GlyphFor("youtube"),
                GradientBrush = GradientFor("youtube")
            };
            _youtubeLibraryItems.Insert(0, card);
            if (IsYouTubeMode) ShowBrowseMode(MusicBrowseMode.YouTube);
        }

        Fill(card, search.Results.Select(r => r.ToTrack()).ToList(), name);
        card.Subtitle = Summary(card.Songs.Count, name);
        card.Query = search.Query;
        SelectLibraryItem(card);
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
            // Superseded: the card was recycled.
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

    /// <summary>
    /// Adds one or more folders to the library. They are saved in Music settings, come back on
    /// every visit, and each gets a card that can be removed again.
    /// </summary>
    [RelayCommand]
    public void AddFolderToLibrary()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add music folders to the library",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dialog.ShowDialog() != true) return;

        LibraryItemViewModel? last = null;
        foreach (string folder in dialog.FolderNames)
        {
            if (!Directory.Exists(folder)) continue;

            // Already on show (or the Local Music folder itself): just select it
            var existing = FolderCard(folder);
            if (existing != null)
            {
                last = existing;
                continue;
            }

            SaveLibraryFolders(folders => { if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder); });
            last = AddFolderCard(folder);
        }

        if (last == null) return;
        BrowseMode = MusicBrowseMode.Local;
        ShowBrowseMode(BrowseMode);
        SelectLibraryItem(last);
    }

    /// <summary>Takes a folder out of the library. Its files are not touched.</summary>
    [RelayCommand]
    public void RemoveFolder(LibraryItemViewModel? card)
    {
        if (card is not { IsRemovable: true, Path: { } folder }) return;

        SaveLibraryFolders(folders => folders.RemoveAll(f => SamePath(f, folder)));
        _localLibraryItems.Remove(card);
        bool wasSelected = ReferenceEquals(SelectedLibraryItem, card);
        ShowBrowseMode(BrowseMode);
        if (wasSelected && _localLibraryItems.FirstOrDefault(i => i.Id == LocalMusicId) is { } local && IsLocalMode)
            SelectLibraryItem(local);
    }

    private void SaveLibraryFolders(Action<List<string>> change)
    {
        if (_settingsService == null) return;
        var settings = _settingsService.GetSettings();
        change(settings.Music.LibraryFolders);
        _settingsService.SaveSettings(settings);
    }

    private LibraryItemViewModel? FolderCard(string folder) =>
        _localLibraryItems.FirstOrDefault(i => (i.IsRemovable && i.Path != null && SamePath(i.Path, folder))
            || (i.Id == LocalMusicId && SamePath(folder, MyMusicFolder)));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string MyMusicFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

    private const string LocalMusicId = "local-music";

    /// <summary>A removable card for an added folder, listed after Local Music and filled in the background.</summary>
    private LibraryItemViewModel AddFolderCard(string folder)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var card = new LibraryItemViewModel
        {
            Title = string.IsNullOrEmpty(name) ? folder : name,
            Path = folder,
            IsRemovable = true,
            Subtitle = Directory.Exists(folder) ? "Scanning…" : "Folder not found",
            IconGlyph = "\uE8B7",
            GradientBrush = CreateGradient("#0284C7", "#0369A1")
        };

        int localIndex = _localLibraryItems.ToList().FindIndex(i => i.Id == LocalMusicId);
        _localLibraryItems.Insert(localIndex + 1 + _localLibraryItems.Count(i => i.IsRemovable), card);
        if (IsLocalMode) ShowBrowseMode(BrowseMode);

        if (Directory.Exists(folder)) _ = FillFolderCardAsync(card, folder);
        return card;
    }

    /// <summary>Reads the folder's tags on a worker thread, then fills the card (and the Songs tab if it is showing it).</summary>
    private async Task FillFolderCardAsync(LibraryItemViewModel card, string folder)
    {
        var (songs, truncated) = await Task.Run(() => LoadSongsFromDirectory(folder));

        card.Songs.Clear();
        foreach (var song in songs) card.Songs.Add(song);
        card.Subtitle = songs.Count == 0
            ? "No music files found"
            : $"{songs.Count}{(truncated ? "+" : "")} track{(songs.Count == 1 ? "" : "s")}";

        if (ReferenceEquals(SelectedLibraryItem, card))
        {
            SelectedSongs.Clear();
            foreach (var song in songs) SelectedSongs.Add(song);
            OnPropertyChanged(nameof(RightPanelSubtitle));
        }
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

        string musicFolder = MyMusicFolder;
        var localItem = new LibraryItemViewModel
        {
            Id = LocalMusicId,
            Title = "Local Music",
            Path = musicFolder,
            Subtitle = "Scanning…",
            IconGlyph = "\uE8D6",
            GradientBrush = CreateGradient("#3B82F6", "#1D4ED8"),
            IsSelected = true
        };
        _localLibraryItems.Add(localItem);

        // Folders the listener added, in the order they were added. The Local Music folder is
        // already its own card, so it is not listed twice.
        foreach (string folder in (_settingsService?.GetSettings().Music.LibraryFolders ?? new List<string>())
                     .Where(f => !string.IsNullOrWhiteSpace(f) && !SamePath(f, musicFolder))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddFolderCard(folder);
        }

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

        _ = FillFolderCardAsync(localItem, musicFolder);
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
                catch (Exception caught) when (AxePower.Core.Services.ExpectedFailures.FileSystem(caught)) { /* An unreadable playlist file is skipped. */ }

                item.Subtitle = $"{item.Songs.Count} track{(item.Songs.Count == 1 ? "" : "s")} • Local Playlist";
                _localLibraryItems.Insert(0, item);
            }
        }
        catch (Exception caught) when (AxePower.Core.Services.ExpectedFailures.FileSystem(caught)) { /* An unreadable playlist folder is skipped. */ }
    }

    /// <summary>Most songs one card lists until the library database (M6) takes over.</summary>
    private const int MaxSongsPerFolder = 2000;

    /// <summary>
    /// Songs under a folder and all its subfolders. Runs on a worker thread. Subfolders that
    /// cannot be opened are skipped rather than ending the scan, which used to leave a whole
    /// folder looking empty when one protected subfolder sat inside it.
    /// </summary>
    private static (List<SongItemViewModel> Songs, bool Truncated) LoadSongsFromDirectory(string directory)
    {
        var songs = new List<SongItemViewModel>();
        if (!Directory.Exists(directory)) return (songs, false);

        var tagReader = new TagLibTagReader();
        bool truncated = false;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var files = Directory.EnumerateFiles(directory, "*", options)
                .Where(f => DecoderFactory.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Take(MaxSongsPerFolder + 1)
                .ToList();
            if (files.Count > MaxSongsPerFolder)
            {
                truncated = true;
                files.RemoveAt(files.Count - 1);
            }

            int number = 1;
            foreach (var file in files)
            {
                try
                {
                    var tags = tagReader.Read(file);
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
        catch (Exception caught) when (AxePower.Core.Services.ExpectedFailures.FileSystem(caught)) { /* An unreadable music folder is skipped. */ }

        return (songs, truncated);
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
