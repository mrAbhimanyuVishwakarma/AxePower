using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// What the player does with tracks that are not files: resolving before loading, preloading the
/// next one, and letting the listener pick a different source. Fake engine, fake resolver — no
/// network, no audio device, and a settings service in a temp folder.
/// </summary>
public sealed class MusicPlayerStreamingTests : IDisposable
{
    private readonly TestAudio _files = new();
    private readonly RecordingEngine _engine = new();
    private readonly FakeResolver _resolver = new();
    private readonly MusicPlayerService _player;

    public MusicPlayerStreamingTests()
    {
        var log = new LogService();
        var settings = new SettingsService(log, Path.Combine(_files.Folder, "settings"));
        var store = new PlaybackStateStore(new MusicStoragePaths(_files.Folder));
        var reader = new LooseTrackReader(new StubTagReader());

        _player = new MusicPlayerService(
            _engine, store, reader, settings, new NotificationService(), log,
            new StubClock(), new PlaybackQueue(new Random(1)), _resolver);
    }

    public void Dispose()
    {
        _player.Dispose();
        _files.Dispose();
    }

    private static Track Remote(string title) =>
        Track.ForProvider(new ProviderRef("catalog", title), title, new[] { "Artist" }, TimeSpan.FromMinutes(3));

    [Fact]
    public async Task AStreamedTrackIsResolvedBeforeTheEngineEverSeesIt()
    {
        await _player.OpenAsync(new[] { Remote("a") });

        Assert.Equal(new[] { "a" }, _resolver.Prepared);
        Assert.NotNull(_engine.LastLoaded);
        Assert.Equal("https://audio/a", _engine.LastLoaded!.Track.ResolvedStream!.Url);
    }

    [Fact]
    public async Task TheResolvedStreamIsKeptOnTheQueueEntry_SoReplayingDoesNotSearchAgain()
    {
        await _player.OpenAsync(new[] { Remote("a") });

        var queued = _player.State.Queue.Single();
        Assert.Equal("https://audio/a", queued.Track.ResolvedStream!.Url);
        Assert.Equal(3, queued.Track.Candidates!.Count);
    }

    [Fact]
    public async Task TheNextStreamedTrackIsResolvedOnlyWhenPlaybackAdvancesToIt()
    {
        await _player.OpenAsync(new[] { Remote("a"), Remote("b") });

        Assert.Equal(new[] { "a" }, _resolver.Prepared);
        Assert.Null(_engine.LastNext);

        await _player.NextAsync();

        Assert.Equal(new[] { "a", "b" }, _resolver.Prepared);
        Assert.Equal("https://audio/b", _engine.LastLoaded!.Track.ResolvedStream!.Url);
    }

    [Fact]
    public async Task ALocalTrackIsNeverSentToTheResolver()
    {
        await _player.OpenAsync(new[] { PlaybackQueueTests.T("song") });

        Assert.Empty(_resolver.Prepared);
    }

    [Fact]
    public async Task WhenAProgressiveOpenFails_TheTrackIsRetriedAsADownload()
    {
        _engine.FailRemoteStreams = true;

        await _player.OpenAsync(new[] { Remote("a") });

        Assert.Equal(new[] { "a", "a", "a" }, _resolver.Prepared);
        Assert.Equal(new[] { false, false, true }, _resolver.LocalCopyRequests);
        Assert.Equal(StreamProtocol.File, _engine.LastLoaded!.Track.ResolvedStream!.Protocol);
        Assert.Null(_player.State.Error);
    }

    [Fact]
    public async Task AnEngineSideStreamFailure_RefreshesTheCurrentUrlOnce()
    {
        await _player.OpenAsync(new[] { Remote("a") });

        _engine.RaiseError("HTTP 403");
        await WaitForAsync(() => _resolver.Prepared.Count == 2);

        Assert.Equal(new[] { "a", "a" }, _resolver.Prepared);
        Assert.Equal("a", _player.State.Current?.Track.Title);
        Assert.True(_player.State.Playing);
    }

