using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.Music;
using AxePower.Core.Services.Update;
using Xunit;

namespace AxePower.Tests.Diagnostics;

/// <summary>
/// Error paths that carry secrets, logged through the real pipeline into a real diagnostics folder.
/// Each test then reads the raw files on disk, the thing an exported bundle is made of.
/// </summary>
public class SecretsInErrorPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_ErrPaths_" + Guid.NewGuid().ToString("N"));
    private readonly DiagnosticsStore _store;
    private readonly LogService _log;

    public SecretsInErrorPathsTests()
    {
        _store = new DiagnosticsStore(Path.Combine(_dir, "diag"));
        _log = new LogService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string OnDisk() => string.Concat(_store.Files().Select(f => DiagnosticsStore.ReadText(f.FullPath)));

    private static Exception Thrown(Func<Exception> make)
    {
        try { throw make(); }
        catch (Exception ex) { return ex; } // a real stack trace, as logged in the field
    }

    [Fact]
    public void FailuresThatEchoSecrets_LeaveNoneOnDisk()
    {
        const string lanToken = "Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpLTE0";
        const string roomSecret = "0123456789abcdef0123456789abcdef";
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1LTEiLCJyb2xlIjoiYXV0aGVudGljYXRlZCJ9.c2lnbmF0dXJlc2lnbmF0dXJl";
        const string refresh = "v1.refresh-token-AbCdEf123456";
        const string email = "someone.private@example.com";
        var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "notes.txt");

        _log.LogError("[LanDrop] Request failed", Thrown(() => new HttpRequestException($"Response status code does not indicate success: 500 for http://192.168.1.23:5050/api/download?id=1&t={lanToken}")));
        _log.LogWarning("[ShareScreen] Room connection dropped", Thrown(() => new WebSocketException($"Unable to connect to wss://host.example/room/abc/chat?n=nonce123&p=proof456#v={roomSecret}")));
        _log.LogError("[Account] Unexpected reply", Thrown(() => new JsonException($"{{\"access_token\":\"{jwt}\",\"refresh_token\":\"{refresh}\",\"user\":{{\"email\":\"{email}\"}}}}")));
        _log.LogWarning($"[CloudBridge] Handshake failed: Authorization: Bearer {jwt}");
        _log.LogWarning($"[Account] Signed in as {email}");
        _log.LogError("[Music] Could not read file", Thrown(() => new IOException($"The process cannot access the file '{profilePath}' because it is being used by another process.")));
        _log.LogWarning("[Update] Proxy refused", Thrown(() => new SocketException(10061)));
        _log.LogInfo($"[App] Running on {Environment.MachineName}");

        var disk = OnDisk();
        Assert.Equal(8, _store.ReadEntries().Count);
        foreach (var secret in new[] { lanToken, roomSecret, "nonce123", "proof456", jwt, refresh, email, Environment.MachineName })
            Assert.DoesNotContain(secret, disk, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), disk.Replace("\\\\", "\\"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\\Documents\\notes.txt", disk); // the useful part survives (JSON-escaped)
    }

    [Fact]
    public async Task LanDrop_WrongAccessLinks_AreLoggedAsOneLockout_WithoutAnyToken()
    {
        var token = new PortalAccessToken(new MemorySecrets());
        var downloads = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(downloads);
        using var service = new LanDropService(_log, token, downloadDirectory: downloads) { DownloadDirectory = downloads };
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));

        var wrong = Enumerable.Range(0, 6).Select(i => $"WrongTokenGuess{i}AbCdEfGhIjKlMnOpQrStUvWxYz012").ToList();
        foreach (var guess in wrong) await Get(port, $"/api/status?t={guess}");
        service.RateLimiter.Reset();
        await Get(port, $"/api/status?t={token.Value}");
        service.StopServer();

        var entries = _store.ReadEntries();
        var lockout = Assert.Single(entries, e => e.EventId == LogEvents.LanDropAccessRefused.Id);
        Assert.Equal("LanDrop", lockout.Category);
        Assert.False(string.IsNullOrEmpty(lockout.CorrelationId));
        var disk = OnDisk();
        Assert.DoesNotContain(token.Value, disk);
        foreach (var guess in wrong) Assert.DoesNotContain(guess, disk);
    }

    [Fact]
    public async Task Update_ARefusedDownload_IsASecurityEvent_WithItsOwnCorrelation()
    {
        var service = new UpdateService(_log, httpClient: new HttpClient(new NeverCalled()), downloadHttpClient: new HttpClient(new NeverCalled()));

        await Assert.ThrowsAsync<UpdateVerificationException>(() => service.DownloadUpdateAsync(
            "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases/download/v9/AxePower-v9-Setup.exe?token=ghp_1234567890abcdefghijABCDEFGHIJ0123",
            new Progress<double>(), CancellationToken.None, manifest: null));

        var refused = Assert.Single(_store.ReadEntries(), e => e.EventId == LogEvents.UpdateVerificationFailed.Id);
        Assert.Equal("WARN", refused.Level);
        Assert.False(string.IsNullOrEmpty(refused.CorrelationId));
        Assert.DoesNotContain("ghp_1234567890abcdefghijABCDEFGHIJ0123", OnDisk());
    }

    private static async Task Get(int port, string path)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        var buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer) > 0) { } }
        catch (IOException) { /* A refused request may end in a reset, which is the server's choice. */ }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private string? _value;
        public string? Get(string key) => _value;
        public void Set(string key, string? value) => _value = value;
    }

    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("No request may be sent before the manifest is verified.");
    }
}
