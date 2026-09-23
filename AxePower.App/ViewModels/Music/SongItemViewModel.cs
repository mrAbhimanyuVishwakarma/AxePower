using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// A song item displayed in a playlist or library collection list.
/// </summary>
public partial class SongItemViewModel : ObservableObject
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public string DurationText => Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss");
    public string Path { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;
}
