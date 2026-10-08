using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// The pieces LanDropService was split into, each tested on its own: storage from in-memory
/// streams, HTTP over memory, the authenticator and client registry without a server, and the
/// endpoints against a fake host. No sockets, no real media keys, nothing outside a temp folder.
/// </summary>
public sealed class LanDropComponentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_LanDropParts_" + Guid.NewGuid().ToString("N"));
    private readonly LanDropSecurityLimits _limits = new() { MaxFileSizeBytes = 1024, ReadTimeout = TimeSpan.FromSeconds(2), MinFreeDiskSpaceBytes = 100 };

    public LanDropComponentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Reads from a prepared request, records whatever is written back.</summary>
    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _input;
        public MemoryStream Output { get; } = new();
        public DuplexStream(byte[] input) => _input = new MemoryStream(input);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public string Response => Encoding.UTF8.GetString(Output.ToArray());
    }

    // -- Storage -------------------------------------------------------------------------

    [Fact]
    public async Task Storage_RawUpload_AppearsOnlyWhenComplete()
    {
        var storage = new LanDropStorage(_dir, _limits, _ => long.MaxValue);

        var item = await storage.ReceiveRawAsync(new MemoryStream("hello"u8.ToArray()), "note.txt", 5, "Android", CancellationToken.None);
        Assert.Equal("hello", File.ReadAllText(item.FilePath));
        Assert.Equal("Android Phone", item.DeviceName);
        Assert.Single(storage.GetReceived());

        // Too short: the sender promised 10 bytes and sent 3.
        await Assert.ThrowsAsync<IOException>(() => storage.ReceiveRawAsync(new MemoryStream("abc"u8.ToArray()), "short.txt", 10, "", CancellationToken.None));
        // Too long for the limit.
        await Assert.ThrowsAsync<IOException>(() => storage.ReceiveRawAsync(new MemoryStream(new byte[2000]), "big.bin", 2000, "", CancellationToken.None));

        Assert.Equal(new[] { "note.txt" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Storage_Multipart_And_SharedDownloads()
    {
        var storage = new LanDropStorage(_dir, _limits, _ => long.MaxValue);
        var body = "--B\r\nContent-Disposition: form-data; name=\"f\"; filename=\"a.txt\"\r\n\r\nAAA\r\n--B--\r\n"u8.ToArray();
        var received = await storage.ReceiveMultipartAsync(new MemoryStream(body), "B", "iPhone", CancellationToken.None);
        Assert.Equal("a.txt", Assert.Single(received).FileName);

        Assert.Null(storage.AddShared(Path.Combine(_dir, "missing.bin")));
        var shared = storage.AddShared(received[0].FilePath)!;
        Assert.Same(shared, storage.FindDownload(shared.Id));
        Assert.Null(storage.FindDownload("nope"));

        var copy = new MemoryStream();
        await LanDropStorage.CopyToAsync(shared, copy, CancellationToken.None);
        Assert.Equal("AAA", Encoding.UTF8.GetString(copy.ToArray()));

        storage.RemoveShared(shared.Id);
        Assert.Empty(storage.GetShared());
    }

    [Fact]
    public void Storage_HasRoomFor_KeepsTheHeadroom()
    {
        long free = 1000;
        var storage = new LanDropStorage(_dir, _limits, _ => free);
        Assert.True(storage.HasRoomFor(900));
        Assert.False(storage.HasRoomFor(901));    // would leave less than the 100-byte headroom
        free = 50;
        Assert.False(storage.HasRoomFor(0));      // already below the headroom
    }

    // -- HTTP over memory ----------------------------------------------------------------

    [Fact]
    public async Task Http_ReadsAHeadAndKeepsTheBodyBytesThatCameWithIt()
    {
        var bytes = "POST /api/text?t=abc&x=1 HTTP/1.1\r\nUser-Agent: Phone\r\nContent-Length: 5\r\n\r\nhello"u8.ToArray();
        var (outcome, request) = await LanDropHttp.ReadRequestAsync(new MemoryStream(bytes), _limits, CancellationToken.None);

        Assert.Equal(LanDropHttp.ReadOutcome.Ok, outcome);
        Assert.Equal("POST", request!.Method);
        Assert.Equal("/api/text", request.Path);
        Assert.Equal("abc", request.Query["t"]);
        Assert.Equal("Phone", request.UserAgent);
        Assert.Equal(5, request.ContentLength);
        Assert.Equal("hello", Encoding.UTF8.GetString(request.InitialBody));
    }

    [Fact]
    public async Task Http_RefusesOversizedAndContradictoryHeads()
    {
        var huge = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 20_000) + "\r\n\r\n");
        Assert.Equal(LanDropHttp.ReadOutcome.HeadTooLarge, (await LanDropHttp.ReadRequestAsync(new MemoryStream(huge), _limits, CancellationToken.None)).Outcome);

        var twoLengths = "POST / HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n"u8.ToArray();
        Assert.Equal(LanDropHttp.ReadOutcome.ConflictingContentLength, (await LanDropHttp.ReadRequestAsync(new MemoryStream(twoLengths), _limits, CancellationToken.None)).Outcome);

        var negative = "POST / HTTP/1.1\r\nContent-Length: -1\r\n\r\n"u8.ToArray();
        Assert.Equal(LanDropHttp.ReadOutcome.MalformedContentLength, (await LanDropHttp.ReadRequestAsync(new MemoryStream(negative), _limits, CancellationToken.None)).Outcome);

        Assert.Equal(LanDropHttp.ReadOutcome.Closed, (await LanDropHttp.ReadRequestAsync(new MemoryStream(), _limits, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Http_WritesTheSameResponseHeadEveryRouteUses()
    {
        var output = new MemoryStream();
        await LanDropHttp.SendAsync(output, 429, "Too Many Requests", "application/json", "{}"u8.ToArray(), retryAfterSeconds: 7, extraHeaders: "Set-Cookie: a=b");
        Assert.Equal(
            "HTTP/1.1 429 Too Many Requests\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store\r\n" +
            "Set-Cookie: a=b\r\nRetry-After: 7\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}",
            Encoding.ASCII.GetString(output.ToArray()));
    }

    // -- Authentication and clients --------------------------------------------------------

    [Fact]
    public void Authenticator_AcceptsHeaderCookieOrQuery_AndLocksOutGuessers()
    {
        var token = new PortalAccessToken();
        token.StartNewSession();
        var auth = new LanDropAuthenticator(token, new LanDropSecurityLimits { MaxFailedAuthAttempts = 3 });
        var ip = IPAddress.Parse("192.168.1.40");

        LanDropRequest Request(string query = "", params (string, string)[] headers) => new()
        {
            Method = "GET", Path = "/", QueryString = query, HasContentLength = false, ContentLength = -1, InitialBody = Array.Empty<byte>(),
            Headers = headers.ToDictionary(h => h.Item1, h => h.Item2, StringComparer.OrdinalIgnoreCase)
        };

        Assert.True(auth.Authorise(Request($"t={token.Value}"), ip));
        Assert.True(auth.Authorise(Request(headers: (PortalAccessToken.HeaderName, token.Value)), ip));
        Assert.True(auth.Authorise(Request(headers: ("Cookie", $"x=1; {LanDropAuthenticator.SessionCookieName}={token.Value}")), ip));

        for (var i = 0; i < 3; i++) Assert.False(auth.Authorise(Request("t=guess"), ip));
        Assert.True(auth.IsLockedOut(ip, out _));

        auth.Bar("192.168.1.41");
        Assert.True(auth.IsBarred(IPAddress.Parse("192.168.1.41")));
        auth.Reset();
        Assert.False(auth.IsBarred(IPAddress.Parse("192.168.1.41")));
        Assert.False(auth.IsLockedOut(ip, out _));
        Assert.StartsWith($"Set-Cookie: axe_session={token.Value}; Path=/; HttpOnly; SameSite=Strict", auth.SessionCookieHeader());
    }

    [Fact]
    public void ClientRegistry_TracksRemovesAndEndsDevices()
    {
        var clients = new LanDropClientRegistry(TimeSpan.FromMinutes(30));
        var first = clients.Track("10.0.0.2", "Mozilla (iPhone)", out var added);
        Assert.True(added);
        Assert.Equal("Apple iPhone", first.DeviceName);
        clients.Track("10.0.0.2", "", out added);
        Assert.False(added);
        Assert.Equal("Apple iPhone", clients.Snapshot().Single().DeviceName); // an empty agent keeps the label

        var token = first.SessionCts.Token;
        Assert.Same(first, clients.Remove(first.Id));                 // by session id, as the UI does
        Assert.True(token.IsCancellationRequested);
        Assert.Null(clients.Remove("10.0.0.2"));

        var second = clients.Track("10.0.0.3", "Android", out _);
        clients.EndAll();
        Assert.True(second.SessionCts.IsCancellationRequested);
        Assert.Empty(clients.Snapshot());
    }

    // -- Domain rules -------------------------------------------------------------------------

    [Theory]
    [InlineData("play-pause", RemoteMediaAction.PlayPause)]
    [InlineData(" NEXT ", RemoteMediaAction.Next)]
    [InlineData("prev", RemoteMediaAction.Previous)]
    [InlineData("vol-up", RemoteMediaAction.VolumeUp)]
    [InlineData("vol-down", RemoteMediaAction.VolumeDown)]
    [InlineData("mute", RemoteMediaAction.Mute)]
    [InlineData("lock", RemoteMediaAction.Lock)]
    public void RemoteActions_AreParsedFromTheButtonName(string body, RemoteMediaAction expected) =>
        Assert.Equal(expected, RemoteMediaActionParser.Parse(body));

    [Fact]
    public void RemoteActions_UnknownNamesParseToNothing()
    {
        Assert.Null(RemoteMediaActionParser.Parse("shutdown"));
        Assert.Null(RemoteMediaActionParser.Parse(""));
    }

    [Fact]
    public void DeviceLabels_And_ClipboardCap_AreUnchanged()
    {
        Assert.Equal("Apple iPad", LanDropDeviceNames.ForSession("iPad"));
        Assert.Equal("iPad", LanDropDeviceNames.ForReceivedFile("iPad"));
        Assert.Equal("Mobile Client", LanDropDeviceNames.ForTextMessage("iPad"));

        var clipboard = new LanDropClipboard(max: 2);
        foreach (var t in new[] { "one", "two", "three" }) clipboard.Add(new LanDropTextMessage { Content = t });
        Assert.Equal("three", clipboard.Latest);
        Assert.Equal(new[] { "three", "two" }, clipboard.GetAll().Select(m => m.Content));
    }

    // -- Endpoints against a fake host ----------------------------------------------------------

    private sealed class FakeHost : ILanDropHost
    {
        public string CurrentIpAddress => "192.168.1.5";
        public int CurrentPort => 5050;
        public string PortalUrl => "http://192.168.1.5:5050/?t=x";
        public List<LanDropFileItem> Files { get; } = new();
        public List<LanDropTextMessage> Texts { get; } = new();
        public void FileReceived(LanDropFileItem item) => Files.Add(item);
        public void TextReceived(LanDropTextMessage message) => Texts.Add(message);
    }

    private sealed class RecordingRemote : IRemoteMediaActions
    {
        public List<RemoteMediaAction> Performed { get; } = new();
        public void Perform(RemoteMediaAction action) => Performed.Add(action);
    }

    private (LanDropEndpoints Endpoints, FakeHost Host, RecordingRemote Remote, LanDropStorage Storage) Endpoints()
    {
        var host = new FakeHost();
        var remote = new RecordingRemote();
        var storage = new LanDropStorage(_dir, _limits, _ => long.MaxValue);
        var token = new PortalAccessToken();
        token.StartNewSession();
        var endpoints = new LanDropEndpoints(host, storage, new LanDropClipboard(10), remote, new LanDropAuthenticator(token, _limits), _limits, new LogService());
        return (endpoints, host, remote, storage);
    }

    private static LanDropRequest Post(string path, string body, params (string, string)[] headers)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new LanDropRequest
        {
            Method = "POST", Path = path, QueryString = "", HasContentLength = true, ContentLength = bytes.Length, InitialBody = bytes,
            Headers = headers.ToDictionary(h => h.Item1, h => h.Item2, StringComparer.OrdinalIgnoreCase)
        };
    }

    [Fact]
    public async Task Endpoints_TextAndRemote_ReachTheHostAndTheActions()
    {
        var (endpoints, host, remote, _) = Endpoints();

        var text = new DuplexStream(Array.Empty<byte>());
        await endpoints.TextAsync(text, Post("/api/text", " hi ", ("User-Agent", "iPhone")), CancellationToken.None);
        Assert.EndsWith("{\"success\":true}", text.Response);
        Assert.Equal("hi", Assert.Single(host.Texts).Content);
        Assert.Equal("iPhone", host.Texts[0].SenderDevice);

        var press = new DuplexStream(Array.Empty<byte>());
        await endpoints.RemoteAsync(press, Post("/api/remote", "mute"), CancellationToken.None);
        Assert.Equal(new[] { RemoteMediaAction.Mute }, remote.Performed);
    }

    [Fact]
    public async Task Endpoints_RawUpload_IsStoredAndAnnounced()
    {
        var (endpoints, host, _, storage) = Endpoints();
        var stream = new DuplexStream(Array.Empty<byte>());
        await endpoints.UploadAsync(stream, Post("/api/upload", "payload", ("Content-Type", "application/octet-stream"), ("X-File-Name", "doc.txt")), CancellationToken.None);

        Assert.StartsWith("HTTP/1.1 200 OK", stream.Response);
        Assert.Equal("doc.txt", Assert.Single(host.Files).FileName);
        Assert.Equal("payload", File.ReadAllText(storage.GetReceived().Single().FilePath));

        var unsafeName = new DuplexStream(Array.Empty<byte>());
        await endpoints.UploadAsync(unsafeName, Post("/api/upload", "x", ("Content-Type", "application/octet-stream"), ("X-File-Name", "../evil.txt")), CancellationToken.None);
        Assert.StartsWith("HTTP/1.1 400", unsafeName.Response);
        Assert.Single(host.Files);
    }

    [Fact]
    public async Task Endpoints_Status_DescribesTheHost()
    {
        var (endpoints, _, _, _) = Endpoints();
        var stream = new DuplexStream(Array.Empty<byte>());
        await endpoints.StatusAsync(stream);
        var json = stream.Response[(stream.Response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("192.168.1.5", doc.RootElement.GetProperty("ip").GetString());
        Assert.Equal(5050, doc.RootElement.GetProperty("port").GetInt32());
    }
}
