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

/// <summary>Player logic against a fake engine: no audio device, no real settings or state files.</summary>
public sealed class MusicPlayerServiceTests : IDisposable
{
    private readonly TestAudio _audio = new();
    private readonly FakeEngine _engine = new();
    private readonly List<string> _notices = new();
    private readonly MusicPlayerService _player;
    private readonly PlaybackStateStore _store;

    public MusicPlayerServiceTests()
    {
        var log = new LogService();
        var settings = new SettingsService(log);
        var notifications = new NotificationService();
        notifications.OnShowNotification += (_, message) => _notices.Add(message);

        _store = new PlaybackStateStore(new MusicStoragePaths(_audio.Folder));
        var reader = new LooseTrackReader(new FakeTagReader());
        _player = new MusicPlayerService(_engine, _store, reader, settings, notifications, log, new FakeClock(), new PlaybackQueue(new Random(3)));
    }

    public void Dispose()
    {
        _player.Dispose();
        _audio.Dispose();
    }

    private static Track T(string name) => PlaybackQueueTests.T(name);

    [Fact]
    public async Task Open_LoadsFirstTrack_Plays_AndPreloadsTheNext()
    {
        await _player.OpenAsync(new[] { T("a"), T("b"), T("c") });

        var state = _player.State;
        Assert.True(state.Playing);
        Assert.Equal(0, state.Index);
        Assert.Equal(new[] { "load:a@0:play", "next:b" }, _engine.Calls.Where(c => c.StartsWith("load") || c.StartsWith("next")));
    }

