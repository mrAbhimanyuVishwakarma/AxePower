using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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

/// <summary>
/// The Music page view model. Hosts Now Playing, the dynamic Library section (4 columns with queue open,
/// 6 columns with queue closed), right-side dual Queued / Songs drawer, Spotify control &amp; playlist creation.
/// </summary>
public partial class MusicViewModel : ObservableObject
{
    private readonly TagLibTagReader _tagReader = new();
    private readonly SettingsService? _settingsService;
    private readonly SpotifyController _spotifyController = new();

    public MusicViewModel(NowPlayingViewModel nowPlaying, SettingsService? settingsService = null)
    {
        NowPlaying = nowPlaying;
        _settingsService = settingsService ?? (Application.Current as App)?.Services?.GetService<SettingsService>();

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
                return $"{SelectedLibraryItem.Title} • {SelectedLibraryItem.SongCount} track{(SelectedLibraryItem.SongCount == 1 ? "" : "s")}";
            return "Select a playlist or folder";
        }
    }

    // ── Library Collections & Selected Songs ───────────────────────────────

    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPanelSubtitle))]
    private LibraryItemViewModel? _selectedLibraryItem;

    public ObservableCollection<SongItemViewModel> SelectedSongs { get; } = new();

    public string LibraryItemsSummary => $"{LibraryItems.Count} collection{(LibraryItems.Count == 1 ? "" : "s")}";

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

        if (item.Title == "YouTube Music" && item.Songs.Count == 0)
        {
            _ = SearchYouTubeAsync("Top Trending Songs");
        }

        OnPropertyChanged(nameof(RightPanelSubtitle));
    }

    [RelayCommand]
    public async Task PlayLibraryItemDirect(LibraryItemViewModel? item)
    {
        if (item == null || item.Songs.Count == 0) return;
        SelectLibraryItem(item);
        if (item.Songs.Any(s => s.Path.StartsWith("yt:", StringComparison.OrdinalIgnoreCase)))
        {
            await PlaySong(item.Songs.FirstOrDefault());
            return;
        }
        var paths = item.Songs.Select(s => s.Path).Where(File.Exists).ToList();
        if (paths.Count > 0)
            await NowPlaying.OpenPathsAsync(paths);
    }

    [RelayCommand]
    public async Task PlaySelectedSongs()
    {
        if (SelectedSongs.Count == 0) return;
        if (SelectedSongs.Any(s => s.Path.StartsWith("yt:", StringComparison.OrdinalIgnoreCase)))
        {
            await PlaySong(SelectedSongs.FirstOrDefault());
            return;
        }
        var paths = SelectedSongs.Select(s => s.Path).Where(File.Exists).ToList();
        if (paths.Count > 0)
            await NowPlaying.OpenPathsAsync(paths);
    }

    [RelayCommand]
    public async Task QueueSelectedSongs()
    {
        if (SelectedSongs.Count == 0) return;
        var paths = SelectedSongs.Select(s => s.Path).Where(File.Exists).ToList();
        if (paths.Count > 0)
            await NowPlaying.AddPathsAsync(paths);
    }

    [RelayCommand]
    public async Task PlaySong(SongItemViewModel? song)
    {
        if (song == null) return;
        NowPlaying.DisableSpotifyMode();

        if (song.Path.StartsWith("yt:", StringComparison.OrdinalIgnoreCase))
        {
            string videoId = song.Path["yt:".Length..];
            NowPlaying.Title = song.Title;
            NowPlaying.Artist = "Streaming from YouTube Music...";
            NowPlaying.Album = "YouTube Music";
            NowPlaying.HasCurrent = true;
            NowPlaying.ErrorMessage = null;

            string? cachedFile = await _ytService.GetOrDownloadAudioStreamAsync(videoId);
            if (cachedFile != null && File.Exists(cachedFile))
            {
                await NowPlaying.OpenPathsAsync(new[] { cachedFile });
                NowPlaying.Title = song.Title;
                NowPlaying.Artist = song.Artist;
                NowPlaying.Album = "YouTube Music";
            }
            else
            {
                NowPlaying.ErrorMessage = "Failed to stream audio from YouTube Music.";
            }
            return;
        }

        if (!File.Exists(song.Path)) return;
        var allPaths = SelectedSongs.Select(s => s.Path).Where(File.Exists).ToList();
        if (allPaths.Count > 0)
        {
            await NowPlaying.OpenPathsAsync(allPaths);
            int idx = allPaths.IndexOf(song.Path);
            if (idx > 0)
            {
                var entry = NowPlaying.Queue.FirstOrDefault(q => q.Index == idx);
                if (entry != null)
                    await NowPlaying.JumpCommand.ExecuteAsync(entry);
            }
        }
        else
        {
            await NowPlaying.OpenPathsAsync(new[] { song.Path });
        }
    }

    [RelayCommand]
    public async Task QueueSong(SongItemViewModel? song)
    {
        if (song == null) return;
        if (song.Path.StartsWith("yt:", StringComparison.OrdinalIgnoreCase))
        {
            string videoId = song.Path["yt:".Length..];
            string? cachedFile = await _ytService.GetOrDownloadAudioStreamAsync(videoId);
            if (cachedFile != null && File.Exists(cachedFile))
                await NowPlaying.AddPathsAsync(new[] { cachedFile });
            return;
        }

        if (!File.Exists(song.Path)) return;
        await NowPlaying.AddPathsAsync(new[] { song.Path });
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
        int count = LibraryItems.Count(i => i.Path?.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) == true);
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

            LibraryItems.Insert(0, item);
            IsCreatePlaylistOpen = false;
            SelectLibraryItem(item);
            OnPropertyChanged(nameof(LibraryItemsSummary));
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

    // ── Spotify & YouTube Music Integration ────────────────────────────────

    private readonly YouTubeMusicService _ytService = new();

    [ObservableProperty]
    private string _searchYouTubeText = string.Empty;

    [ObservableProperty]
    private bool _isSearchingYouTube;

    [RelayCommand]
    public Task LaunchAndMinimizeSpotifyAsync()
    {
        return NowPlaying.EnableSpotifyModeAsync();
    }

    [RelayCommand]
    public async Task SearchYouTubeAsync(string? query = null)
    {
        string q = string.IsNullOrWhiteSpace(query) ? SearchYouTubeText : query;
        if (string.IsNullOrWhiteSpace(q)) q = "Top Trending Songs";
        IsSearchingYouTube = true;
        try
        {
            var tracks = await _ytService.SearchTracksAsync(q, limit: 30);
            var ytCard = LibraryItems.FirstOrDefault(i => i.Title == "YouTube Music");
            if (ytCard == null)
            {
                ytCard = new LibraryItemViewModel
                {
                    Title = "YouTube Music",
                    Subtitle = $"{tracks.Count} tracks • Online",
                    IconGlyph = "\uE768",
                    GradientBrush = CreateGradient("#DC2626", "#7F1D1D")
                };
                LibraryItems.Insert(0, ytCard);
            }

            ytCard.Songs.Clear();
            int num = 1;
            foreach (var t in tracks)
            {
                ytCard.Songs.Add(new SongItemViewModel
                {
                    Number = num++,
                    Title = t.Title,
                    Artist = t.Artist,
                    Album = "YouTube Music",
                    Duration = t.Duration ?? TimeSpan.FromMinutes(3),
                    Path = $"yt:{t.VideoId}"
                });
            }

            ytCard.Subtitle = $"{ytCard.Songs.Count} tracks • Online";
            SelectLibraryItem(ytCard);
            OnPropertyChanged(nameof(LibraryItemsSummary));
        }
        catch (Exception ex)
        {
            NowPlaying.ErrorMessage = $"YouTube search error: {ex.Message}";
        }
        finally
        {
            IsSearchingYouTube = false;
        }
    }

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
        LibraryItems.Clear();

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
        LibraryItems.Add(localItem);

        // 2. YouTube Music (In-App Search & Stream)
        var ytItem = new LibraryItemViewModel
        {
            Title = "YouTube Music",
            Subtitle = "Search & Stream Free",
            IconGlyph = "\uE768",
            GradientBrush = CreateGradient("#DC2626", "#7F1D1D")
        };
        LibraryItems.Add(ytItem);

        // 2. Favorites & Starred
        var favItem = new LibraryItemViewModel
        {
            Title = "Favorites",
            Subtitle = "Starred tracks",
            IconGlyph = "\uE734",
            GradientBrush = CreateGradient("#EC4899", "#8B5CF6")
        };
        foreach (var s in localSongs.Take(5)) favItem.Songs.Add(s);
        LibraryItems.Add(favItem);

        // 3. Gaming & Beast Beats
        var gamingItem = new LibraryItemViewModel
        {
            Title = "Gaming Beast",
            Subtitle = "High energy mix",
            IconGlyph = "\uE7FC",
            GradientBrush = CreateGradient("#F97316", "#DC2626")
        };
        foreach (var s in localSongs) gamingItem.Songs.Add(s);
        LibraryItems.Add(gamingItem);

        // 4. Chill & Eco Focus
        var chillItem = new LibraryItemViewModel
        {
            Title = "Chill & Focus",
            Subtitle = "Relaxing audio",
            IconGlyph = "\uE706",
            GradientBrush = CreateGradient("#10B981", "#047857")
        };
        foreach (var s in localSongs) chillItem.Songs.Add(s);
        LibraryItems.Add(chillItem);

        // 5. AxePower Mix
        var mixItem = new LibraryItemViewModel
        {
            Title = "AxePower Mix",
            Subtitle = "Curated rotation",
            IconGlyph = "\uE945",
            GradientBrush = CreateGradient("#6366F1", "#4338CA")
        };
        foreach (var s in localSongs) mixItem.Songs.Add(s);
        LibraryItems.Add(mixItem);

        // 6. Recently Added
        var recentItem = new LibraryItemViewModel
        {
            Title = "Recently Added",
            Subtitle = $"{localSongs.Count} tracks",
            IconGlyph = "\uE823",
            GradientBrush = CreateGradient("#8B5CF6", "#6D28D9")
        };
        foreach (var s in localSongs) recentItem.Songs.Add(s);
        LibraryItems.Add(recentItem);

        // 7. Scan for any user created playlists in SelectedPlaylistPath
        ScanPlaylistsInDirectory(SelectedPlaylistPath);
        if (!string.Equals(SelectedPlaylistPath, musicFolder, StringComparison.OrdinalIgnoreCase))
            ScanPlaylistsInDirectory(musicFolder);

        // Default selection to Local Music or first item
        var defaultItem = LibraryItems.FirstOrDefault(i => i.IsSelected) ?? LibraryItems.FirstOrDefault();
        if (defaultItem != null) SelectLibraryItem(defaultItem);
        OnPropertyChanged(nameof(LibraryItemsSummary));
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
                if (LibraryItems.Any(i => string.Equals(i.Title, plName, StringComparison.OrdinalIgnoreCase)))
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
                LibraryItems.Insert(0, item);
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
        LibraryItems.Insert(0, item);
        SelectLibraryItem(item);
        OnPropertyChanged(nameof(LibraryItemsSummary));
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
