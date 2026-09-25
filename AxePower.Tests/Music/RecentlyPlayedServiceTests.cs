using System;
using System.IO;
using System.Linq;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public class RecentlyPlayedServiceTests
{
    private static Track Local(string path, string title = "Song") => new(
        0, path, title, new[] { "Artist" }, "Album", null, null, null, null,
        Array.Empty<string>(), TimeSpan.FromMinutes(3),
        new AudioFormatInfo("FLAC", 44100, 16, 2, 900, true), null, null, false);

    private static Track Streamed(string id, string title = "Streamed") =>
        Track.ForProvider(new ProviderRef("youtube", id, "https://example.test/" + id), title,
            new[] { "Singer" }, TimeSpan.FromSeconds(212), "Album", "https://example.test/thumb.jpg");

    private static PlayerState Playing(QueueItem item, bool playing = true) =>
        PlayerState.Empty with { Queue = new[] { item }, Index = 0, Playing = playing };

    [Fact]
    public void KeepsLocalAndStreamedApart_NewestFirst()
    {
        using var audio = new TestAudio();
        var clock = new StepClock();
        var service = new RecentlyPlayedService(new MusicStoragePaths(audio.Folder), clock);

        service.Record(Local(@"D:\Music\a.flac", "A"));
        service.Record(Streamed("v1", "Y1"));
        service.Record(Local(@"D:\Music\b.flac", "B"));

        Assert.Equal(new[] { "B", "A" }, service.Get(streamed: false).Select(p => p.Title));
        Assert.Equal(new[] { "Y1" }, service.Get(streamed: true).Select(p => p.Title));
    }

    [Fact]
    public void ReplayingASong_MovesItToTheTop_WithoutADuplicate()
    {
        using var audio = new TestAudio();
        var service = new RecentlyPlayedService(new MusicStoragePaths(audio.Folder), new StepClock());

        service.Record(Local(@"D:\Music\a.flac", "A"));
        service.Record(Local(@"D:\Music\b.flac", "B"));
        service.Record(Local(@"d:\music\A.FLAC", "A"));

        Assert.Equal(new[] { "A", "B" }, service.Get(streamed: false).Select(p => p.Title));
    }

    [Fact]
    public void EachListIsCappedOnItsOwn()
    {
        using var audio = new TestAudio();
        var service = new RecentlyPlayedService(new MusicStoragePaths(audio.Folder), new StepClock());

        service.Record(Local(@"D:\Music\keep.flac", "Keep"));
        for (int i = 0; i < RecentlyPlayedService.Capacity + 5; i++)
            service.Record(Streamed("v" + i));

        Assert.Equal(RecentlyPlayedService.Capacity, service.Get(streamed: true).Count);
        Assert.Equal("Keep", Assert.Single(service.Get(streamed: false)).Title);
    }

    [Fact]
    public void SurvivesARestart_AndRebuildsPlayableTracks()
    {
        using var audio = new TestAudio();
        var paths = new MusicStoragePaths(audio.Folder);
        var first = new RecentlyPlayedService(paths, new StepClock());
        first.Record(Local(@"D:\Music\a.flac", "A"));
        first.Record(Streamed("abc", "Y"));

        var second = new RecentlyPlayedService(paths, new StepClock());

        var local = Assert.Single(second.Get(streamed: false)).ToTrack();
        Assert.True(local.IsLocal);
        Assert.Equal(@"D:\Music\a.flac", local.Path);

        var remote = Assert.Single(second.Get(streamed: true)).ToTrack();
        Assert.False(remote.IsLocal);
        Assert.Equal(new ProviderRef("youtube", "abc", "https://example.test/abc"), remote.Source);
        Assert.Equal(TimeSpan.FromSeconds(212), remote.Duration);
        Assert.Equal("https://example.test/thumb.jpg", remote.ThumbnailUrl);
    }

    [Fact]
    public void ACorruptFile_IsAnEmptyHistory()
    {
        using var audio = new TestAudio();
        var paths = new MusicStoragePaths(audio.Folder);
        File.WriteAllText(paths.RecentlyPlayedFile, "{ not json");

        var service = new RecentlyPlayedService(paths, new StepClock());

        Assert.Empty(service.Get(streamed: false));
        service.Record(Local(@"D:\Music\a.flac"));
        Assert.Single(service.Get(streamed: false));
    }

    [Fact]
    public void PlayerState_RecordsOnlyOncePlaying_AndOncePerQueueEntry()
    {
        using var audio = new TestAudio();
        var service = new RecentlyPlayedService(new MusicStoragePaths(audio.Folder), new StepClock());
        int changes = 0;
        service.Changed += (_, _) => changes++;
        var item = QueueItem.For(Local(@"D:\Music\a.flac"));

        // A session restored paused is not a play
        service.OnStateChanged(Playing(item, playing: false));
        Assert.Empty(service.Get(streamed: false));

        service.OnStateChanged(Playing(item));
        service.OnStateChanged(Playing(item));   // position ticks, pause/resume: same entry
        Assert.Single(service.Get(streamed: false));
        Assert.Equal(1, changes);
    }

    private sealed class StepClock : IClock
    {
        private DateTime _now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => _now = _now.AddSeconds(1);
    }
}
