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
/// When the queue gets extended and when it is left alone. A fake player and a fake discovery
/// source, so nothing here plays audio or reaches the network.
/// </summary>
public sealed class DiscoveryServiceTests : IDisposable
{
    private readonly TestAudio _files = new();
    private readonly FakePlayer _player = new();
    private readonly FakeDiscoveryProvider _provider = new();
    private readonly TestClock _clock = new();
    private readonly SettingsService _settings;
    private readonly DiscoveryService _discovery;

    public DiscoveryServiceTests()
    {
        var log = new LogService();
        _settings = new SettingsService(log, Path.Combine(_files.Folder, "settings"));

        var current = _settings.GetSettings();
        current.Music.Providers.Enabled = new List<string> { "local", "fake" };
        current.Music.Providers.DiscoveryEnabled = true;
        _settings.SaveSettings(current);

        var registry = new ProviderRegistry(new IMusicProvider[] { _provider }, _settings, log);
        _discovery = new DiscoveryService(_player, registry, _settings, log, _clock);
        _discovery.Start();
    }

    public void Dispose()
    {
        _discovery.Dispose();
        _files.Dispose();
    }

    private static Track T(string title) =>
        Track.ForProvider(new ProviderRef("fake", title), title, new[] { "Artist" }, TimeSpan.FromMinutes(3));

    private async Task PublishAsync(int count, int index, RepeatMode repeat = RepeatMode.Off)
    {
        int before = _provider.Calls;
        _player.Publish(Enumerable.Range(0, count).Select(i => T("t" + i)).ToList(), index, repeat);

        // The fetch runs off the state event, so wait for it rather than for a fixed delay.
        // Cases that expect no fetch still have to wait out the full window to prove it.
        await Settle(() => _provider.Calls > before && !_provider.InFlight);
    }

    private static async Task Settle(Func<bool> done)
    {
        for (int i = 0; i < 100 && !done(); i++) await Task.Delay(5);
        await Task.Delay(5);
    }

    [Fact]
    public async Task ReachingTheLastTrackExtendsTheQueue()
    {
        await PublishAsync(count: 3, index: 2);

        Assert.Equal(1, _provider.Calls);
        Assert.Equal(new[] { "rec0", "rec1" }, _player.Added.Select(t => t.Title));
    }

    [Fact]
    public async Task TheMiddleOfAQueueIsLeftAlone()
    {
        await PublishAsync(count: 3, index: 1);

        Assert.Equal(0, _provider.Calls);
        Assert.Empty(_player.Added);
    }

