using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Music;

/// <summary>The saved session in playback-state.json: queue, position and session-level controls.</summary>
public sealed class PersistedPlaybackState
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("queue")]
    public List<PersistedQueueEntry> Queue { get; set; } = new();

    /// <summary>Unshuffled order as indices into <see cref="Queue"/>; null when not shuffled.</summary>
    [JsonPropertyName("originalOrder")]
    public List<int>? OriginalOrder { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; } = -1;

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; set; }

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; }

    [JsonPropertyName("repeat")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("pitchSemitones")]
    public double PitchSemitones { get; set; }

    [JsonPropertyName("mixOffset")]
    public int? MixOffset { get; set; }

    /// <summary>
    /// Drops entries whose file no longer exists and fixes up the index and the unshuffled
    /// order. If the current entry is gone, the next surviving entry becomes current at 0.
    /// Streamed entries have no file and are always kept.
    /// </summary>
    public PersistedPlaybackState WithoutMissingFiles(Func<string, bool> fileExists, out int dropped)
    {
        var keep = new List<int>();
        for (int i = 0; i < Queue.Count; i++)
        {
            if (Queue[i].Stream is not null || (!string.IsNullOrEmpty(Queue[i].Path) && fileExists(Queue[i].Path))) keep.Add(i);
        }

        dropped = Queue.Count - keep.Count;
        var newIndexOf = keep.Select((oldIndex, newIndex) => (oldIndex, newIndex)).ToDictionary(p => p.oldIndex, p => p.newIndex);

        int index = -1;
        long position = PositionMs;
        if (keep.Count > 0)
        {
            if (newIndexOf.TryGetValue(Index, out var same))
            {
                index = same;
            }
            else
            {
                // The current file is gone: continue with the next one that survived, from the start
                int next = keep.FindIndex(i => i > Index);
                index = next >= 0 ? next : keep.Count - 1;
                position = 0;
            }
        }

        return new PersistedPlaybackState
        {
            Version = Version,
            Queue = keep.Select(i => Queue[i]).ToList(),
            OriginalOrder = OriginalOrder?.Where(newIndexOf.ContainsKey).Select(i => newIndexOf[i]).ToList(),
            Index = index,
            PositionMs = index < 0 ? 0 : position,
            Volume = Volume,
            Muted = Muted,
            Shuffle = Shuffle,
            Repeat = Repeat,
            Speed = Speed,
            PitchSemitones = PitchSemitones,
            MixOffset = MixOffset
        };
    }
}

public sealed class PersistedQueueEntry
{
    /// <summary>Library id, or 0 for a file opened directly.</summary>
    [JsonPropertyName("trackId")]
    public long TrackId { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// For a streamed track, which has no file: where it came from and what to show until it is
    /// played. Null for a local file. Sessions saved before this existed simply lack it.
    /// </summary>
    [JsonPropertyName("stream")]
    public RecentPlay? Stream { get; set; }
}
