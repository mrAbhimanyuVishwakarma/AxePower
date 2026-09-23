using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public class PlaybackQueueTests
{
    internal static Track T(string name) => new(0, $@"C:\music\{name}.flac", name, new[] { "Artist" }, null, null, null, null, null,
        Array.Empty<string>(), TimeSpan.FromMinutes(3), new AudioFormatInfo("FLAC", 44100, 16, 2, 900, true), null, null, false);

    private static List<Track> Tracks(int n) => Enumerable.Range(0, n).Select(i => T($"t{i}")).ToList();

    private static PlaybackQueue Open(int n, int start = 0, RepeatMode repeat = RepeatMode.Off, int seed = 1)
    {
        var q = new PlaybackQueue(new Random(seed)) { Repeat = repeat };
        q.Open(Tracks(n), start);
        return q;
    }

    private static string[] Titles(PlaybackQueue q) => q.Items.Select(i => i.Track.Title).ToArray();

    // ── Open / jump ─────────────────────────────────────────────────────────

    [Fact]
    public void Open_SetsItemsAndStartIndex()
    {
        var q = Open(4, start: 2);
        Assert.Equal(4, q.Count);
        Assert.Equal(2, q.Index);
        Assert.Equal("t2", q.Current!.Track.Title);
        Assert.False(q.Shuffle);
    }

    [Fact]
    public void Open_Empty_HasNoCurrent()
    {
        var q = new PlaybackQueue();
        q.Open(Array.Empty<Track>());
        Assert.Equal(-1, q.Index);
        Assert.Null(q.Current);
        Assert.Equal(QueueStep.None, q.Next());
    }

    [Fact]
    public void Open_WithShuffle_ShufflesAndStartsAtZero_AndCanRestoreOrder()
    {
        var q = new PlaybackQueue(new Random(7));
        q.Open(Tracks(20), startIndex: 5, shuffle: true);

        Assert.True(q.Shuffle);
        Assert.Equal(0, q.Index);
        Assert.NotEqual(Tracks(20).Select(t => t.Title), Titles(q));

        q.SetShuffle(false);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"t{i}"), Titles(q));
    }

    [Fact]
    public void Jump_PlaysThatItem_OutOfRangeDoesNothing()
    {
        var q = Open(3);
        Assert.Equal(QueueStep.Play, q.Jump(2));
        Assert.Equal(2, q.Index);
        Assert.Equal(QueueStep.None, q.Jump(9));
        Assert.Equal(2, q.Index);
    }

    // ── Next / auto-advance / previous ──────────────────────────────────────

    [Fact]
    public void Next_AdvancesThenStopsAtEnd_WhenRepeatOff()
    {
        var q = Open(2);
        Assert.Equal(QueueStep.Play, q.Next());
        Assert.Equal(1, q.Index);
        Assert.Equal(QueueStep.Stop, q.Next());
        Assert.Equal(1, q.Index); // stays on the last item
    }

    [Fact]
    public void Next_WrapsToFirst_WhenRepeatAll()
    {
        var q = Open(2, start: 1, repeat: RepeatMode.All);
        Assert.Equal(QueueStep.Play, q.Next());
        Assert.Equal(0, q.Index);
    }

    [Fact]
    public void ManualNext_WithRepeatOne_StillAdvances()
    {
        var q = Open(3, repeat: RepeatMode.One);
        Assert.Equal(QueueStep.Play, q.Next());
        Assert.Equal(1, q.Index);
    }

    [Fact]
    public void AdvanceAfterEnd_RepeatOne_RestartsSameItem()
    {
        var q = Open(3, start: 1, repeat: RepeatMode.One);
        Assert.Equal(QueueStep.Restart, q.AdvanceAfterEnd());
        Assert.Equal(1, q.Index);
    }

    [Theory]
    [InlineData(RepeatMode.Off, QueueStep.Stop, 2)]
    [InlineData(RepeatMode.All, QueueStep.Play, 0)]
    public void AdvanceAfterEnd_OnLastItem(RepeatMode repeat, QueueStep expected, int expectedIndex)
    {
        var q = Open(3, start: 2, repeat: repeat);
        Assert.Equal(expected, q.AdvanceAfterEnd());
        Assert.Equal(expectedIndex, q.Index);
    }

    [Fact]
    public void Previous_MoreThanThreeSecondsIn_RestartsTrack()
    {
        var q = Open(3, start: 1);
        Assert.Equal(QueueStep.Restart, q.Previous(TimeSpan.FromSeconds(3.5)));
        Assert.Equal(1, q.Index);
    }

    [Fact]
    public void Previous_WithinThreeSeconds_GoesBack()
    {
        var q = Open(3, start: 1);
        Assert.Equal(QueueStep.Play, q.Previous(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, q.Index);
    }

    [Theory]
    [InlineData(RepeatMode.Off, QueueStep.Restart, 0)]
    [InlineData(RepeatMode.All, QueueStep.Play, 2)]
    public void Previous_OnFirstItem(RepeatMode repeat, QueueStep expected, int expectedIndex)
    {
        var q = Open(3, start: 0, repeat: repeat);
        Assert.Equal(expected, q.Previous(TimeSpan.Zero));
        Assert.Equal(expectedIndex, q.Index);
    }

    [Fact]
    public void PeekAfterEnd_FollowsRepeatRules()
    {
        var q = Open(2, start: 1);
        Assert.Null(q.PeekAfterEnd());

        q.Repeat = RepeatMode.All;
        Assert.Equal(q.Items[0].Key, q.PeekAfterEnd()!.Key);

        q.Repeat = RepeatMode.One;
        var replay = q.PeekAfterEnd()!;
        Assert.NotEqual(q.Current!.Key, replay.Key);            // a new key the player maps back
        Assert.Same(q.Current!.Track, replay.Track);
    }

    // ── Shuffle ─────────────────────────────────────────────────────────────

    [Fact]
    public void ShuffleOn_KeepsCurrentFirst_AndUnshuffleRestoresOrderAndCurrent()
    {
        var q = Open(30, start: 12);
        var current = q.Current!.Key;

        q.SetShuffle(true);
        Assert.Equal(0, q.Index);
        Assert.Equal(current, q.Current!.Key);
        Assert.Equal(30, q.Count);

        q.SetShuffle(false);
        Assert.Equal(Enumerable.Range(0, 30).Select(i => $"t{i}"), Titles(q));
        Assert.Equal(12, q.Index);
        Assert.Equal(current, q.Current!.Key);
    }

    [Fact]
    public void Unshuffle_AppendsItemsAddedWhileShuffled_AndDropsRemovedOnes()
    {
        var q = Open(5, start: 0);
        q.SetShuffle(true);

        q.AddToQueue(new[] { T("added") });
        int removeAt = q.Items.ToList().FindIndex(i => i.Track.Title == "t3");
        q.Remove(removeAt);

        q.SetShuffle(false);
        Assert.Equal(new[] { "t0", "t1", "t2", "t4", "added" }, Titles(q));
    }

    [Fact]
    public void Shuffle_IsDeterministicForAGivenSeed()
    {
        var a = Open(15, seed: 42);
        var b = Open(15, seed: 42);
        a.SetShuffle(true);
        b.SetShuffle(true);
        Assert.Equal(Titles(a), Titles(b));
    }

    // ── Editing ─────────────────────────────────────────────────────────────

    [Fact]
    public void PlayNext_InsertsAfterCurrent_InOrder()
    {
        var q = Open(3, start: 0);
        q.PlayNext(new[] { T("a"), T("b") });
        Assert.Equal(new[] { "t0", "a", "b", "t1", "t2" }, Titles(q));
        Assert.Equal(0, q.Index);
    }

    [Fact]
    public void Insert_BeforeCurrent_ShiftsIndex()
    {
        var q = Open(3, start: 1);
        q.Insert(0, T("x"));
        Assert.Equal(2, q.Index);
        Assert.Equal("t1", q.Current!.Track.Title);

        q.Insert(3, T("y")); // after current
        Assert.Equal(2, q.Index);
    }

    [Fact]
    public void Remove_BeforeCurrent_ShiftsIndex()
    {
        var q = Open(4, start: 2);
        Assert.Equal(RemoveOutcome.Unchanged, q.Remove(0));
        Assert.Equal(1, q.Index);
        Assert.Equal("t2", q.Current!.Track.Title);
    }

    [Fact]
    public void Remove_CurrentItem_NextOneTakesOver()
    {
        var q = Open(4, start: 1);
        Assert.Equal(RemoveOutcome.CurrentReplaced, q.Remove(1));
        Assert.Equal(1, q.Index);
        Assert.Equal("t2", q.Current!.Track.Title);
    }

    [Fact]
    public void Remove_CurrentLastItem_Stops_OnNewLast()
    {
        var q = Open(3, start: 2);
        Assert.Equal(RemoveOutcome.Stopped, q.Remove(2));
        Assert.Equal(1, q.Index);
    }

    [Fact]
    public void Remove_OnlyItem_EmptiesQueue()
    {
        var q = Open(1);
        Assert.Equal(RemoveOutcome.Emptied, q.Remove(0));
        Assert.Equal(-1, q.Index);
        Assert.Equal(0, q.Count);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 3)]
    public void Move_KeepsCurrentItemCurrent(int from, int to)
    {
        var q = Open(4, start: 2);
        var current = q.Current!.Key;

        q.Move(from, to);

        Assert.Equal(current, q.Current!.Key);
        Assert.Equal(4, q.Count);
    }

    [Fact]
    public void Duplicates_AreAllowed_WithDistinctKeys()
    {
        var q = new PlaybackQueue();
        var same = T("same");
        q.Open(new[] { same, same });
        Assert.Equal(2, q.Count);
        Assert.NotEqual(q.Items[0].Key, q.Items[1].Key);
    }

    [Fact]
    public void Capacity_IsTenThousand()
    {
        var q = new PlaybackQueue();
        q.Open(Tracks(PlaybackQueue.MaxItems));

        var ex = Assert.Throws<QueueFullException>(() => q.AddToQueue(new[] { T("one too many") }));
        Assert.Equal("Queue is full (10,000 tracks)", ex.Message);
        Assert.Throws<QueueFullException>(() => new PlaybackQueue().Open(Tracks(PlaybackQueue.MaxItems + 1)));
    }

    [Fact]
    public void Restore_KeepsKeysOrderIndexAndShuffle()
    {
        var items = Tracks(4).Select(QueueItem.For).ToList();
        var original = new[] { items[2].Key, items[0].Key, items[3].Key, items[1].Key };

        var q = new PlaybackQueue();
        q.Restore(items, 3, original);

        Assert.True(q.Shuffle);
        Assert.Equal(3, q.Index);
        Assert.Equal(items.Select(i => i.Key), q.Items.Select(i => i.Key));

        q.SetShuffle(false);
        Assert.Equal(original, q.Items.Select(i => i.Key));
    }
}
