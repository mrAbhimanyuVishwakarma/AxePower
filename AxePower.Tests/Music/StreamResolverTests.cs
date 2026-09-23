using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// Stream resolution against a fake provider: no network, no audio device and no real
/// settings file — the registry is driven by an injected settings service.
/// </summary>
public sealed class StreamResolverTests : IDisposable
{
    private readonly TestAudio _files = new();
    private readonly FakeStreamingProvider _provider = new();
    private readonly TestClock _clock = new();
    private readonly StreamResolver _resolver;
    private readonly ProviderRegistry _registry;

    public StreamResolverTests()
    {
        var log = new LogService();
        var settings = Settings(log, "fake");

        _registry = new ProviderRegistry(new IMusicProvider[] { new LocalFileProvider(), _provider }, settings, log);
        _resolver = new StreamResolver(_registry, _clock, log);
    }

    public void Dispose() => _files.Dispose();

    /// <summary>A settings service in a temp folder, so no test ever writes the real settings.json.</summary>
    private SettingsService Settings(LogService log, string? activeStreaming, int? retries = null)
    {
        var settings = new SettingsService(log, Path.Combine(_files.Folder, Guid.NewGuid().ToString("N")));
        var current = settings.GetSettings();
        current.Music.Providers.Enabled = activeStreaming is null
            ? new List<string>()
            : new List<string> { "local", activeStreaming };
        current.Music.Providers.ActiveStreaming = activeStreaming ?? string.Empty;
        if (retries is { } value) current.Music.Providers.StreamResolutionRetries = value;
        settings.SaveSettings(current);
        return settings;
    }

    private static Track Remote(string title = "Song") =>
        Track.ForProvider(new ProviderRef("spotify", "abc"), title, new[] { "Artist" }, TimeSpan.FromMinutes(3));

    [Fact]
    public async Task LocalTrack_IsReturnedUntouched_WithoutCallingAnyProvider()
    {
        var local = PlaybackQueueTests.T("song");

        var prepared = await _resolver.PrepareAsync(local, preferLocalCopy: false, CancellationToken.None);

        Assert.Same(local, prepared);
        Assert.Equal(0, _provider.SearchCount);
        Assert.Equal(0, _provider.ResolveCount);
    }

    [Fact]
    public async Task RemoteTrack_IsSearchedThenResolved()
    {
        var prepared = await _resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None);

