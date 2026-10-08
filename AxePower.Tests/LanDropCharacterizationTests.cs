using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
/// Characterization of LAN Drop's supported behaviour, written before LanDropService was split
/// into components (Prompt 17). Every route, status code, header and event the portal relies on
/// is pinned here over real loopback sockets, so the refactor can prove it changed nothing a phone
/// or the app can see. /api/remote is pinned only for an action that presses no key.
/// </summary>
public sealed class LanDropCharacterizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_LanDropChar_" + Guid.NewGuid().ToString("N"));
    private readonly List<LanDropService> _services = new();
    private readonly LogService _log = new();

    public LanDropCharacterizationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (LanDropService Service, int Port, string Token, string Downloads) Start()
    {
        var downloads = Path.Combine(_dir, "downloads-" + _services.Count);
        Directory.CreateDirectory(downloads);
        var token = new PortalAccessToken();
        var service = new LanDropService(_log, token, music: null,
            limits: new LanDropSecurityLimits { ReadTimeout = TimeSpan.FromSeconds(3), WriteTimeout = TimeSpan.FromSeconds(3), SessionWatchdogInterval = TimeSpan.FromHours(1) },
            diskSpaceChecker: _ => long.MaxValue,
            networkClassifier: _ => new NetworkClassificationResult { Category = NetworkSecurityCategory.Private, IsPublicOrUnclassified = false, NetworkName = "test" },
            addressIsLocal: _ => true,
            downloadDirectory: downloads);
        _services.Add(service);
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Assert.True(service.StartServerAsync("127.0.0.1", port).GetAwaiter().GetResult());
        return (service, port, token.Value, downloads);
    }

    private sealed record Response(int Status, Dictionary<string, string> Headers, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);
    }

    private static async Task<Response> SendAsync(int port, string head, byte[]? body = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
        if (body != null) await stream.WriteAsync(body);

        using var all = new MemoryStream();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(5000);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0) all.Write(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }

        var bytes = all.ToArray();
        var split = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        if (split < 0) return new Response(0, new Dictionary<string, string>(), Array.Empty<byte>());
        var lines = Encoding.ASCII.GetString(bytes, 0, split).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        return new Response(int.Parse(lines[0].Split(' ')[1]), headers, bytes[(split + 4)..]);
    }

    private static string Get(string path, string token, string ua = "Mozilla/5.0 (Linux; Android 14)") =>
        $"GET {path}{(path.Contains('?') ? '&' : '?')}t={token} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: {ua}\r\n\r\n";

    private static string Post(string path, string token, int length, string contentType = "text/plain", string extra = "", string ua = "Mozilla/5.0 (Linux; Android 14)") =>
        $"POST {path}?t={token} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: {ua}\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\n{extra}\r\n";

    private static void AssertStandardHeaders(Response r)
    {
        Assert.Equal("nosniff", r.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", r.Headers["Referrer-Policy"]);
        Assert.Equal("no-store", r.Headers["Cache-Control"]);
        Assert.Equal("close", r.Headers["Connection"]);
        Assert.Equal(r.Body.Length.ToString(), r.Headers["Content-Length"]);
        Assert.False(r.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    // -- Portal page -------------------------------------------------------------------

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task PortalPage_IsServedWithItsSessionCookie(string path)
    {
        var (_, port, token, _) = Start();
        var r = await SendAsync(port, Get(path, token));

        Assert.Equal(200, r.Status);
        Assert.Equal("text/html; charset=utf-8", r.Headers["Content-Type"]);
        AssertStandardHeaders(r);
        Assert.StartsWith($"{LanDropService.SessionCookieName}={token}; Path=/; HttpOnly; SameSite=Strict", r.Headers["Set-Cookie"]);
        Assert.StartsWith("<!DOCTYPE html>", r.Text);
        Assert.Contains("<title>AxePower Mobile Hub</title>", r.Text);
        foreach (var route in new[] { "/api/upload", "/api/text", "/api/remote", "/api/status", "/api/download" })
            Assert.Contains(route, r.Text);
        Assert.Contains("history.replaceState", r.Text);
    }

    // -- Status ------------------------------------------------------------------------

    [Fact]
    public async Task Status_ReportsHostSharedFilesClipboardAndBattery()
    {
        var (service, port, token, _) = Start();
        var shared = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(shared, "0123456789");
        service.AddSharedFile(shared);
        service.PostTextMessageFromPc("  from the pc  ");

        var r = await SendAsync(port, Get("/api/status", token));
        Assert.Equal(200, r.Status);
        Assert.Equal("application/json; charset=utf-8", r.Headers["Content-Type"]);
        AssertStandardHeaders(r);

        using var json = JsonDocument.Parse(r.Body);
        var root = json.RootElement;
        Assert.Equal(Environment.MachineName, root.GetProperty("hostName").GetString());
        Assert.Equal("127.0.0.1", root.GetProperty("ip").GetString());
        Assert.Equal(port, root.GetProperty("port").GetInt32());
        Assert.Equal("from the pc", root.GetProperty("clipboard").GetString());
        var file = Assert.Single(root.GetProperty("sharedFiles").EnumerateArray());
        Assert.Equal("notes.txt", file.GetProperty("name").GetString());
        Assert.Equal(10, file.GetProperty("size").GetInt64());
        Assert.False(string.IsNullOrEmpty(file.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(file.GetProperty("formattedSize").GetString()));
        Assert.Matches(@"^\d\d:\d\d$", file.GetProperty("time").GetString()!);
        var battery = root.GetProperty("battery");
        Assert.InRange(battery.GetProperty("percent").GetInt32(), 0, 100);
        Assert.True(battery.GetProperty("charging").ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    // -- Downloads -----------------------------------------------------------------------

    [Fact]
    public async Task Download_StreamsASharedFileAsAnAttachment()
    {
        var (service, port, token, _) = Start();
        var path = Path.Combine(_dir, "my photo & more.bin");
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(path, content);
        service.AddSharedFile(path);
        var id = service.GetSharedFiles().Single().Id;

        var r = await SendAsync(port, Get($"/api/download?id={id}", token));
        Assert.Equal(200, r.Status);
        Assert.Equal("application/octet-stream", r.Headers["Content-Type"]);
        Assert.Equal(content.Length.ToString(), r.Headers["Content-Length"]);
        Assert.Equal($"attachment; filename=\"{WebUtility.UrlEncode("my photo & more.bin")}\"", r.Headers["Content-Disposition"]);
        Assert.Equal("nosniff", r.Headers["X-Content-Type-Options"]);
        Assert.Equal(content, r.Body);
    }

    [Fact]
    public async Task Download_OfAMissingOrRemovedFile_Is404()
    {
        var (service, port, token, _) = Start();
        var path = Path.Combine(_dir, "gone.txt");
        File.WriteAllText(path, "x");
        service.AddSharedFile(path);
        var id = service.GetSharedFiles().Single().Id;
        File.Delete(path);

        foreach (var target in new[] { "/api/download", "/api/download?id=nope", $"/api/download?id={id}" })
        {
            var r = await SendAsync(port, Get(target, token));
            Assert.Equal(404, r.Status);
            Assert.Equal("File Not Found", r.Text);
        }

        service.RemoveSharedFile(id);
        Assert.Empty(service.GetSharedFiles());
    }

    // -- Quick text ------------------------------------------------------------------------

    [Fact]
    public async Task QuickText_IsReceivedTrimmedAndBecomesTheLatestClipboard()
    {
        var (service, port, token, _) = Start();
        var received = new List<LanDropTextMessage>();
        service.OnTextReceived += (_, m) => received.Add(m);

        var body = Encoding.UTF8.GetBytes("  hello from the phone \n");
        var r = await SendAsync(port, Post("/api/text", token, body.Length), body);
        Assert.Equal(200, r.Status);
        Assert.Equal("{\"success\":true}", r.Text);
        AssertStandardHeaders(r);

        var message = Assert.Single(received);
        Assert.Equal("hello from the phone", message.Content);
        Assert.Equal("Android Phone", message.SenderDevice);
        Assert.False(message.IsFromPc);
        Assert.Equal("hello from the phone", service.GetClipboardMessages().First().Content);

        // An empty body is accepted and ignored.
        var empty = Encoding.UTF8.GetBytes("   ");
        Assert.Equal(200, (await SendAsync(port, Post("/api/text", token, empty.Length), empty)).Status);
        Assert.Single(received);
    }

    // -- Uploads ---------------------------------------------------------------------------

    [Fact]
    public async Task MultipartUpload_SavesTheFileAndRaisesFileReceived()
    {
        var (service, port, token, downloads) = Start();
        var received = new List<LanDropFileItem>();
        service.OnFileReceived += (_, f) => received.Add(f);

        const string b = "----WebKitFormBoundaryAbc123";
        var body = Encoding.UTF8.GetBytes(
            $"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"holiday.jpg\"\r\nContent-Type: image/jpeg\r\n\r\nJPEGDATA\r\n--{b}--\r\n");
        var r = await SendAsync(port, Post("/api/upload", token, body.Length, $"multipart/form-data; boundary={b}", ua: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)"), body);

        Assert.Equal(200, r.Status);
        Assert.Equal("{\"success\":true}", r.Text);
        var item = Assert.Single(received);
        Assert.Equal("holiday.jpg", item.FileName);
        Assert.Equal("iPhone", item.DeviceName);
        Assert.True(item.IsReceived);
        Assert.Equal(8, item.SizeBytes);
        Assert.Equal("JPEGDATA", File.ReadAllText(Path.Combine(downloads, "holiday.jpg")));
        Assert.Equal(item.FilePath, service.GetReceivedFiles().Single().FilePath);
    }

    [Fact]
    public async Task RawUpload_UsesTheDecodedXFileNameOrAGeneratedName()
    {
        var (service, port, token, downloads) = Start();
        var named = Encoding.UTF8.GetBytes("raw bytes");
        var r = await SendAsync(port, Post("/api/upload", token, named.Length, "application/octet-stream", $"X-File-Name: {WebUtility.UrlEncode("my report.pdf")}\r\n"), named);
        Assert.Equal(200, r.Status);
        Assert.Equal("raw bytes", File.ReadAllText(Path.Combine(downloads, "my report.pdf")));

        var anonymous = Encoding.UTF8.GetBytes("no name");
        Assert.Equal(200, (await SendAsync(port, Post("/api/upload", token, anonymous.Length, "application/octet-stream"), anonymous)).Status);
        Assert.Contains(Directory.GetFiles(downloads), f => Path.GetFileName(f).StartsWith("drop_") && f.EndsWith(".bin"));
        Assert.Equal(2, service.GetReceivedFiles().Count);
    }

    [Fact]
    public async Task MultipartUpload_WithoutABoundary_Is400WithAMessage()
    {
        var (_, port, token, _) = Start();
        // No body: the server answers before reading one, and unread request bytes would make
        // Windows reset the connection and drop the answer's body before the client sees it.
        var r = await SendAsync(port, Post("/api/upload", token, 0, "multipart/form-data"));
        Assert.Equal(400, r.Status);
        Assert.Equal("{\"success\":false,\"error\":\"Missing boundary in multipart request.\"}", r.Text);
    }

    [Fact]
    public async Task StoppingTheServer_EndsAnUploadInFlight_AndLeavesNoFiles()
    {
        var (service, port, token, downloads) = Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(Post("/api/upload", token, 1_000_000, "application/octet-stream", "X-File-Name: big.bin\r\n")));
        await stream.WriteAsync(new byte[10_000]);
        await Task.Delay(300);

        service.StopServer();

        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(5000);
        try { while (await stream.ReadAsync(buffer, cts.Token) > 0) { } } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        Assert.False(cts.IsCancellationRequested, "the connection should close when the server stops");
        await Task.Delay(200);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    // -- Other routes ----------------------------------------------------------------------

    [Fact]
    public async Task Qr_IsAnSvgOfThePortalLink()
    {
        var (_, port, token, _) = Start();
        var r = await SendAsync(port, Get("/qr", token));
        Assert.Equal(200, r.Status);
        Assert.Equal("image/svg+xml", r.Headers["Content-Type"]);
        Assert.Contains("<svg", r.Text);
    }

    [Fact]
    public async Task UnknownRoutesAndMethods_AreAnsweredAsBefore()
    {
        var (_, port, token, _) = Start();

        var getMissing = await SendAsync(port, Get("/nope", token));
        Assert.Equal(404, getMissing.Status);
        Assert.Equal("Not Found", getMissing.Text);

        var postMissing = await SendAsync(port, Post("/api/nope", token, 0));
        Assert.Equal(404, postMissing.Status);

        // Music routes without a player.
        Assert.Equal(404, (await SendAsync(port, Get("/api/music/state", token))).Status);
        Assert.Equal("No player", (await SendAsync(port, Get("/api/music/state", token))).Text);

        // OPTIONS is answered before authentication, with nothing in it.
        var options = await SendAsync(port, "OPTIONS /api/upload HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.Equal(204, options.Status);
        Assert.Empty(options.Body);

        // Any other method, even authenticated, gets no response at all.
        var put = await SendAsync(port, $"PUT /api/text?t={token} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\n\r\n");
        Assert.Equal(0, put.Status);
    }

    [Fact]
    public async Task RemoteAction_UnknownActionStillSucceedsWithoutPressingAnything()
    {
        var (_, port, token, _) = Start();
        var body = Encoding.UTF8.GetBytes("not-an-action");
        var r = await SendAsync(port, Post("/api/remote", token, body.Length), body);
        Assert.Equal(200, r.Status);
        Assert.Equal("{\"success\":true}", r.Text);

        var big = new byte[1025];
        Assert.Equal(413, (await SendAsync(port, Post("/api/remote", token, big.Length), big)).Status);
    }

    private sealed class RecordingRemote : IRemoteMediaActions
    {
        public List<RemoteMediaAction> Performed { get; } = new();
        public void Perform(RemoteMediaAction action) { lock (Performed) Performed.Add(action); }
    }

    [Fact]
    public async Task RemoteActions_MapEveryPortalButton_WithoutTouchingTheRealPc()
    {
        var remote = new RecordingRemote();
        var token = new PortalAccessToken();
        using var service = new LanDropService(_log, token,
            limits: new LanDropSecurityLimits { SessionWatchdogInterval = TimeSpan.FromHours(1) },
            networkClassifier: _ => new NetworkClassificationResult { Category = NetworkSecurityCategory.Private, IsPublicOrUnclassified = false, NetworkName = "t" },
            addressIsLocal: _ => true, downloadDirectory: Path.Combine(_dir, "remote"), remoteActions: remote);
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));

        var expected = new (string Body, RemoteMediaAction Action)[]
        {
            ("play-pause", RemoteMediaAction.PlayPause), ("next", RemoteMediaAction.Next), ("prev", RemoteMediaAction.Previous),
            ("vol-up", RemoteMediaAction.VolumeUp), ("vol-down", RemoteMediaAction.VolumeDown), ("mute", RemoteMediaAction.Mute),
            (" LOCK\n", RemoteMediaAction.Lock)
        };
        foreach (var (body, _) in expected.Append(("dance", default)))
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var r = await SendAsync(port, Post("/api/remote", token.Value, bytes.Length), bytes);
            Assert.Equal(200, r.Status);
            Assert.Equal("{\"success\":true}", r.Text);
        }

        Assert.Equal(expected.Select(e => e.Action), remote.Performed);   // "dance" did nothing
    }

    // -- Lifecycle, events, disposal ----------------------------------------------------------

    [Fact]
    public async Task StateAndClientEvents_AreRaisedAsBefore()
    {
        var token = new PortalAccessToken();
        using var service = new LanDropService(_log, token,
            limits: new LanDropSecurityLimits { SessionWatchdogInterval = TimeSpan.FromHours(1) },
            networkClassifier: _ => new NetworkClassificationResult { Category = NetworkSecurityCategory.Private, IsPublicOrUnclassified = false, NetworkName = "t" },
            addressIsLocal: _ => true, downloadDirectory: Path.Combine(_dir, "events"));
        var states = new List<LanDropServerState>();
        var clientChanges = 0;
        service.OnStateChanged += (_, s) => states.Add(s);
        service.OnClientsChanged += (_, _) => Interlocked.Increment(ref clientChanges);

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        var started = states.Last();
        Assert.True(started.IsRunning);
        Assert.Equal("127.0.0.1", started.SelectedIp);
        Assert.Equal(port, started.Port);
        Assert.Equal($"http://127.0.0.1:{port}/?t={token.Value}", started.PortalUrl);
        Assert.Equal($"http://127.0.0.1:{port}/", service.ServerUrl);

        var before = clientChanges;
        await SendAsync(port, Get("/api/status", token.Value));
        await SendAsync(port, Get("/api/status", token.Value)); // same device: no second "added"
        Assert.Equal(before + 1, clientChanges);
        Assert.Equal("Android Device", service.GetConnectedClients().Single().DeviceName);

        service.StopServer();
        Assert.False(states.Last().IsRunning);
        Assert.Equal(string.Empty, service.PortalUrl);
        Assert.Empty(service.GetConnectedClients());
    }

    [Fact]
    public async Task Dispose_StopsTheServerAndFreesThePort()
    {
        var (service, port, _, _) = Start();
        service.Dispose();
        Assert.False(service.IsRunning);

        await Task.Delay(100);
        using var rebind = new TcpListener(IPAddress.Loopback, port);
        rebind.Start(); // throws if the port were still held
        rebind.Stop();
    }

    [Fact]
    public async Task OrphanStagingFiles_AreCleanedWhenTheFolderIsUsed()
    {
        var folder = Path.Combine(_dir, "orphans");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".landrop_deadbeef.tmp"), "half an upload");
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "a real file");

        using var service = new LanDropService(_log, new PortalAccessToken(), downloadDirectory: folder);
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(folder).Select(Path.GetFileName));

        File.WriteAllText(Path.Combine(folder, ".landrop_cafe.tmp"), "another");
        service.DownloadDirectory = folder;
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        await Task.CompletedTask;
    }
}
