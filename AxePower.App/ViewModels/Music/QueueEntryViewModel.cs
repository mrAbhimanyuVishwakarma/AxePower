using System;
using System.Collections.Generic;
using System.Linq;
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
        Candidates = (item.Track.Candidates ?? Array.Empty<StreamCandidate>())
            .Select(c => new StreamCandidateViewModel(c, index, c.Id == item.Track.SelectedCandidateId))
            .ToArray();
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

    /// <summary>
    /// The other takes the streaming source found for this track. Empty for a local file, and
    /// for a streamed track that has not been played yet — candidates are searched on play.
    /// </summary>
    public IReadOnlyList<StreamCandidateViewModel> Candidates { get; }

    public bool HasCandidates => Candidates.Count > 1;

    /// <summary>What is actually playing, for the row's source badge.</summary>
    public string? SourceLabel => Item.Track.IsLocal
        ? null
        : Item.Track.ResolvedStream is { } stream
            ? $"{stream.Source.Provider}{(stream.BitrateKbps is > 0 ? $" · {stream.BitrateKbps} kbps" : string.Empty)}"
            : Item.Track.Source?.Provider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isCurrent;
}

/// <summary>One alternative source for a queued track, as shown in its source picker.</summary>
public sealed class StreamCandidateViewModel
{
    public StreamCandidateViewModel(StreamCandidate candidate, int queueIndex, bool isSelected)
    {
        Candidate = candidate;
        QueueIndex = queueIndex;
        IsSelected = isSelected;
    }

    public StreamCandidate Candidate { get; }
    public int QueueIndex { get; }
    public string Id => Candidate.Id;
    public string Title => Candidate.Title;
    public string? ThumbnailUrl => Candidate.ThumbnailUrl;
    public bool IsSelected { get; }
    public bool Failed => Candidate.Failed;

    public string Detail
    {
        get
        {
            string length = Candidate.Duration is { } duration
                ? NowPlayingViewModel.FormatTime(duration)
                : "unknown length";
            string artist = string.IsNullOrWhiteSpace(Candidate.Artist) ? Candidate.Source.Provider : Candidate.Artist!;
            return Failed ? $"{artist} · {length} · could not be played" : $"{artist} · {length}";
        }
    }
}
