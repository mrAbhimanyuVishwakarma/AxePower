using System;
using System.IO;
using System.Linq;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public class RecentSearchesServiceTests
{
    private static Track Song(string id, string title) =>
        Track.ForProvider(new ProviderRef("youtube", id), title, new[] { "Singer" }, TimeSpan.FromSeconds(200));

    private static RecentSearchesService Service(TestAudio audio) =>
        new(new MusicStoragePaths(audio.Folder), new StepClock());

    [Fact]
    public void KeepsTheLastSeven_NewestFirst()
    {
        using var audio = new TestAudio();
        var service = Service(audio);

        for (int i = 1; i <= 9; i++)
            service.Add("query " + i, new[] { Song("v" + i, "Song " + i) });

        Assert.Equal(RecentSearchesService.Capacity, service.Get().Count);
        Assert.Equal("query 9", service.Get()[0].Query);
        Assert.Equal("query 3", service.Get().Last().Query);
    }

    [Fact]
    public void SearchingAgain_MovesItToTheTop_AndReplacesItsSongs()
    {
        using var audio = new TestAudio();
        var service = Service(audio);

        service.Add("Arijit Singh", new[] { Song("a", "Old") });
        service.Add("lofi", new[] { Song("b", "Lofi") });
        service.Add("  arijit singh ", new[] { Song("c", "New") });

        Assert.Equal(new[] { "arijit singh", "lofi" }, service.Get().Select(s => s.Query));
        Assert.Equal("New", Assert.Single(service.Find("ARIJIT SINGH")!.Results).Title);
    }

    [Fact]
    public void SurvivesARestart_WithPlayableSongsInOrder()
    {
        using var audio = new TestAudio();
        Service(audio).Add("hindi", new[] { Song("x1", "First"), Song("x2", "Second") });

        var reopened = Service(audio);
        var tracks = reopened.Find("hindi")!.Results.Select(r => r.ToTrack()).ToList();

        Assert.Equal(new[] { "First", "Second" }, tracks.Select(t => t.Title));
        Assert.All(tracks, t => Assert.False(t.IsLocal));
        Assert.Equal("x2", tracks[1].Source!.Id);
    }

    [Fact]
    public void Remove_ForgetsASearch_AcrossRestarts()
    {
        using var audio = new TestAudio();
        var service = Service(audio);
        service.Add("keep", new[] { Song("k", "K") });
        service.Add("drop", new[] { Song("d", "D") });

        service.Remove("DROP");

        Assert.Equal("keep", Assert.Single(Service(audio).Get()).Query);
    }

    [Fact]
    public void BlankQueriesAndACorruptFile_AreIgnored()
    {
        using var audio = new TestAudio();
        var paths = new MusicStoragePaths(audio.Folder);
        File.WriteAllText(paths.RecentSearchesFile, "[ broken");
        var service = new RecentSearchesService(paths, new StepClock());

        Assert.Empty(service.Get());
        service.Add("   ", new[] { Song("a", "A") });
        Assert.Empty(service.Get());
    }

    private sealed class StepClock : IClock
    {
        private DateTime _now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => _now = _now.AddSeconds(1);
    }
}
