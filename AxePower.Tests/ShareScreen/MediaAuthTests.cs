using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// The media server asks the viewer host about every publish and read (threat model R2). The
/// unit tests pin the decision; the live test runs the real MediaMTX and FFmpeg when both are on
/// this PC, with its config in a temp folder so AxePower's own files are never touched.
/// </summary>
public class MediaAuthTests : IDisposable
{
    private readonly RemoteInputService _input = new(new LogService());
    private readonly ShareViewerHost _host;
    private readonly ShareRoom _room = ShareRoomFactory.Create();

    public MediaAuthTests()
    {
        _host = new ShareViewerHost(new LogService(), _input);
        _host.Start(_room, "127.0.0.1", webrtcPort: 18889, preferredPort: 19500);
    }

    public void Dispose()
    {
        _host.Dispose();
        _input.RevokeAll();
    }

    private async Task<string> NonceAsync()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://127.0.0.1:{_host.Port}/{_room.PathName}/nonce");
        return JsonDocument.Parse(json).RootElement.GetProperty("n").GetString()!;
    }

    private async Task<string> WhepQueryAsync(string channel = ShareScreenCrypto.WhepChannel, string? token = null)
    {
        var nonce = await NonceAsync();
        return $"n={nonce}&p={ShareScreenCrypto.ComputeProof(ShareScreenCrypto.DeriveAuthKey(token ?? _room.ViewerToken), channel, nonce)}";
    }

    private byte[] Body(string action, string protocol = "webrtc", string query = "", string user = "", string password = "", string ip = "127.0.0.1", string? path = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { user, password, token = "", ip, action, path = path ?? _room.PathName, protocol, id = "x", query, userAgent = "test" });

    [Fact]
    public async Task Reads_NeedAFreshWhepProof()
    {
        var query = await WhepQueryAsync();
        Assert.True(_host.AuthorizeMedia(Body("read", query: query)));
        Assert.False(_host.AuthorizeMedia(Body("read", query: query)));                 // single use
        Assert.False(_host.AuthorizeMedia(Body("read")));                               // no proof
        Assert.False(_host.AuthorizeMedia(Body("read", query: await WhepQueryAsync("chat"))));   // wrong channel
        Assert.False(_host.AuthorizeMedia(Body("read", query: await WhepQueryAsync(token: "ffffffffffffffffffffffffffffffff"))));
        Assert.False(_host.AuthorizeMedia(Body("read", protocol: "rtsp", query: await WhepQueryAsync())));
        Assert.False(_host.AuthorizeMedia(Body("read", query: await WhepQueryAsync(), path: "axeotherroom")));
    }

    [Fact]
    public void Publishing_NeedsTheEncodersCredentialsFromThisPc()
    {
        Assert.True(_host.AuthorizeMedia(Body("publish", "rtsp", user: "publisher", password: _room.PublisherToken)));
        Assert.False(_host.AuthorizeMedia(Body("publish", "rtsp", user: "publisher", password: "wrong")));
        Assert.False(_host.AuthorizeMedia(Body("publish", "rtsp", user: "someone", password: _room.PublisherToken)));
        Assert.False(_host.AuthorizeMedia(Body("publish", "rtsp", user: "publisher", password: _room.PublisherToken, ip: "192.168.1.40")));
        Assert.False(_host.AuthorizeMedia(Body("playback")));
        Assert.False(_host.AuthorizeMedia("not json"u8.ToArray()));
        Assert.False(_host.AuthorizeMedia("[1]"u8.ToArray()));
    }

    [Fact]
    public async Task AuthEndpoint_AnswersOnlyAtItsSecretPath()
    {
        using var http = new HttpClient();
        var query = await WhepQueryAsync();
        var ok = await http.PostAsync(_host.MediaAuthAddress, new ByteArrayContent(Body("read", query: query)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var replay = await http.PostAsync(_host.MediaAuthAddress, new ByteArrayContent(Body("read", query: query)));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Any other path, or a GET, gets nothing.
        var guess = await http.PostAsync($"http://127.0.0.1:{_host.Port}/mtx-auth/00", new ByteArrayContent(Body("read", query: await WhepQueryAsync())));
        Assert.NotEqual(HttpStatusCode.OK, guess.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync(_host.MediaAuthAddress)).StatusCode);
    }

    [Fact]
    public void GeneratedConfig_HasNoAnonymousReaders()
    {
        var config = MediaMtxServer.BuildConfig(_room, new ShareScreenSettings(), 9997, _host.MediaAuthAddress);
        Assert.Contains("authMethod: http", config);
        Assert.Contains($"authHTTPAddress: {_host.MediaAuthAddress}", config);
        Assert.DoesNotContain("user: any", config);
        Assert.DoesNotContain("authInternalUsers", config);
        Assert.DoesNotContain(_room.PublisherToken, config); // the encoder's password is checked by the host, not written to disk
    }

    // -- Live: the real MediaMTX and FFmpeg --------------------------------------------------

    [Fact]
    public async Task Live_MediaMtx_PublishesForTheEncoder_AndAdmitsOnlyProvenReaders()
    {
        if (!LiveMediaMtx.Available) return; // tools not on this PC: the unit tests above still pin the rules

        var dir = Path.Combine(Path.GetTempPath(), "AxePower_MediaAuthLive_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var live = new LiveMediaMtx();
            // The encoder publishes with the same credentials the app gives FFmpeg.
            await live.StartAsync(dir, _room, (settings, apiPort) => MediaMtxServer.BuildConfig(_room, settings, apiPort, _host.MediaAuthAddress));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpStatusCode> Whep(string query)
            {
                var offer = new StringContent("v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n", Encoding.ASCII, "application/sdp");
                var url = $"http://127.0.0.1:{live.Settings.WebrtcPort}/{_room.PathName}/whep{(query.Length > 0 ? "?" + query : string.Empty)}";
                return (await http.PostAsync(url, offer)).StatusCode;
            }

            Assert.Equal(HttpStatusCode.Unauthorized, await Whep(string.Empty));            // path alone: refused
            var proven = await WhepQueryAsync();
            Assert.NotEqual(HttpStatusCode.Unauthorized, await Whep(proven));              // past auth (the dummy offer is then rejected as SDP)
            Assert.Equal(HttpStatusCode.Unauthorized, await Whep(proven));                 // the same proof again: refused
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
