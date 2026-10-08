using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// The phone remote over a real loopback socket: what the portal refuses without a token, what
/// the music routes return, and that the event stream pushes state.
///
/// The server binds 127.0.0.1 on a high port, and every test stops it again.
/// </summary>
public sealed class MusicRemoteApiTests : IDisposable
{
    private const int Port = 5077;

    private readonly FakePlayer _player = new();
    private readonly PortalAccessToken _token = new(new InMemorySecretStore());
    private readonly LanDropService _portal;
    private readonly MusicRemote _remote;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public MusicRemoteApiTests()
    {
        var log = new LogService();
        _remote = new MusicRemote(_player, log);
        _remote.Start();

        _portal = new LanDropService(log, _token, _remote, downloadDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxePower_LanDropTests"));
        _portal.StartServerAsync("127.0.0.1", Port).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _portal.StopServer();
        _http.Dispose();
    }

    private string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    private Task<HttpResponseMessage> GetAsync(string path, bool authorised = true) =>
        _http.GetAsync(Url(path + (authorised ? (path.Contains('?') ? "&" : "?") + "t=" + _token.Value : string.Empty)));

    private Task<HttpResponseMessage> PostAsync(string path, bool authorised = true) =>
        _http.PostAsync(Url(path + (authorised ? (path.Contains('?') ? "&" : "?") + "t=" + _token.Value : string.Empty)),
            new StringContent(string.Empty));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ── Access control ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/music/state")]
    [InlineData("/")]
    public async Task EveryRouteIsRefusedWithoutTheToken(string path)
    {
        var response = await GetAsync(path, authorised: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWrongTokenIsRefusedJustLikeAMissingOne()
    {
        var response = await _http.GetAsync(Url("/api/music/state?t=not-the-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheRemoteThatCanLockThePcIsBehindTheTokenToo()
    {
        // Before the token this route was reachable by any page the user had open, and it can
        // lock the workstation.
        var response = await _http.PostAsync(Url("/api/remote"), new StringContent("lock"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheTokenIsAcceptedInAHeaderSoItNeedNotSitInTheUrl()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/music/state"));
        request.Headers.Add(PortalAccessToken.HeaderName, _token.Value);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ResponsesDoNotGrantAnyOtherOriginAccess()
    {
        var response = await GetAsync("/api/music/state");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ── State ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task StateDescribesWhatIsPlaying()
    {
        _player.Publish(new[] { "One", "Two", "Three" }, index: 1, playing: true);

        var json = await JsonAsync(await GetAsync("/api/music/state"));

        Assert.True(json.GetProperty("playing").GetBoolean());
        Assert.Equal("Two", json.GetProperty("title").GetString());
        Assert.Equal("Artist", json.GetProperty("artist").GetString());
        Assert.Equal(1, json.GetProperty("index").GetInt32());
        Assert.Equal(3, json.GetProperty("queueLength").GetInt32());
        Assert.Equal(3, json.GetProperty("queue").GetArrayLength());
        Assert.True(json.GetProperty("queue")[1].GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task AVeryLongQueueIsTrimmedBeforeItIsSent()
    {
        _player.Publish(Enumerable.Range(0, 400).Select(i => "t" + i).ToArray(), index: 0, playing: false);

        var json = await JsonAsync(await GetAsync("/api/music/state"));

        Assert.Equal(400, json.GetProperty("queueLength").GetInt32());
        Assert.Equal(200, json.GetProperty("queue").GetArrayLength());
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CommandsReachThePlayerAndReturnTheNewState()
    {
        _player.Publish(new[] { "One", "Two" }, index: 0, playing: false);

        var response = await PostAsync("/api/music/command?action=toggle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("toggle", _player.Calls);
        Assert.Equal("title", (await JsonAsync(response)).EnumerateObject().Select(p => p.Name).First(n => n == "title"));
    }

    [Theory]
    [InlineData("seek", "42", "seek:42")]
    [InlineData("volume", "30", "volume:0.3")]
    [InlineData("repeat", "one", "repeat:One")]
    [InlineData("jump", "1", "jump:1")]
    public async Task CommandsWithAValueArePassedThrough(string action, string value, string expected)
    {
        _player.Publish(new[] { "One", "Two" }, index: 0, playing: false);

        var response = await PostAsync($"/api/music/command?action={action}&value={value}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, _player.Calls);
    }

    [Theory]
    [InlineData("action=nonsense")]
    [InlineData("action=seek&value=-5")]
    [InlineData("action=seek&value=abc")]
    [InlineData("action=volume&value=900")]
    [InlineData("action=repeat&value=sideways")]
    [InlineData("action=jump&value=99")]
    public async Task ACommandThatCannotBeCarriedOutIsRejected(string query)
    {
        _player.Publish(new[] { "One", "Two" }, index: 0, playing: false);
        _player.Calls.Clear();

        var response = await PostAsync("/api/music/command?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_player.Calls);
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheEventStreamPushesStateWhenThePlayerChanges()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/music/events?t=" + _token.Value));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new System.IO.StreamReader(stream);

        // The change has to come after the stream is open, or its notification is missed.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200, cts.Token);
            _player.Publish(new[] { "Pushed" }, index: 0, playing: true);
        }, cts.Token);

        // A client gets the state as it is the moment it connects, then every change after
        // that, so a phone opening the page shows what is playing without asking for it.
        var payloads = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            payloads.Add(line[6..]);
            if (line.Contains("Pushed", StringComparison.Ordinal)) break;
        }

        Assert.True(payloads.Count >= 2, "expected the opening snapshot and then the change");
        Assert.DoesNotContain("Pushed", payloads[0]);
        Assert.Contains("Pushed", payloads[^1]);
    }

    [Fact]
    public async Task TheEventStreamIsRefusedWithoutTheToken()
    {
        var response = await GetAsync("/api/music/events", authorised: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private sealed class FakePlayer : IMusicPlayerService
    {
        public List<string> Calls { get; } = new();
        public PlayerState State { get; private set; } = PlayerState.Empty;

        public event EventHandler<PlayerState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged;

        public void Publish(IReadOnlyList<string> titles, int index, bool playing)
        {
            var items = titles
                .Select(t => QueueItem.For(Track.ForProvider(
                    new ProviderRef("fake", t), t, new[] { "Artist" }, TimeSpan.FromMinutes(3))))
                .ToArray();

            State = PlayerState.Empty with
            {
                Queue = items,
                Index = index,
                Playing = playing,
                Duration = TimeSpan.FromMinutes(3)
            };
            StateChanged?.Invoke(this, State);
        }

        public Task SeekAsync(TimeSpan position) { Calls.Add($"seek:{position.TotalSeconds:0}"); return Task.CompletedTask; }
        public void SetVolume(double volume) => Calls.Add($"volume:{volume:0.##}");
        public void SetRepeat(RepeatMode mode) => Calls.Add($"repeat:{mode}");
        public Task JumpAsync(int index) { Calls.Add($"jump:{index}"); return Task.CompletedTask; }
        public Task TogglePlayPauseAsync() { Calls.Add("toggle"); return Task.CompletedTask; }
        public Task PlayAsync() { Calls.Add("play"); return Task.CompletedTask; }
        public Task PauseAsync() { Calls.Add("pause"); return Task.CompletedTask; }
        public Task NextAsync() { Calls.Add("next"); return Task.CompletedTask; }
        public Task PreviousAsync() { Calls.Add("previous"); return Task.CompletedTask; }
        public void ToggleMute() => Calls.Add("mute");
        public void SetShuffle(bool on) => Calls.Add($"shuffle:{on}");

        public void Unused() => PositionChanged?.Invoke(this, TimeSpan.Zero);

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task OpenAsync(IReadOnlyList<Track> tracks, int startIndex = 0, bool play = true, bool shuffle = false, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenPathsAsync(IEnumerable<string> filesOrFolders, bool play = true, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddPathsAsync(IEnumerable<string> filesOrFolders, CancellationToken ct = default) => Task.CompletedTask;
        public void SetSpeed(double speed) { }
        public void SetPitch(double semitones) { }
        public void PlayNext(IEnumerable<Track> tracks) { }
        public void AddToQueue(IEnumerable<Track> tracks) { }
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