    [Fact]
    public async Task GaplessBoundary_AdvancesIndex_AndPreloadsTheFollowingTrack()
    {
        await _player.OpenAsync(new[] { T("a"), T("b"), T("c") });
        var keyB = _player.State.Queue[1].Key;
        _engine.Calls.Clear();

        _engine.RaiseTrackStarted(keyB);
        await _player.WhenIdleAsync();

        Assert.Equal(1, _player.State.Index);
        Assert.Equal(TimeSpan.Zero, _player.State.Position);
        Assert.Contains("next:c", _engine.Calls);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("load")); // no reload: the engine already joined it
    }

    [Fact]
    public async Task TrackEnded_OnLastTrack_StopsCompleted_OnTheLastTrackAtZero()
    {
        await _player.OpenAsync(new[] { T("a"), T("b") }, startIndex: 1);
        var last = _player.State.Current!.Key;
        _engine.Calls.Clear();

        _engine.RaiseTrackEnded(last);
        await _player.WhenIdleAsync();

        var state = _player.State;
        Assert.True(state.Completed);
        Assert.False(state.Playing);
        Assert.Equal(1, state.Index);
        Assert.Contains("load:b@0:pause", _engine.Calls);
    }

    [Fact]
    public async Task ThreeFailuresInARow_PausesWithAnError()
    {
        _engine.FailPaths.UnionWith(new[] { "a", "b", "c" });

        await _player.OpenAsync(new[] { T("a"), T("b"), T("c"), T("d") });

        var state = _player.State;
        Assert.False(state.Playing);
        Assert.NotNull(state.Error);
        Assert.Equal(3, _notices.Count(n => n.StartsWith("Couldn't play")));
        Assert.Contains("Couldn't play 'a' — skipped", _notices);
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("load:d")); // stopped before racing on
    }

    [Fact]
    public async Task OneFailure_SkipsToTheNextTrack()
    {
        _engine.FailPaths.Add("a");

        await _player.OpenAsync(new[] { T("a"), T("b") });

        Assert.Equal(1, _player.State.Index);
        Assert.True(_player.State.Playing);
        Assert.Null(_player.State.Error);
    }

    [Fact]
    public async Task Previous_MoreThanThreeSecondsIn_SeeksToStart()
    {
        await _player.OpenAsync(new[] { T("a"), T("b") }, startIndex: 1);
        _engine.Position = TimeSpan.FromSeconds(10);
        _engine.Calls.Clear();

        await _player.PreviousAsync();

        Assert.Equal(1, _player.State.Index);
        Assert.Contains("seek:0", _engine.Calls);
    }

    [Fact]
    public async Task StateChanged_IsRaisedOncePerChange()
    {
        await _player.OpenAsync(new[] { T("a"), T("b") });
        int raised = 0;
        _player.StateChanged += (_, _) => raised++;

        _player.SetRepeat(RepeatMode.All);
        await _player.WhenIdleAsync();
        Assert.Equal(1, raised);

        _player.SetShuffle(true);
        await _player.WhenIdleAsync();
        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task RepeatOne_PreloadsAReplayOfTheCurrentTrack_AndItMapsBack()
    {
        await _player.OpenAsync(new[] { T("a"), T("b") });
        _player.SetRepeat(RepeatMode.One);
        await _player.WhenIdleAsync();

        var replay = _engine.LastNext!;
        Assert.Equal("a", replay.Track.Title);
        Assert.NotEqual(_player.State.Current!.Key, replay.Key);

        _engine.RaiseTrackStarted(replay.Key);
        await _player.WhenIdleAsync();
        Assert.Equal(0, _player.State.Index);
    }

    [Fact]
    public async Task VolumeAboveOne_IsCappedWithoutBoost()
    {
        await _player.OpenAsync(new[] { T("a") });
        _player.SetVolume(1.8);
        await _player.WhenIdleAsync();

        Assert.Equal(1.0, _player.State.Volume);
        Assert.Equal(1.0, _engine.Volume);
    }

    [Fact]
    public async Task Initialize_RestoresTheSavedSessionPaused_DroppingMissingFiles()
    {
        string a = _audio.Wav("a.wav");
        string b = _audio.Wav("b.wav");
        _store.Save(new PersistedPlaybackState
        {
            Queue = new List<PersistedQueueEntry>
            {
                new() { Path = a },
                new() { Path = Path.Combine(_audio.Folder, "gone.wav") },
                new() { Path = b }
            },
            Index = 2,
            PositionMs = 400,
            Volume = 0.5,
            Repeat = RepeatMode.All
        });

        await _player.InitializeAsync(CancellationToken.None);

        var state = _player.State;
        Assert.Equal(2, state.Queue.Count);
        Assert.Equal(1, state.Index);
        Assert.Equal("b", state.Current!.Track.Title);
        Assert.False(state.Playing);                         // restored paused by default
        Assert.Equal(0.5, state.Volume);
        Assert.Equal(RepeatMode.All, state.Repeat);
        Assert.Contains("load:b@400:pause", _engine.Calls);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Titles come from the file name, so tests can name their tracks by path.</summary>
    private sealed class FakeTagReader : ITagReader
    {
        public TrackTags Read(string path) => new(Path.GetFileNameWithoutExtension(path), "Artist", null, null, null, null, null, null,
            TimeSpan.FromMinutes(3), new AudioFormatInfo("WAV", 44100, 16, 2, 1411, true), null, false, false);
        public byte[]? ReadEmbeddedCover(string path) => null;
        public string? ReadEmbeddedLyrics(string path) => null;
    }

    private sealed class FakeEngine : IAudioEngine
    {
        public List<string> Calls { get; } = new();
        public HashSet<string> FailPaths { get; } = new();
        public QueueItem? LastNext { get; private set; }
        public double Volume { get; private set; } = 1.0;

        public event EventHandler<Guid>? TrackStarted;
        public event EventHandler<Guid>? TrackEnded;
        public event EventHandler<EngineError>? Error;
        public event EventHandler<OutputInfo>? OutputChanged;
        public event EventHandler? DeviceLost;

        public void RaiseTrackStarted(Guid key) => TrackStarted?.Invoke(this, key);
        public void RaiseTrackEnded(Guid key) => TrackEnded?.Invoke(this, key);
        public void RaiseError(Guid key) => Error?.Invoke(this, new EngineError(key, "boom", null));
        public void RaiseOutput(OutputInfo info) => OutputChanged?.Invoke(this, info);
        public void RaiseDeviceLost() => DeviceLost?.Invoke(this, EventArgs.Empty);

        public Task LoadAsync(QueueItem item, TimeSpan startAt, bool play, CancellationToken ct)
        {
            Calls.Add($"load:{item.Track.Title}@{startAt.TotalMilliseconds:0}:{(play ? "play" : "pause")}");
            if (FailPaths.Contains(item.Track.Title)) throw new UnsupportedFormatException(".bad");
            IsPlaying = play;
            Duration = item.Track.Duration;
            Position = startAt;
            return Task.CompletedTask;
        }

        public void SetNext(QueueItem? next)
        {
            LastNext = next;
            Calls.Add($"next:{next?.Track.Title ?? "none"}");
        }

        public void Play() { Calls.Add("play"); IsPlaying = true; }
        public void Pause() { Calls.Add("pause"); IsPlaying = false; }
        public Task StopAsync() { Calls.Add("stop"); IsPlaying = false; return Task.CompletedTask; }

        public Task SeekAsync(TimeSpan position, CancellationToken ct)
        {
            Calls.Add($"seek:{position.TotalMilliseconds:0}");
            Position = position;
            return Task.CompletedTask;
        }

        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; private set; }
        public bool IsPlaying { get; private set; }
        public void SetVolume(double volume, bool muted) => Volume = volume;
        public Task FadeToAsync(double gain, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
        public void SetDsp(DspSettings dsp) { }
        public Task ConfigureOutputAsync(OutputSettings output, CancellationToken ct) => Task.CompletedTask;
        public float[]? GetSpectrum() => null;
        public bool SpectrumEnabled { get; set; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
