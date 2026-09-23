using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

public sealed class QueueFullException : InvalidOperationException
{
    public QueueFullException() : base($"Queue is full ({PlaybackQueue.MaxItems:N0} tracks)") { }
}

/// <summary>What the player should do after a navigation request.</summary>
public enum QueueStep
{
    /// <summary>Load and play the item at <see cref="PlaybackQueue.Index"/> from the start.</summary>
    Play,

    /// <summary>Restart the current item (seek to 0).</summary>
    Restart,

    /// <summary>End of queue: stop on the current (last) item at position 0, marked completed.</summary>
    Stop,

    /// <summary>Nothing to do (empty queue).</summary>
    None
}

/// <summary>What happened to the current item when an entry was removed.</summary>
public enum RemoveOutcome
{
    /// <summary>The current item is still current.</summary>
    Unchanged,

    /// <summary>The current item was removed; the item now at <see cref="PlaybackQueue.Index"/> takes over.</summary>
    CurrentReplaced,

    /// <summary>The current item was removed and nothing follows it; playback stops.</summary>
    Stopped,

    /// <summary>The queue is now empty.</summary>
    Emptied
}

/// <summary>
/// The play queue as pure logic (no audio), implementing the navigation, shuffle and editing
/// rules. Entries are addressed by <see cref="QueueItem.Key"/>, so duplicates are fine.
/// </summary>
public sealed class PlaybackQueue
{
    public const int MaxItems = 10_000;
    public static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);

    private readonly List<QueueItem> _items = new();
    private readonly Random _random;
    private List<Guid>? _originalOrder;    // keys in unshuffled order, only while shuffled

    public PlaybackQueue(Random? random = null)
    {
        _random = random ?? new Random();
    }

    public IReadOnlyList<QueueItem> Items => _items;
    public int Count => _items.Count;
    public int Index { get; private set; } = -1;
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public QueueItem? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;

    /// <summary>Keys in unshuffled order while shuffled; null otherwise. Used for persistence.</summary>
    public IReadOnlyList<Guid>? OriginalOrder => _originalOrder;

    public int IndexOfKey(Guid key) => _items.FindIndex(i => i.Key == key);

    // ── Opening ─────────────────────────────────────────────────────────────

    /// <summary>Replaces the queue. With shuffle, the whole list is shuffled and playback starts at 0.</summary>
    public void Open(IReadOnlyList<Track> tracks, int startIndex = 0, bool shuffle = false)
    {
        if (tracks.Count > MaxItems) throw new QueueFullException();

        _items.Clear();
        _items.AddRange(tracks.Select(QueueItem.For));
        _originalOrder = null;
        Shuffle = false;
        Index = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);

        if (shuffle && _items.Count > 0)
        {
            _originalOrder = _items.Select(i => i.Key).ToList();
            FisherYates(0);
            Index = 0;
            Shuffle = true;
        }
    }

    /// <summary>Restores a saved queue exactly (keys, order, index and shuffle state).</summary>
    public void Restore(IReadOnlyList<QueueItem> items, int index, IReadOnlyList<Guid>? originalOrder)
    {
        _items.Clear();
        _items.AddRange(items.Take(MaxItems));
        Index = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);

        var present = _items.Select(i => i.Key).ToHashSet();
        _originalOrder = originalOrder?.Where(present.Contains).ToList();
        Shuffle = _originalOrder != null;
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    /// <summary>Manual next. At the end: Repeat All wraps to the first item, otherwise stops.</summary>
    public QueueStep Next()
    {
        if (_items.Count == 0) return QueueStep.None;

        if (Index < _items.Count - 1)
        {
            Index++;
            return QueueStep.Play;
        }

        if (Repeat == RepeatMode.All)
        {
            Index = 0;
            return QueueStep.Play;
        }

        return QueueStep.Stop;
    }

    /// <summary>The current track finished on its own. Repeat One replays it; otherwise like Next.</summary>
    public QueueStep AdvanceAfterEnd()
    {
        if (_items.Count == 0) return QueueStep.None;
        if (Repeat == RepeatMode.One) return QueueStep.Restart;
        return Next();
    }

    /// <summary>
    /// Previous. More than 3 s into the track it restarts the track; otherwise it goes back one.
    /// On the first item Repeat All wraps to the last item, otherwise the track restarts.
    /// </summary>
    public QueueStep Previous(TimeSpan position)
    {
        if (_items.Count == 0) return QueueStep.None;
        if (position > RestartThreshold) return QueueStep.Restart;

        if (Index > 0)
        {
            Index--;
            return QueueStep.Play;
        }

        if (Repeat == RepeatMode.All && _items.Count > 1)
        {
            Index = _items.Count - 1;
            return QueueStep.Play;
        }

        return QueueStep.Restart;
    }

    public QueueStep Jump(int index)
    {
        if (index < 0 || index >= _items.Count) return QueueStep.None;
        Index = index;
        return QueueStep.Play;
    }

    /// <summary>
    /// What follows the current item if it ends on its own — the engine preloads this for a
    /// gapless join. Repeat One returns the same track under a new key, so the engine treats
    /// it as a new entry; the player maps that key back to the current item.
    /// </summary>
    public QueueItem? PeekAfterEnd()
    {
        var current = Current;
        if (current == null) return null;

        if (Repeat == RepeatMode.One) return current with { Key = Guid.NewGuid() };
        if (Index < _items.Count - 1) return _items[Index + 1];
        if (Repeat == RepeatMode.All) return _items[0];
        return null;
    }

    // ── Shuffle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// On: the current item moves to the top and the rest is shuffled. Off: the original order
    /// comes back, including items added while shuffled; the current item stays current.
    /// </summary>
    public void SetShuffle(bool on)
    {
        if (on == Shuffle) return;

        if (on)
        {
            _originalOrder = _items.Select(i => i.Key).ToList();
            if (Current is { } current)
            {
                _items.RemoveAt(Index);
                _items.Insert(0, current);
                Index = 0;
                FisherYates(1);
            }
            else
            {
                FisherYates(0);
            }
            Shuffle = true;
        }
        else
        {
            var current = Current;
            var byKey = _items.ToDictionary(i => i.Key);
            var ordered = new List<QueueItem>(_items.Count);

            foreach (var key in _originalOrder ?? new List<Guid>())
            {
                if (byKey.Remove(key, out var item)) ordered.Add(item);
            }
            // Anything not in the original order keeps its current relative order at the end
            ordered.AddRange(_items.Where(i => byKey.ContainsKey(i.Key)));

            _items.Clear();
            _items.AddRange(ordered);
            Index = current == null ? (_items.Count == 0 ? -1 : 0) : IndexOfKey(current.Key);
            _originalOrder = null;
            Shuffle = false;
        }
    }

    private void FisherYates(int from)
    {
        for (int i = _items.Count - 1; i > from; i--)
        {
            int j = _random.Next(from, i + 1);
            (_items[i], _items[j]) = (_items[j], _items[i]);
        }
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    /// <summary>Inserts right after the current item, keeping the given order.</summary>
    public void PlayNext(IEnumerable<Track> tracks)
    {
        var items = tracks.Select(QueueItem.For).ToList();
        EnsureCapacity(items.Count);

        int at = Index < 0 ? _items.Count : Index + 1;
        _items.InsertRange(at, items);
        _originalOrder?.AddRange(items.Select(i => i.Key));
    }

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        var items = tracks.Select(QueueItem.For).ToList();
        EnsureCapacity(items.Count);

        _items.AddRange(items);
        _originalOrder?.AddRange(items.Select(i => i.Key));
    }

    public void Insert(int index, Track track)
    {
        EnsureCapacity(1);

        index = Math.Clamp(index, 0, _items.Count);
        var item = QueueItem.For(track);
        _items.Insert(index, item);
        _originalOrder?.Add(item.Key);

        if (Index >= 0 && index <= Index) Index++;
    }

    public RemoveOutcome Remove(int index)
    {
        if (index < 0 || index >= _items.Count) return RemoveOutcome.Unchanged;

        var removed = _items[index];
        _items.RemoveAt(index);
        _originalOrder?.Remove(removed.Key);

        if (_items.Count == 0)
        {
            Index = -1;
            _originalOrder = Shuffle ? new List<Guid>() : null;
            return RemoveOutcome.Emptied;
        }

        if (index < Index)
        {
            Index--;
            return RemoveOutcome.Unchanged;
        }

        if (index > Index) return RemoveOutcome.Unchanged;

        // The current item itself was removed
        if (index < _items.Count) return RemoveOutcome.CurrentReplaced;

        Index = _items.Count - 1;
        return RemoveOutcome.Stopped;
    }

    /// <summary>Reorders one entry; the current item stays current.</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count) return;
        to = Math.Clamp(to, 0, _items.Count - 1);
        if (from == to) return;

        var current = Current;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);

        if (current != null) Index = IndexOfKey(current.Key);
    }

    /// <summary>
    /// Swaps the track behind one queue entry, keeping its key and its place. Used after a
    /// streaming source resolves, so the candidate list and the resolved URL survive in the
    /// queue instead of being recomputed on every play.
    /// </summary>
    public bool Replace(Guid key, Track track)
    {
        int index = IndexOfKey(key);
        if (index < 0) return false;

        _items[index] = _items[index] with { Track = track };
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        Index = -1;
        _originalOrder = Shuffle ? new List<Guid>() : null;
    }

    private void EnsureCapacity(int adding)
    {
        if (_items.Count + adding > MaxItems) throw new QueueFullException();
    }
}
