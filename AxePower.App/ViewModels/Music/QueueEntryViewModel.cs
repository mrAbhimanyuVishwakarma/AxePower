using System;
using AxePower.Core.Models.Music;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.ViewModels.Music;

/// <summary>One row of the queue drawer.</summary>
public partial class QueueEntryViewModel : ObservableObject
{
    public QueueEntryViewModel(QueueItem item, int index)
    {
        Item = item;
        Index = index;
    }

    public QueueItem Item { get; }
    public Guid Key => Item.Key;
    public int Index { get; }
    public int Number => Index + 1;
    public string Title => Item.Track.Title;
    public string Artist => string.Join(", ", Item.Track.Artists);
    public string DurationText => NowPlayingViewModel.FormatTime(Item.Track.Duration);

    /// <summary>Full text for the tooltip and screen readers.</summary>
    public string AccessibleName => $"{Title} by {Artist}{(IsCurrent ? ", now playing" : string.Empty)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isCurrent;
}