    [Fact]
    public async Task NothingHappensWhileDiscoveryIsOff()
    {
        _discovery.SetEnabled(false);

        await PublishAsync(count: 3, index: 2);

        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task RepeatIsLeftToDoItsOwnJob()
    {
        // Repeat already keeps the queue going, so extending it as well would override a
        // choice the listener made on purpose.
        await PublishAsync(count: 2, index: 1, repeat: RepeatMode.All);
        Assert.Equal(0, _provider.Calls);

        await PublishAsync(count: 2, index: 1, repeat: RepeatMode.One);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task AnEmptyQueueIsNotExtended()
    {
        await PublishAsync(count: 0, index: -1);

        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task RepeatedStateUpdatesDoNotStartASecondSearch()
    {
        // Position ticks republish the state several times a second; each one must not become
        // its own search.
        await PublishAsync(count: 3, index: 2);
        await PublishAsync(count: 5, index: 2);
        await PublishAsync(count: 5, index: 2);

        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task ASearchIsTriedAgainOnceTheCooldownHasPassed()
    {
        _provider.Recommendations = Array.Empty<Track>();
        await PublishAsync(count: 3, index: 2);
        Assert.Equal(1, _provider.Calls);

        await PublishAsync(count: 3, index: 2);
        Assert.Equal(1, _provider.Calls);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await PublishAsync(count: 3, index: 2);

        Assert.Equal(2, _provider.Calls);
    }

    [Fact]
    public async Task OnlyTheRecentTracksAreSentAsContext()
    {
        await PublishAsync(count: 25, index: 24);

        Assert.Equal(10, _provider.LastContext.Count);
        Assert.Equal("t24", _provider.LastContext.Last().Title);
    }

    [Fact]
    public async Task ATrackThatIsAlreadyQueuedIsNotAddedAgain()
    {
        _provider.Recommendations = new[] { T("t1"), T("rec-new") };

        await PublishAsync(count: 3, index: 2);

        Assert.Equal(new[] { "rec-new" }, _player.Added.Select(t => t.Title));
    }

    [Fact]
    public async Task ASourceThatFailsDoesNotBringDownThePlayer()
    {
        _provider.Throw = true;

        await PublishAsync(count: 3, index: 2);

        Assert.Empty(_player.Added);
    }

    [Fact]
    public async Task AVeryLongQueueIsNotFedAnyFurther()
    {
        await PublishAsync(count: 500, index: 499);

        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public void TheToggleIsRememberedInSettings()
    {
        _discovery.SetEnabled(false);
        Assert.False(_settings.GetSettings().Music.Providers.DiscoveryEnabled);
        Assert.False(_discovery.IsEnabled);

        _discovery.SetEnabled(true);
        Assert.True(_settings.GetSettings().Music.Providers.DiscoveryEnabled);
        Assert.True(_discovery.IsEnabled);
    }

    [Fact]
    public void DiscoveryReportsItselfUnavailableWithNoSourceForIt()
    {
        var log = new LogService();
        var settings = new SettingsService(log, Path.Combine(_files.Folder, "empty"));
        var registry = new ProviderRegistry(Array.Empty<IMusicProvider>(), settings, log);
        using var discovery = new DiscoveryService(_player, registry, settings, log, _clock);

        Assert.False(discovery.IsAvailable);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class TestClock : IClock
    {
        private DateTime _now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        public DateTime UtcNow => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeDiscoveryProvider : IDiscoveryProvider
    {
        public int Calls { get; private set; }
        public bool InFlight { get; private set; }
        public bool Throw { get; set; }
        public IReadOnlyList<Track> LastContext { get; private set; } = Array.Empty<Track>();

        public IReadOnlyList<Track> Recommendations { get; set; } = new[]
        {
            Track.ForProvider(new ProviderRef("fake", "rec0"), "rec0", new[] { "A" }, TimeSpan.FromMinutes(3)),
            Track.ForProvider(new ProviderRef("fake", "rec1"), "rec1", new[] { "B" }, TimeSpan.FromMinutes(3))
        };

        public ProviderInfo Info { get; } = new(
            "fake", "Fake", "Recommends without a network.", "Tests",
            new[] { ProviderKind.Discovery }, new[] { "Recommendations" });

        public Task<ProviderState> GetStateAsync(CancellationToken ct) => Task.FromResult(ProviderState.Ready);

        public Task<IReadOnlyList<Track>> GetRecommendationsAsync(
            IReadOnlyList<Track> context, DiscoveryOptions options, CancellationToken ct)
        {
            Calls++;
            InFlight = true;
            LastContext = context;
            try
            {
                if (Throw) throw new InvalidOperationException("no");
                return Task.FromResult(Recommendations);
            }
            finally { InFlight = false; }
        }
    }

    private sealed class FakePlayer : IMusicPlayerService
    {
        public List<Track> Added { get; } = new();
        public PlayerState State { get; private set; } = PlayerState.Empty;

        public event EventHandler<PlayerState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;

        public void Publish(IReadOnlyList<Track> tracks, int index, RepeatMode repeat)
        {
            var items = tracks.Select(QueueItem.For).ToArray();
            State = PlayerState.Empty with { Queue = items, Index = index, Repeat = repeat, Playing = true };
            StateChanged?.Invoke(this, State);
        }

        public void AddToQueue(IEnumerable<Track> tracks)
        {
            var added = tracks.ToList();
            Added.AddRange(added);

            var items = State.Queue.Concat(added.Select(QueueItem.For)).ToArray();
            State = State with { Queue = items };
        }

        public void Unused() => PositionChanged?.Invoke(this, TimeSpan.Zero);

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task OpenAsync(IReadOnlyList<Track> tracks, int startIndex = 0, bool play = true, bool shuffle = false, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenPathsAsync(IEnumerable<string> filesOrFolders, bool play = true, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddPathsAsync(IEnumerable<string> filesOrFolders, CancellationToken ct = default) => Task.CompletedTask;
        public Task PlayAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task TogglePlayPauseAsync() => Task.CompletedTask;
        public Task NextAsync() => Task.CompletedTask;
        public Task PreviousAsync() => Task.CompletedTask;
        public Task JumpAsync(int index) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public void SetVolume(double volume) { }
        public void ToggleMute() { }
        public void SetShuffle(bool on) { }
        public void SetRepeat(RepeatMode mode) { }
        public void SetSpeed(double speed) { }
        public void SetPitch(double semitones) { }
        public void PlayNext(IEnumerable<Track> tracks) { }
        public void Insert(int index, Track track) { }
        public void Move(int from, int to) { }
        public void Remove(int index) { }
        public void ClearQueue() { }
        public void SetMix(bool on) { }
        public Task SelectCandidateAsync(int index, string candidateId) => Task.CompletedTask;
        public Task FadeOutAndPauseAsync(TimeSpan duration) => Task.CompletedTask;
        public Task DuckAsync(double level, TimeSpan fade) => Task.CompletedTask;
        public Task UnduckAsync(TimeSpan fade) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
