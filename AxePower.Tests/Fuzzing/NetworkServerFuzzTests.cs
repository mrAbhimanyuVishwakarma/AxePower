using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.ShareScreen;
using Xunit;
using static AxePower.Tests.Fuzzing.Mutator;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Mutated raw requests against the real LAN Drop and Share Screen servers on loopback. Each
/// input is sent, the sending side is closed, and the server must answer or hang up within the
/// time limit; afterwards the server must still serve a valid request, and no file may appear
/// outside the test's own folder.
///
/// Safety: inputs that name /api/remote are never sent (that route presses real media keys and
/// can lock the workstation), and Share Screen control is never switched on, so nothing here can
/// reach the desktop. The real Downloads folder is checked to be unchanged.
/// </summary>
[Collection(FuzzCollection.Name)]
public class NetworkServerFuzzTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "AxePower_FuzzServers_" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly LogService _log = new();

    private static readonly string RealLanDropFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AxePower_LanDrop");

    public NetworkServerFuzzTests()
    {
        _downloads = Path.Combine(_sandbox, "downloads");
        Directory.CreateDirectory(_downloads);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string RealFolderSnapshot() =>
        Directory.Exists(RealLanDropFolder)
            ? string.Join("|", Directory.GetFiles(RealLanDropFolder).OrderBy(f => f).Select(f => f + ":" + new FileInfo(f).Length))
            : "<absent>";

    /// <summary>Sends one input, closes the sending side, and reads until the server hangs up.</summary>
    private static byte[] Exchange(int port, byte[] request)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        try
        {
            stream.Write(request);
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (IOException) { /* the server may refuse before reading everything */ }

        using var response = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                response.Write(buffer, 0, read);
                if (response.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Response larger than any page the server serves.");
            }
        }
        catch (IOException) { /* reset by the server: an acceptable way to refuse */ }
        return response.ToArray();
    }

    private static readonly FuzzHarness.Budget ServerBudget =
        new(TimeSpan.FromSeconds(6), length => 48L * 1024 * 1024 + 128L * length, ProcessWide: true);

    // -- LAN Drop ---------------------------------------------------------------

    [Fact]
    public void LanDropServer_SurvivesMutatedRequests()
    {
        var before = RealFolderSnapshot();
        var token = new PortalAccessToken();
        using var service = new LanDropService(_log, token, music: null,
            limits: new LanDropSecurityLimits
            {
                ReadTimeout = TimeSpan.FromMilliseconds(800),
                WriteTimeout = TimeSpan.FromMilliseconds(800),
                MaxFileSizeBytes = 256 * 1024,
                MaxTotalRequestSizeBytes = 512 * 1024,
                SessionWatchdogInterval = TimeSpan.FromHours(1)
            },
            diskSpaceChecker: _ => long.MaxValue,
            networkClassifier: _ => new NetworkClassificationResult
            {
                Category = NetworkSecurityCategory.Private, IsPublicOrUnclassified = false, NetworkName = "test"
            },
            addressIsLocal: _ => true,
            downloadDirectory: _downloads);

        var port = FreePort();
        Assert.True(service.StartServerAsync("127.0.0.1", port).GetAwaiter().GetResult());
        var t = token.Value;

        string Multipart(string b) =>
            $"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"note.txt\"\r\n\r\nhello\r\n--{b}--\r\n";
        byte[] Post(string path, string contentType, string body) =>
            Utf8($"POST {path}?t={t} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

        var seeds = new[]
        {
            Utf8($"GET /?t={t} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: Android\r\n\r\n"),
            Utf8($"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1\r\nCookie: axe_session={t}\r\n\r\n"),
            Utf8($"GET /api/download?id=missing&t={t} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"),
            Post("/api/text", "text/plain", "hello from the fuzzer"),
            Post("/api/upload", "multipart/form-data; boundary=XyZ", Multipart("XyZ")),
            Post("/api/upload", "multipart/form-data; boundary=\"" + new string('Q', 70) + "\"", Multipart(new string('Q', 70))),
        };

        var inputs = Mutants(seeds, 400, FuzzTestCorpus.DeterministicSeed + 30, maxLength: 64 * 1024)
            .Where(b => !Encoding.Latin1.GetString(b).Contains("/api/remote", StringComparison.Ordinal));

        // Mutated tokens trip the per-IP lockout after five failures, which is the point of it,
        // but it would also stop every later input at a 429. Clearing it per input keeps the
        // fuzzer reaching the parsers behind authentication.
        void Property(byte[] input)
        {
            service.RateLimiter.Reset();
            Exchange(port, input);
        }

        FuzzHarness.Check("landrop-server", inputs, Property, ServerBudget);
        FuzzHarness.Check("landrop-server", FuzzHarness.RegressionInputs("landrop-server"), Property, ServerBudget);
        service.RateLimiter.Reset();

        // Still serving, nothing half-staged, nothing outside the test folder, real folder untouched.
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(Exchange(port, seeds[1])));
        Assert.Empty(Directory.GetFiles(_downloads, ".landrop_*.tmp"));
        Assert.Equal(new[] { _downloads }, Directory.GetFileSystemEntries(_sandbox));
        Assert.Equal(before, RealFolderSnapshot());
    }

    // -- Share Screen -----------------------------------------------------------

    private const string NoncePlaceholder = "NONCE_PLACEHOLDER";
    private const string ProofPlaceholder = "PROOF_PLACEHOLDER";

    [Fact]
    public void ShareViewerHost_SurvivesMutatedRequestsAndFrames()
    {
        var input = new RemoteInputService(_log); // control is never enabled in this test
        using var host = new ShareViewerHost(_log, input)
        {
            RequestHeadTimeout = TimeSpan.FromMilliseconds(800),
            IdleTimeout = TimeSpan.FromMilliseconds(800),
            PingInterval = TimeSpan.FromMinutes(10)
        };
        var room = ShareRoomFactory.Create();
        host.Start(room, "127.0.0.1", webrtcPort: 18889, preferredPort: 19400);
        var port = host.Port;

        static byte[] MaskedFrame(int opcode, byte[] payload)
        {
            var key = new byte[] { 1, 2, 3, 4 };
            var head = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length <= 125) head.Add((byte)(0x80 | payload.Length));
            else { head.Add(0x80 | 126); head.Add((byte)(payload.Length >> 8)); head.Add((byte)payload.Length); }
            head.AddRange(key);
            head.AddRange(payload.Select((b, i) => (byte)(b ^ key[i % 4])));
            return head.ToArray();
        }

        string Upgrade(string channel) =>
            $"GET /{room.PathName}/{channel}?n={NoncePlaceholder}&p={ProofPlaceholder} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";

        var chatWire = RoomMessageCrypto.Encrypt(new ShareChatMessage("m1", "Fuzz", "hi", DateTime.UtcNow), room.ViewerToken);
        var voiceWire = RoomVoiceCrypto.Encrypt(new byte[] { 0xFC, 1, 2, 3 }, room.ViewerToken);
        var seeds = new[]
        {
            Utf8($"GET /{room.PathName} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"),
            Utf8($"GET /{room.PathName}/nonce HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"),
            Utf8(Upgrade("chat")).Concat(MaskedFrame(0x1, chatWire)).Concat(MaskedFrame(0x9, Array.Empty<byte>())).Concat(MaskedFrame(0x8, Array.Empty<byte>())).ToArray(),
            Utf8(Upgrade("voice")).Concat(MaskedFrame(0x2, voiceWire)).ToArray(),
            Utf8(Upgrade("ws")).Concat(MaskedFrame(0x1, Utf8("1|00112233445566778899aabbccddeeff|{\"t\":\"r\"}"))).ToArray(),
        };

        // A fresh nonce and proof are spliced in when the placeholders survived mutation, so the
        // fuzzer gets past admission and reaches the frame parser, not just the handshake.
        byte[] Admit(byte[] bytes)
        {
            var text = Encoding.Latin1.GetString(bytes);
            if (!text.Contains(NoncePlaceholder, StringComparison.Ordinal)) return bytes;
            var page = Encoding.ASCII.GetString(Exchange(port, seeds[1]));
            // Mutants that never use their nonce leave it outstanding; at the cap of 64 the host
            // answers 503, as designed. Then the input goes out unadmitted.
            if (!page.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return bytes;
            var nonce = JsonDocument.Parse(page[(page.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).RootElement.GetProperty("n").GetString()!;
            var channel = text.Contains("/voice?", StringComparison.Ordinal) ? "voice" : "chat";
            var proof = ShareScreenCrypto.ComputeProof(ShareScreenCrypto.DeriveAuthKey(room.ViewerToken), channel, nonce);
            return Encoding.Latin1.GetBytes(text.Replace(NoncePlaceholder, nonce).Replace(ProofPlaceholder, proof));
        }

        void Property(byte[] bytes) => Exchange(port, Admit(bytes));

        FuzzHarness.Check("share-viewer-server", Mutants(seeds, 400, FuzzTestCorpus.DeterministicSeed + 31, maxLength: 64 * 1024), Property, ServerBudget);
        FuzzHarness.Check("share-viewer-server", FuzzHarness.RegressionInputs("share-viewer-server"), Property, ServerBudget);

        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(Exchange(port, seeds[0])));
        Assert.False(input.IsAccepting);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (host.GetActiveParticipants().Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Empty(host.GetActiveParticipants());
    }
}
