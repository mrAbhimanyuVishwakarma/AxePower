using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// The Music page. Phase 1 holds Now Playing, the queue and the open/drop actions; the library
/// tabs (tracks, albums, artists, genres, folders, playlists) arrive in Phase 2.
/// </summary>
public partial class MusicViewModel : ObservableObject
{
    public MusicViewModel(NowPlayingViewModel nowPlaying)
    {
        NowPlaying = nowPlaying;
    }

    public NowPlayingViewModel NowPlaying { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueToggleLabel))]
    private bool _isQueueOpen = true;

    public string QueueToggleLabel => IsQueueOpen ? "Hide queue" : "Show queue";

    [RelayCommand]
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    /// <summary>Files dropped on the page replace the queue; files dropped on the queue are appended.</summary>
    public Task HandleDropAsync(IReadOnlyList<string> paths, bool onQueue) =>
        onQueue ? NowPlaying.AddPathsAsync(paths) : NowPlaying.OpenPathsAsync(paths);
}
