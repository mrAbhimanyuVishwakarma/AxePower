using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// HTTPS rooms over Tailscale. Tailscale itself is not on the build machine, so the provider is
/// driven with a fake <c>tailscale</c>, and the servers are exercised over real TLS with a
/// self-signed certificate made here. The real <c>tailscale cert</c> run is listed as unverified.
/// </summary>
public class ShareTlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_ShareTls_" + Guid.NewGuid().ToString("N"));

    public ShareTlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A self-signed certificate for localhost, written as PEM the way tailscale cert writes it.</summary>
    private (string CertPath, string KeyPath, X509Certificate2 Usable, string Thumbprint) MakeCertificate(string name = "localhost")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        var certPath = Path.Combine(_dir, "room.crt");
        var keyPath = Path.Combine(_dir, "room.key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        var usable = new X509Certificate2(cert.Export(X509ContentType.Pkcs12));
        return (certPath, keyPath, usable, cert.Thumbprint);
    }

    private static HttpClient PinnedClient(string thumbprint) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && cert.Thumbprint == thumbprint
    }) { Timeout = TimeSpan.FromSeconds(10) };

    // -- Detecting Tailscale and asking it for a certificate ---------------------------------

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("[fd7a:115c:a1e0:ab12::3]", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("gaming-pc.tail1234.ts.net", true)]
    [InlineData("gaming-pc.tail1234.ts.net.", true)]
    [InlineData("example.net", false)]
    [InlineData("", false)]
    public void TailscaleHosts_AreRecognised(string host, bool expected) =>
        Assert.Equal(expected, TailscaleCertificateProvider.IsTailscaleHost(host));

    [Fact]
    public void TailscaleStatus_IsParsed()
    {
        var (dns, ips) = TailscaleCertificateProvider.ParseStatus(
            "{\"Self\":{\"DNSName\":\"gaming-pc.tail1234.ts.net.\",\"TailscaleIPs\":[\"100.101.102.103\",\"fd7a:115c:a1e0::1\"]}}");
        Assert.Equal("gaming-pc.tail1234.ts.net", dns);
        Assert.Equal(new[] { "100.101.102.103", "fd7a:115c:a1e0::1" }, ips);
        Assert.Null(TailscaleCertificateProvider.ParseStatus("{\"Self\":{\"DNSName\":\"\"}}").DnsName);
        Assert.Null(TailscaleCertificateProvider.ParseStatus("[]").DnsName);
    }

    private const string Status = "{\"Self\":{\"DNSName\":\"gaming-pc.tail1234.ts.net.\",\"TailscaleIPs\":[\"100.101.102.103\"]}}";

    [Fact]
    public async Task Provider_IssuesForThisPcsTailscaleName()
    {
        var made = MakeCertificate("gaming-pc.tail1234.ts.net");
        var calls = new System.Collections.Generic.List<string>();
        var provider = new TailscaleCertificateProvider(new LogService(), "tailscale.exe", _dir, (exe, args, _) =>
        {
            calls.Add(args);
            if (args.StartsWith("status", StringComparison.Ordinal)) return Task.FromResult((0, Status, ""));
            // tailscale cert writes the PEM files where it was told to; ours are already there.
            Assert.Contains($"--cert-file \"{made.CertPath}\"", args);
            Assert.Contains($"--key-file \"{made.KeyPath}\"", args);
            Assert.EndsWith(" gaming-pc.tail1234.ts.net", args);
            return Task.FromResult((0, "", ""));
        });

        var byIp = await provider.TryGetAsync("100.101.102.103", CancellationToken.None);
        Assert.NotNull(byIp);
        Assert.Equal("gaming-pc.tail1234.ts.net", byIp!.HostName);
        Assert.True(byIp.Certificate.HasPrivateKey);
        Assert.Equal(made.Thumbprint, byIp.Certificate.Thumbprint);

        var byName = await provider.TryGetAsync("gaming-pc.tail1234.ts.net", CancellationToken.None);
        Assert.NotNull(byName);
        Assert.Equal(4, calls.Count);
    }

    [Fact]
    public async Task Provider_StaysOnHttp_WhenItCannot()
    {
        var ran = 0;
        TailscaleCertificateProvider With(Func<string, (int, string, string)> answer) =>
            new(new LogService(), "tailscale.exe", _dir, (_, args, _) => { ran++; return Task.FromResult(answer(args)); });

        // Not a Tailscale address: Tailscale is never asked.
        Assert.Null(await With(_ => (0, Status, "")).TryGetAsync("192.168.1.5", CancellationToken.None));
        Assert.Equal(0, ran);

        // HTTPS not switched on for the tailnet.
        Assert.Null(await With(a => a.StartsWith("status") ? (0, Status, "") : (1, "", "your Tailscale account does not support getting TLS certs"))
            .TryGetAsync("100.101.102.103", CancellationToken.None));

        // Someone else's Tailscale address.
        Assert.Null(await With(_ => (0, Status, "")).TryGetAsync("100.90.90.90", CancellationToken.None));

        // tailscale not running.
        Assert.Null(await With(_ => (1, "", "failed to connect to local tailscaled")).TryGetAsync("100.101.102.103", CancellationToken.None));
    }

    // -- The viewer host over real TLS ------------------------------------------------------

    [Fact]
    public async Task ViewerHost_ServesTheRoomOverHttps_AndKeepsPlainHttpForTheMediaServerOnly()
    {
        var cert = MakeCertificate();
        var input = new RemoteInputService(new LogService());
        using var host = new ShareViewerHost(new LogService(), input);
        var room = ShareRoomFactory.Create();
        host.Start(room, "localhost", webrtcPort: 18889, preferredPort: 19600, tls: cert.Usable);

        Assert.True(host.IsSecure);
        Assert.StartsWith($"https://localhost:{host.Port}/{room.PathName}#v=", host.ViewerUrl);

        using var https = PinnedClient(cert.Thumbprint);
        var page = await https.GetStringAsync($"https://localhost:{host.Port}/{room.PathName}");
        Assert.Contains($"wss://localhost:{host.Port}/{room.PathName}/ws", page);
        Assert.Contains($"https://localhost:18889/{room.PathName}/whep", page);
        Assert.DoesNotContain("ws://", page.Replace("wss://", string.Empty));

        // Plain HTTP for the page is refused, even from this PC.
        using var plain = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var refused = await plain.GetAsync($"http://127.0.0.1:{host.Port}/{room.PathName}");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The media server's check still arrives over plain loopback HTTP.
        Assert.StartsWith("http://127.0.0.1:", host.MediaAuthAddress);
        var auth = await plain.PostAsync(host.MediaAuthAddress, new StringContent("{\"action\":\"read\",\"path\":\"x\"}"));
        Assert.Equal(HttpStatusCode.Unauthorized, auth.StatusCode);
    }

    [Fact]
    public async Task ViewerHost_ChatWorksOverWss()
    {
        var cert = MakeCertificate();
        var input = new RemoteInputService(new LogService());
        using var host = new ShareViewerHost(new LogService(), input);
        var room = ShareRoomFactory.Create();
        host.Start(room, "localhost", webrtcPort: 18889, preferredPort: 19650, tls: cert.Usable);
        var received = new TaskCompletionSource<ShareChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatMessageReceived += (_, m) => received.TrySetResult(m);

        using var https = PinnedClient(cert.Thumbprint);
        var roomUri = new Uri($"https://localhost:{host.Port}/{room.PathName}");
        var socketUri = await ShareRoomClient.BuildAdmittedSocketUriAsync(roomUri, room.ViewerToken, ShareScreenCrypto.ChatChannel, CancellationToken.None, https);
        Assert.Equal("wss", socketUri.Scheme);

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, c, _, _) => c != null && new X509Certificate2(c).Thumbprint == cert.Thumbprint;
        await ws.ConnectAsync(socketUri, CancellationToken.None);
        var wire = RoomMessageCrypto.Encrypt(new ShareChatMessage("m1", "Alex", "over tls", DateTime.UtcNow), room.ViewerToken);
        await ws.SendAsync(wire, WebSocketMessageType.Text, true, CancellationToken.None);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("over tls", message.Text);
        Assert.Contains(host.GetActiveParticipants(), p => p.Role == "Chat/Viewer");
    }

    [Fact]
    public void MediaMtxConfig_ServesWhepOverHttpsWithTheCertificate()
    {
        var cert = MakeCertificate();
        var tls = new ShareTlsCertificate(cert.Usable, "localhost", cert.CertPath, cert.KeyPath);
        var config = MediaMtxServer.BuildConfig(ShareRoomFactory.Create(), new ShareScreenSettings(), 9997, "http://127.0.0.1:1/x", tls);
        Assert.Contains("webrtcEncryption: yes", config);
        Assert.Contains($"webrtcServerKey: '{cert.KeyPath}'", config);
        Assert.Contains($"webrtcServerCert: '{cert.CertPath}'", config);
        Assert.Contains("webrtcEncryption: no", MediaMtxServer.BuildConfig(ShareRoomFactory.Create(), new ShareScreenSettings(), 9997, "http://127.0.0.1:1/x"));
    }

    [Fact]
    public async Task Live_MediaMtx_ServesWhepOverHttps_AndStillChecksProofs()
    {
        if (!LiveMediaMtx.Available) return; // tools not on this PC

        var cert = MakeCertificate();
        var input = new RemoteInputService(new LogService());
        using var host = new ShareViewerHost(new LogService(), input);
        var room = ShareRoomFactory.Create();
        host.Start(room, "localhost", webrtcPort: 18889, preferredPort: 19700, tls: cert.Usable);

        using var live = new LiveMediaMtx();
        await live.StartAsync(_dir, room, (ports, apiPort) =>
            MediaMtxServer.BuildConfig(room, ports, apiPort, host.MediaAuthAddress, new ShareTlsCertificate(cert.Usable, "localhost", cert.CertPath, cert.KeyPath)));
        var settings = live.Settings;
        using var https = PinnedClient(cert.Thumbprint);

        async Task<HttpStatusCode> Whep(string query) =>
            (await https.PostAsync($"https://localhost:{settings.WebrtcPort}/{room.PathName}/whep{query}",
                new StringContent("v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n", Encoding.ASCII, "application/sdp"))).StatusCode;

        Assert.Equal(HttpStatusCode.Unauthorized, await Whep(string.Empty));
        var nonce = JsonDocument.Parse(await https.GetStringAsync($"https://localhost:{host.Port}/{room.PathName}/nonce")).RootElement.GetProperty("n").GetString()!;
        var proof = ShareScreenCrypto.ComputeProof(ShareScreenCrypto.DeriveAuthKey(room.ViewerToken), ShareScreenCrypto.WhepChannel, nonce);
        Assert.NotEqual(HttpStatusCode.Unauthorized, await Whep($"?n={nonce}&p={proof}"));
    }
}
