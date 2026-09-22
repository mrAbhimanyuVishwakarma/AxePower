using System.Collections.Generic;
using System.IO;
using System.Linq;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public class PlaybackStateStoreTests
{
    private static PersistedPlaybackState Sample() => new()
    {
        Queue = new List<PersistedQueueEntry>
        {
            new() { TrackId = 0, Path = @"D:\Music\a.flac" },
            new() { TrackId = 42, Path = @"D:\Music\b.flac" },
            new() { TrackId = 0, Path = @"D:\Music\c.flac" },
        },
        OriginalOrder = new List<int> { 2, 0, 1 },
        Index = 1,
        PositionMs = 81234,
        Volume = 0.8,
        Muted = true,
        Shuffle = true,
        Repeat = RepeatMode.All,
        Speed = 1.25,
        PitchSemitones = -2
    };

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var audio = new TestAudio();
        var store = new PlaybackStateStore(new MusicStoragePaths(audio.Folder));

        store.Save(Sample());
        var loaded = store.Load()!;

        Assert.Equal(3, loaded.Queue.Count);
        Assert.Equal(42, loaded.Queue[1].TrackId);
        Assert.Equal(new[] { 2, 0, 1 }, loaded.OriginalOrder);
        Assert.Equal(1, loaded.Index);
        Assert.Equal(81234, loaded.PositionMs);
        Assert.Equal(0.8, loaded.Volume);
        Assert.True(loaded.Muted);
        Assert.True(loaded.Shuffle);
        Assert.Equal(RepeatMode.All, loaded.Repeat);
        Assert.Equal(1.25, loaded.Speed);
        Assert.Equal(-2, loaded.PitchSemitones);
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempFile_AndKeepsABackup()
    {
        using var audio = new TestAudio();
        var paths = new MusicStoragePaths(audio.Folder);
        var store = new PlaybackStateStore(paths);

        store.Save(Sample());
        var second = Sample();
        second.Index = 2;
        store.Save(second);

        Assert.False(File.Exists(paths.PlaybackStateFile + ".temp"));
        Assert.True(File.Exists(paths.PlaybackStateFile + ".backup"));
        Assert.Equal(2, store.Load()!.Index);
        Assert.Contains("\"repeat\": \"All\"", File.ReadAllText(paths.PlaybackStateFile));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("{ \"version\": 99, \"queue\": [] }")]
    public void CorruptOrUnknownFile_LoadsAsNothingSaved(string content)
    {
        using var audio = new TestAudio();
        var paths = new MusicStoragePaths(audio.Folder);
        File.WriteAllText(paths.PlaybackStateFile, content);

        Assert.Null(new PlaybackStateStore(paths).Load());
    }

    [Fact]
    public void MissingFile_LoadsAsNothingSaved()
    {
        using var audio = new TestAudio();
        Assert.Null(new PlaybackStateStore(new MusicStoragePaths(audio.Folder)).Load());
    }

    [Fact]
    public void MissingFiles_AreDropped_IndexAndOrderFollow()
    {
        var state = Sample();
        var restored = state.WithoutMissingFiles(p => !p.EndsWith("a.flac"), out int dropped);

        Assert.Equal(1, dropped);
        Assert.Equal(new[] { @"D:\Music\b.flac", @"D:\Music\c.flac" }, restored.Queue.Select(q => q.Path));
        Assert.Equal(0, restored.Index);                     // b stayed current
        Assert.Equal(81234, restored.PositionMs);            // and keeps its position
        Assert.Equal(new[] { 1, 0 }, restored.OriginalOrder); // c, b
    }

    [Fact]
    public void MissingCurrentFile_NextSurvivorBecomesCurrent_FromTheStart()
    {
        var state = Sample();
        var restored = state.WithoutMissingFiles(p => !p.EndsWith("b.flac"), out _);

        Assert.Equal(1, restored.Index);                     // c
        Assert.Equal(0, restored.PositionMs);
    }

    [Fact]
    public void AllFilesMissing_LeavesAnEmptyQueue()
    {
        var restored = Sample().WithoutMissingFiles(_ => false, out int dropped);
        Assert.Equal(3, dropped);
        Assert.Empty(restored.Queue);
        Assert.Equal(-1, restored.Index);
    }
}