        Assert.Equal(1, _provider.SearchCount);
        Assert.Equal("c1", prepared.SelectedCandidateId);
        Assert.NotNull(prepared.ResolvedStream);
        Assert.Equal("https://audio/c1", prepared.ResolvedStream!.Url);
        Assert.Equal(_clock.UtcNow, prepared.ResolvedStream.ResolvedUtc);
    }

    [Fact]
    public async Task AFailingCandidate_IsMarkedAndTheNextOneIsTried()
    {
        _provider.FailFor.Add("c1");

        var prepared = await _resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None);

        Assert.Equal("c2", prepared.SelectedCandidateId);
        Assert.True(prepared.Candidates!.Single(c => c.Id == "c1").Failed);
        Assert.False(prepared.Candidates!.Single(c => c.Id == "c2").Failed);
    }

    [Fact]
    public async Task WhenEveryCandidateFails_ResolutionThrowsRatherThanReturningNothing()
    {
        _provider.FailFor.UnionWith(new[] { "c1", "c2", "c3" });

        await Assert.ThrowsAsync<StreamResolutionException>(
            () => _resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None));
    }

    [Fact]
    public async Task NoMoreCandidatesAreTriedThanTheRetryBudgetAllows()
    {
        var log = new LogService();
        var settings = Settings(log, "fake", retries: 2);
        var registry = new ProviderRegistry(new IMusicProvider[] { _provider }, settings, log);
        var resolver = new StreamResolver(registry, _clock, log);

        _provider.FailFor.UnionWith(new[] { "c1", "c2", "c3" });

        await Assert.ThrowsAsync<StreamResolutionException>(
            () => resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None));

        Assert.Equal(2, _provider.ResolveCount);
    }

    [Fact]
    public async Task AFreshStreamIsReused_ButAnExpiredOneIsResolvedAgain()
    {
        var first = await _resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None);
        Assert.Equal(1, _provider.ResolveCount);

        var reused = await _resolver.PrepareAsync(first, preferLocalCopy: false, CancellationToken.None);
        Assert.Equal(1, _provider.ResolveCount);
        Assert.Same(first, reused);

        _clock.Advance(TimeSpan.FromHours(2));
        var refreshed = await _resolver.PrepareAsync(reused, preferLocalCopy: false, CancellationToken.None);

        Assert.Equal(2, _provider.ResolveCount);
        Assert.Equal(_clock.UtcNow, refreshed.ResolvedStream!.ResolvedUtc);
    }

    [Fact]
    public async Task AskingForALocalCopy_ReResolvesEvenWhenARemoteStreamIsStillFresh()
    {
        var streamed = await _resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None);
        Assert.Equal(StreamProtocol.Https, streamed.ResolvedStream!.Protocol);

        var downloaded = await _resolver.PrepareAsync(streamed, preferLocalCopy: true, CancellationToken.None);

        Assert.Equal(StreamProtocol.File, downloaded.ResolvedStream!.Protocol);
        Assert.Equal(2, _provider.ResolveCount);
    }

    [Fact]
    public async Task PickingACandidateClearsItsFailureMarkAndTheOldStream()
    {
        _provider.FailFor.Add("c1");
        var prepared = await _resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None);
        Assert.Equal("c2", prepared.SelectedCandidateId);

        _provider.FailFor.Clear();
        var switched = _resolver.SelectCandidate(prepared, "c1");

        Assert.Equal("c1", switched.SelectedCandidateId);
        Assert.Null(switched.ResolvedStream);
        Assert.False(switched.Candidates!.Single(c => c.Id == "c1").Failed);

        var replayed = await _resolver.PrepareAsync(switched, preferLocalCopy: false, CancellationToken.None);
        Assert.Equal("https://audio/c1", replayed.ResolvedStream!.Url);
    }

    [Fact]
    public async Task ASelectedCandidateIsTriedBeforeTheRest()
    {
        var track = await _resolver.EnsureCandidatesAsync(Remote(), CancellationToken.None);
        var picked = _resolver.SelectCandidate(track, "c3");

        var prepared = await _resolver.PrepareAsync(picked, preferLocalCopy: false, CancellationToken.None);

        Assert.Equal("c3", prepared.SelectedCandidateId);
        Assert.Equal(new[] { "c3" }, _provider.Resolved);
    }

    [Fact]
    public async Task WithNoStreamingSourceSwitchedOn_ResolutionSaysSoInsteadOfFailingSilently()
    {
        var log = new LogService();
        var settings = Settings(log, activeStreaming: null);
        var registry = new ProviderRegistry(Array.Empty<IMusicProvider>(), settings, log);
        var resolver = new StreamResolver(registry, _clock, log);

        var error = await Assert.ThrowsAsync<StreamResolutionException>(
            () => resolver.PrepareAsync(Remote(), preferLocalCopy: false, CancellationToken.None));

        Assert.Contains("streaming source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class TestClock : IClock
    {
        private DateTime _now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeStreamingProvider : IStreamingProvider
    {
        public HashSet<string> FailFor { get; } = new();
        public List<string> Resolved { get; } = new();
        public int SearchCount { get; private set; }
        public int ResolveCount => Resolved.Count;

        public ProviderInfo Info { get; } = new(
            "fake", "Fake", "A provider that never touches the network.", "Tests",
            new[] { ProviderKind.Streaming }, new[] { "Streaming" });

        public bool SupportsLocalFiles => false;

        public Task<ProviderState> GetStateAsync(CancellationToken ct) => Task.FromResult(ProviderState.Ready);

        public Task<IReadOnlyList<StreamCandidate>> FindCandidatesAsync(Track track, CancellationToken ct)
        {
            SearchCount++;
            IReadOnlyList<StreamCandidate> candidates = new[] { "c1", "c2", "c3" }
                .Select(id => new StreamCandidate(id, "Take " + id, new ProviderRef("fake", id)))
                .ToList();
            return Task.FromResult(candidates);
        }

        public Task<AudioStream?> ResolveAsync(StreamCandidate candidate, bool preferLocalCopy, CancellationToken ct)
        {
            Resolved.Add(candidate.Id);
            if (FailFor.Contains(candidate.Id)) throw new InvalidOperationException("nope");

            return Task.FromResult<AudioStream?>(preferLocalCopy
                ? new AudioStream(@"C:\cache\" + candidate.Id + ".m4a", StreamProtocol.File, candidate.Source)
                : new AudioStream("https://audio/" + candidate.Id, StreamProtocol.Https, candidate.Source));
        }
    }
}
