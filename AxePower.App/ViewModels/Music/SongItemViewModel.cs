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

    /// <summary>
    /// The track behind this row. Local rows build one from the path when they are played;
    /// rows that came from a source carry the real thing, including where it came from.
    /// </summary>
    public AxePower.Core.Models.Music.Track? Track { get; init; }

    /// <summary>True when this row plays from a source rather than from a file on this PC.</summary>
    public bool IsRemote => Track is { IsLocal: false };

    /// <summary>Artwork for a row that has no local cover, e.g. a search result.</summary>
    public string? ThumbnailUrl => Track?.ThumbnailUrl;

    [ObservableProperty]
    private bool _isPlaying;
}