    [Fact]
    public async Task PickingADifferentSourceReloadsTheTrackFromTheStart()
    {
        await _player.OpenAsync(new[] { Remote("a") });
        _engine.Calls.Clear();

        await _player.SelectCandidateAsync(0, "a#2");

        Assert.Equal("a#2", _player.State.Queue[0].Track.SelectedCandidateId);
        Assert.Equal("https://audio/a#2", _engine.LastLoaded!.Track.ResolvedStream!.Url);
        Assert.Contains(_engine.Calls, call => call.StartsWith("load:a@0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PickingASourceForATrackThatIsNotPlayingDoesNotDisturbPlayback()
    {
        await _player.OpenAsync(new[] { Remote("a"), Remote("b") });
        await WaitForAsync(() => _engine.LastNext is not null);
        _engine.Calls.Clear();

        await _player.SelectCandidateAsync(1, "b#3");

        Assert.Equal("b#3", _player.State.Queue[1].Track.SelectedCandidateId);
        Assert.DoesNotContain(_engine.Calls, call => call.StartsWith("load:", StringComparison.Ordinal));
        Assert.True(_player.State.Playing);
    }

    [Fact]
    public async Task AnUnknownCandidateIsIgnoredRatherThanClearingTheStream()
    {
        await _player.OpenAsync(new[] { Remote("a") });
        string before = _player.State.Queue[0].Track.SelectedCandidateId!;

        await _player.SelectCandidateAsync(0, "not-a-candidate");

        Assert.Equal(before, _player.State.Queue[0].Track.SelectedCandidateId);
    }

    [Fact]
    public async Task ATrackThatCannotBeResolvedIsSkippedWithANotice()
    {
        _resolver.FailFor.Add("a");

        await _player.OpenAsync(new[] { Remote("a"), Remote("b") });

        Assert.Equal("b", _player.State.Current?.Track.Title);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class StubClock : IClock
    {
        public DateTime UtcNow => new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class StubTagReader : ITagReader
    {
        public TrackTags Read(string path) => new(
            Path.GetFileNameWithoutExtension(path), "Artist", null, null, null, null, null, null,
            TimeSpan.FromMinutes(3), new AudioFormatInfo("WAV", 44100, 16, 2, 1411, true), null, false, false);

        public byte[]? ReadEmbeddedCover(string path) => null;
        public string? ReadEmbeddedLyrics(string path) => null;
    }

    private sealed class FakeResolver : IStreamResolver
    {
        public List<string> Prepared { get; } = new();
        public List<bool> LocalCopyRequests { get; } = new();
        public HashSet<string> FailFor { get; } = new();

        public Task<Track> PrepareAsync(Track track, bool preferLocalCopy, CancellationToken ct)
        {
            if (track.IsLocal) return Task.FromResult(track);

            Prepared.Add(track.Title);
            LocalCopyRequests.Add(preferLocalCopy);

            if (FailFor.Contains(track.Title))
                throw new StreamResolutionException($"no source for {track.Title}");

            if (track.ResolvedStream is { Protocol: StreamProtocol.Https } && !preferLocalCopy)
                return Task.FromResult(track);

            string id = track.SelectedCandidateId ?? track.Title;
            var source = new ProviderRef("fake", id);

            return Task.FromResult(Candidates(track) with
            {
                SelectedCandidateId = id,
                ResolvedStream = preferLocalCopy
                    ? new AudioStream(@"C:\cache\" + track.Title + ".m4a", StreamProtocol.File, source)
                    : new AudioStream("https://audio/" + id, StreamProtocol.Https, source)
            });
        }

        public Task<Track> RefreshAsync(Track track, bool preferLocalCopy, CancellationToken ct) =>
            PrepareAsync(track with { ResolvedStream = null }, preferLocalCopy, ct);

        public Task<Track> EnsureCandidatesAsync(Track track, CancellationToken ct) =>
            Task.FromResult(track.IsLocal ? track : Candidates(track));

        public Track SelectCandidate(Track track, string candidateId)
        {
            var withCandidates = Candidates(track);
            return withCandidates.Candidates!.Any(c => c.Id == candidateId)
                ? withCandidates with { SelectedCandidateId = candidateId, ResolvedStream = null }
                : track;
        }

        private static Track Candidates(Track track) => track.Candidates is { Count: > 0 }
            ? track
            : track with
            {
                Candidates = new[] { track.Title, track.Title + "#2", track.Title + "#3" }
                    .Select(id => new StreamCandidate(id, "Take " + id, new ProviderRef("fake", id)))
                    .ToList()
            };
    }

    private sealed class RecordingEngine : IAudioEngine
    {
        public List<string> Calls { get; } = new();
        public List<QueueItem?> NextCalls { get; } = new();
        public QueueItem? LastLoaded { get; private set; }
        public QueueItem? LastNext { get; private set; }
        public bool FailRemoteStreams { get; set; }

        public event EventHandler<Guid>? TrackStarted;
        public event EventHandler<Guid>? TrackEnded;
        public event EventHandler<EngineError>? Error;
        public event EventHandler<OutputInfo>? OutputChanged;
        public event EventHandler? DeviceLost;

        public TimeSpan Position { get; private set; }
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public bool IsPlaying { get; private set; }
        public bool SpectrumEnabled { get; set; }

        public Task LoadAsync(QueueItem item, TimeSpan startAt, bool play, CancellationToken ct)
        {
            Calls.Add($"load:{item.Track.Title}@{startAt.TotalMilliseconds:0}:{(play ? "play" : "pause")}");

            // Stands in for a source that refuses ranged requests: only a downloaded copy works.
            if (FailRemoteStreams && item.Track.ResolvedStream is { Protocol: StreamProtocol.Https })
                throw new InvalidOperationException("cannot open remote stream");

            LastLoaded = item;
            Position = startAt;
            IsPlaying = play;
            return Task.CompletedTask;
        }

        public void SetNext(QueueItem? next)
        {
            NextCalls.Add(next);
            if (next is not null) LastNext = next;
        }

        public void Play() { IsPlaying = true; Calls.Add("play"); }
        public void Pause() { IsPlaying = false; Calls.Add("pause"); }
        public Task StopAsync() { IsPlaying = false; LastLoaded = null; Calls.Add("stop"); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken ct) { Position = position; return Task.CompletedTask; }
        public void SetVolume(double volume, bool muted) { }
        public Task FadeToAsync(double gain, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public void SetDsp(DspSettings dsp) { }
        public Task ConfigureOutputAsync(OutputSettings output, CancellationToken ct) => Task.CompletedTask;
        public float[]? GetSpectrum() => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Unused()
        {
            TrackStarted?.Invoke(this, Guid.Empty);
            TrackEnded?.Invoke(this, Guid.Empty);
            Error?.Invoke(this, new EngineError(Guid.Empty, "", null));
            OutputChanged?.Invoke(this, new OutputInfo("", false, 0, 0, 0, false, null));
            DeviceLost?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseError(string message) =>
            Error?.Invoke(this, new EngineError(LastLoaded?.Key ?? Guid.Empty, message, null));
    }
}
