using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.ShareScreen;
using AxePower.Tests.Friends;
using Xunit;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// The tests named in docs/security/SHARE_SCREEN_THREAT_MODEL.md §8. They run against a real
/// viewer host over loopback sockets. Control frames sent here carry only the harmless
/// "release all" event, so no test moves the real mouse or presses a real key.
/// </summary>
public class ShareScreenSecurityTests : IDisposable
{
    private readonly RemoteInputService _input = new(new LogService());
    private readonly ShareViewerHost _host;
    private readonly ShareRoom _room = ShareRoomFactory.Create();

    private static readonly CaptureDisplay Screen =
        new(0, @"\\.\DISPLAY1", "Display 1", 1920, 1080, 60, true);

    private const string ReleaseEvent = "{\"t\":\"r\"}";

    public ShareScreenSecurityTests()
    {
        _host = new ShareViewerHost(new LogService(), _input)
        {
            // These tests are about the protocol; the host's Allow prompt is covered on its own below.
            ApproveControl = (_, _) => Task.FromResult(true)
        };
        _host.Start(_room, "127.0.0.1", webrtcPort: 18889, preferredPort: 19100);
    }

    // -- Helpers -------------------------------------------------------------

    private sealed class Upgrade : IDisposable
    {
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
        public required int Status { get; init; }
        public void Dispose() => Client.Dispose();
    }

    private async Task<string> GetNonceAsync()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /{_room.PathName}/nonce HTTP/1.1\r\nHost: 127.0.0.1:{_host.Port}\r\n\r\n"));
        var text = await ReadAllAsync(stream);
        Assert.StartsWith("HTTP/1.1 200", text);
        using var json = JsonDocument.Parse(text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
        return json.RootElement.GetProperty("n").GetString()!;
    }

    private async Task<string> AuthQueryAsync(string channel, string? viewerToken = null)
    {
        var nonce = await GetNonceAsync();
        var key = ShareScreenCrypto.DeriveAuthKey(viewerToken ?? _room.ViewerToken);
        return $"n={nonce}&p={ShareScreenCrypto.ComputeProof(key, channel, nonce)}";
    }

    private async Task<(string Query, byte[] FrameKey)> ControlQueryAsync(string code, string? viewerToken = null)
    {
        var nonce = await GetNonceAsync();
        var key = ShareScreenCrypto.DeriveControlKey(viewerToken ?? _room.ViewerToken, code);
        return ($"n={nonce}&p={ShareScreenCrypto.ComputeProof(key, ShareScreenCrypto.ControlChannel, nonce)}",
            ShareScreenCrypto.DeriveControlFrameKey(key, nonce));
    }

    private async Task<Upgrade> UpgradeAsync(
        string target, string? origin = null, string? host = null, IPAddress? fromAddress = null)
    {
        var client = fromAddress == null ? new TcpClient() : new TcpClient(new IPEndPoint(fromAddress, 0));
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);

        var request = new StringBuilder();
        request.Append($"GET {target} HTTP/1.1\r\n");
        request.Append($"Host: {host ?? $"127.0.0.1:{_host.Port}"}\r\n");
        if (!string.IsNullOrEmpty(origin)) request.Append($"Origin: {origin}\r\n");
        request.Append("Upgrade: websocket\r\nConnection: Upgrade\r\n");
        request.Append("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n");
        request.Append("Sec-WebSocket-Version: 13\r\n\r\n");

        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));

        var head = await ReadHeadAsync(stream);
        var parts = head.Split("\r\n")[0].Split(' ');
        var status = parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0;
        return new Upgrade { Client = client, Stream = stream, Status = status };
    }

    private async Task<int> UpgradeStatusAsync(string target, string? origin = null, string? host = null, IPAddress? fromAddress = null)
    {
        using var upgrade = await UpgradeAsync(target, origin, host, fromAddress);
        return upgrade.Status;
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new MemoryStream();
        var one = new byte[1];
        using var cts = new CancellationTokenSource(5000);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(one, cts.Token);
                if (read == 0) break;
                bytes.WriteByte(one[0]);
                if (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream)
    {
        var bytes = new MemoryStream();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(5000);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0) bytes.Write(buffer, 0, read);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>True once the server has closed the connection (EOF, a close frame, or a reset).</summary>
    private static async Task<bool> ServerClosesAsync(NetworkStream stream, int withinMs)
    {
        var buffer = new byte[1024];
        using var cts = new CancellationTokenSource(withinMs);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0) return true;
                // A close frame from the server also counts; pings are skipped.
                if ((buffer[0] & 0x0F) == 0x8) return true;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return true; }
    }

    private static string SignedControlFrame(byte[] frameKey, long seq, string json) =>
        ShareScreenCrypto.SealControlFrame(frameKey, seq, json);

    private static byte[] MaskedFrame(byte[] payload, int opcode)
    {
        var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        byte[] frame;
        int offset;
        if (payload.Length <= 125)
        {
            frame = new byte[6 + payload.Length];
            frame[1] = (byte)(0x80 | payload.Length);
            offset = 2;
        }
        else if (payload.Length <= 65535)
        {
            frame = new byte[8 + payload.Length];
            frame[1] = 0x80 | 126;
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)(payload.Length & 0xFF);
            offset = 4;
        }
        else
        {
            frame = new byte[14 + payload.Length];
            frame[1] = 0x80 | 127;
            for (var i = 0; i < 8; i++) frame[2 + i] = (byte)(((long)payload.Length >> (56 - i * 8)) & 0xFF);
            offset = 10;
        }
        frame[0] = (byte)(0x80 | opcode);
        Buffer.BlockCopy(mask, 0, frame, offset, 4);
        for (var i = 0; i < payload.Length; i++) frame[offset + 4 + i] = (byte)(payload[i] ^ mask[i % 4]);
        return frame;
    }

    private static Task SendTextAsync(NetworkStream stream, string text) =>
        stream.WriteAsync(MaskedFrame(Encoding.UTF8.GetBytes(text), 0x1)).AsTask();

    // -- S1/S2: nothing secret on the wire, nonces single-use -----------------

    [Fact]
    public async Task UpgradeRequests_CarryNeitherViewerTokenNorControlCode()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            var chatUri = await ShareRoomClient.BuildAdmittedSocketUriAsync(
                new Uri($"http://127.0.0.1:{_host.Port}/{_room.PathName}#v={_room.ViewerToken}"),
                _room.ViewerToken, ShareScreenCrypto.ChatChannel, CancellationToken.None);
            var (controlQuery, _) = await ControlQueryAsync(code);

            foreach (var wire in new[] { chatUri.ToString(), controlQuery })
            {
                Assert.DoesNotContain(_room.ViewerToken, wire);
                Assert.DoesNotContain(code, wire, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("v=", wire);
                Assert.DoesNotContain("k=", wire);
            }

            Assert.Equal(101, await UpgradeStatusAsync(chatUri.PathAndQuery));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ReplayedUpgrade_WithConsumedNonce_IsRejected()
    {
        var query = await AuthQueryAsync(ShareScreenCrypto.ChatChannel);
        using (var first = await UpgradeAsync($"/{_room.PathName}/chat?{query}"))
        {
            Assert.Equal(101, first.Status);
        }
        Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/chat?{query}"));
    }

    [Fact]
    public void Nonces_AreSingleUse_ExpireAndAreCapped()
    {
        var now = DateTime.UtcNow;
        var nonces = new AdmissionNonces(maxOutstanding: 2, lifetime: TimeSpan.FromSeconds(30), clock: () => now);

        var a = nonces.Issue()!;
        Assert.True(nonces.TryConsume(a));
        Assert.False(nonces.TryConsume(a));

        var b = nonces.Issue()!;
        var c = nonces.Issue()!;
        Assert.Null(nonces.Issue()); // cap reached

        now = now.AddSeconds(31);
        Assert.False(nonces.TryConsume(b)); // expired
        Assert.NotNull(nonces.Issue());     // expired ones are pruned
        Assert.False(nonces.TryConsume("never-issued"));
        _ = c;
    }

    // -- X1/X2: cross-site and rebinding --------------------------------------

    [Fact]
    public async Task WrongOrigin_IsRejectedWith403_OnControlChatAndVoice()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            const string evil = "https://malicious-attacker.com";
            var (controlQuery, _) = await ControlQueryAsync(code);
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{controlQuery}", evil));
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/chat?{await AuthQueryAsync("chat")}", evil));
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/voice?{await AuthQueryAsync("voice")}", evil));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ValidOrigin_MatchingHost_IsAccepted()
    {
        var query = await AuthQueryAsync("chat");
        Assert.Equal(101, await UpgradeStatusAsync($"/{_room.PathName}/chat?{query}", $"http://127.0.0.1:{_host.Port}"));
    }

    [Fact]
    public async Task ReboundHostHeader_IsRejected()
    {
        // A DNS-rebound page: its Origin and Host both name the attacker's domain, which an
        // Origin-only check would let through.
        var query = await AuthQueryAsync("chat");
        Assert.Equal(421, await UpgradeStatusAsync(
            $"/{_room.PathName}/chat?{query}", "http://rebind.attacker.example", $"rebind.attacker.example:{_host.Port}"));
        Assert.Equal(421, await UpgradeStatusAsync($"/{_room.PathName}", host: "rebind.attacker.example"));
        Assert.True(_host.IsAllowedHost($"127.0.0.1:{_host.Port}"));
        Assert.True(_host.IsAllowedHost("[::1]:1234"));
        Assert.False(_host.IsAllowedHost(""));
        Assert.False(_host.IsAllowedHost("8.8.8.8"));
    }

    // -- B1: brute force ------------------------------------------------------

    [Fact]
    public async Task BruteForceGuessing_TriggersLockoutAndReturns429()
    {
        _host.EnableControl(Screen);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var (query, _) = await ControlQueryAsync("WRONG" + i);
                Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{query}"));
            }

            var (next, _) = await ControlQueryAsync("WRONG9");
            Assert.Equal(429, await UpgradeStatusAsync($"/{_room.PathName}/ws?{next}"));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task TooManyFailedControlAttempts_SwitchControlOff()
    {
        var code = _host.EnableControl(Screen);
        var disabled = false;
        _host.ControlDisabledAfterFailedAttempts += (_, _) => disabled = true;

        // Four loopback source addresses, five failures each: under every per-IP lockout, but
        // twenty in the session.
        for (var source = 2; source <= 5; source++)
        {
            var from = IPAddress.Parse($"127.0.0.{source}");
            for (var i = 0; i < 5; i++)
            {
                var (query, _) = await ControlQueryAsync("BAD" + source + i);
                await UpgradeStatusAsync($"/{_room.PathName}/ws?{query}", fromAddress: from);
            }
        }

        Assert.True(disabled);
        Assert.False(_input.IsAccepting);

        // The code the host gave out is void now.
        var (valid, _) = await ControlQueryAsync(code);
        Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{valid}", fromAddress: IPAddress.Parse("127.0.0.9")));
    }

    // -- §4.5: domain separation ----------------------------------------------

    [Fact]
    public void KeyDerivation_IsDomainSeparated()
    {
        const string token = "0123456789abcdef0123456789abcdef";
        var keys = new[]
        {
            ShareScreenCrypto.DeriveChatKey(token),
            ShareScreenCrypto.DeriveVoiceKey(token),
            ShareScreenCrypto.DeriveAuthKey(token),
            ShareScreenCrypto.DeriveControlKey(token, "ABCDEFGH"),
            ShareScreenCrypto.DeriveControlKey(token, "ABCDEFGJ"),
            ShareScreenCrypto.DeriveControlFrameKey(ShareScreenCrypto.DeriveControlKey(token, "ABCDEFGH"), "nonce-1"),
            ShareScreenCrypto.DeriveControlFrameKey(ShareScreenCrypto.DeriveControlKey(token, "ABCDEFGH"), "nonce-2"),
            ShareScreenCrypto.DeriveChatKey("another-room-token-value-000000")
        };

        Assert.All(keys, k => Assert.Equal(32, k.Length));
        Assert.Equal(keys.Length, keys.Select(Convert.ToHexString).Distinct().Count());

        // A proof for one channel is not a proof for another, even under the same key.
        var auth = ShareScreenCrypto.DeriveAuthKey(token);
        var chatProof = ShareScreenCrypto.ComputeProof(auth, "chat", "n1");
        Assert.False(ShareScreenCrypto.VerifyProof(auth, "voice", "n1", chatProof));
        Assert.True(ShareScreenCrypto.VerifyProof(auth, "chat", "n1", chatProof));
    }

    // -- P1/P2: replay, poisoning, legacy -------------------------------------

    [Fact]
    public void ChatReplay_IsRejected_AfterAuthentication()
    {
        var filter = new ReplayFilter();
        var msg = new ShareChatMessage("m1", "Alex", "hello", DateTime.UtcNow);
        var wire = RoomMessageCrypto.Encrypt(msg, _room.ViewerToken, sequence: 5, sender: "00000000000000aa");

        Assert.True(RoomMessageCrypto.TryDecrypt(wire, _room.ViewerToken, filter, out var first, out var seq));
        Assert.Equal("hello", first!.Text);
        Assert.Equal(5, seq);
        Assert.False(RoomMessageCrypto.TryDecrypt(wire, _room.ViewerToken, filter, out _, out _));

        // The same sequence from a different sender is a different message.
        var other = RoomMessageCrypto.Encrypt(msg, _room.ViewerToken, sequence: 5, sender: "00000000000000bb");
        Assert.True(RoomMessageCrypto.TryDecrypt(other, _room.ViewerToken, filter, out _, out _));
    }

    [Fact]
    public void VoiceReplay_IsRejected_AfterAuthentication()
    {
        var filter = new ReplayFilter();
        var opus = new byte[] { 0xFC, 0xFF, 0xFE, 1, 2, 3 };
        var wire = RoomVoiceCrypto.Encrypt(opus, _room.ViewerToken, sequence: 42);

        Assert.True(RoomVoiceCrypto.TryDecrypt(wire, _room.ViewerToken, filter, out var decoded, out var seq));
        Assert.Equal(opus, decoded);
        Assert.Equal(42, seq);
        Assert.False(RoomVoiceCrypto.TryDecrypt(wire, _room.ViewerToken, filter, out _, out _));
    }

    [Fact]
    public void ForgedSequence_DoesNotPoisonTheWindow()
    {
        var filter = new ReplayFilter();
        const string sender = "00000000000000cc";
        var msg = new ShareChatMessage("m1", "Alex", "hello", DateTime.UtcNow);

        // Rewrite a captured envelope's sequence to 6: it must fail authentication…
        var captured = RoomMessageCrypto.Encrypt(msg, _room.ViewerToken, sequence: 5, sender: sender);
        var forged = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(captured).Replace("\"Sequence\":5", "\"Sequence\":6"));
        Assert.NotEqual(captured, forged);
        Assert.False(RoomMessageCrypto.TryDecrypt(forged, _room.ViewerToken, filter, out _, out _));

        // …and must not have used up sequence 6 for the real sender.
        var genuine = RoomMessageCrypto.Encrypt(msg, _room.ViewerToken, sequence: 6, sender: sender);
        Assert.True(RoomMessageCrypto.TryDecrypt(genuine, _room.ViewerToken, filter, out _, out _));

        // Voice: the sequence bytes are associated data.
        var voiceFilter = new ReplayFilter();
        var packet = RoomVoiceCrypto.Encrypt(new byte[] { 1, 2, 3 }, _room.ViewerToken, sequence: 7);
        var tampered = (byte[])packet.Clone();
        tampered[16] ^= 0x01; // last byte of the sequence
        Assert.False(RoomVoiceCrypto.TryDecrypt(tampered, _room.ViewerToken, voiceFilter, out _, out _));
        Assert.True(RoomVoiceCrypto.TryDecrypt(packet, _room.ViewerToken, voiceFilter, out _, out _));
    }

    [Fact]
    public void LegacyEnvelopes_AreRejected()
    {
        var filter = new ReplayFilter();
        var v1Chat = Encoding.UTF8.GetBytes(
            "{\"Version\":1,\"Nonce\":\"AAAAAAAAAAAAAAAA\",\"Ciphertext\":\"AAAA\",\"Tag\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"Sequence\":1}");
        Assert.False(RoomMessageCrypto.TryDecrypt(v1Chat, _room.ViewerToken, filter, out _, out _));

        var current = RoomVoiceCrypto.Encrypt(new byte[] { 9, 9, 9 }, _room.ViewerToken, sequence: 1);
        foreach (byte legacy in new byte[] { 1, 2 })
        {
            var old = (byte[])current.Clone();
            old[0] = legacy;
            Assert.False(RoomVoiceCrypto.TryDecrypt(old, _room.ViewerToken, filter, out _, out _));
        }
    }

    [Fact]
    public void ReplayFilter_AcceptsReorderingInsideTheWindow_AndRejectsTheRest()
    {
        var filter = new ReplayFilter();
        Assert.True(filter.TryAccept("s", 10));
        Assert.True(filter.TryAccept("s", 8));     // late but new
        Assert.False(filter.TryAccept("s", 8));    // duplicate
        Assert.True(filter.TryAccept("s", 10 + ReplayFilter.WindowSize));
        Assert.False(filter.TryAccept("s", 10));   // fell out of the window
        Assert.True(filter.TryAccept("s", 11));    // slot reused correctly
        Assert.False(filter.TryAccept("s", -1));

        var bounded = new ReplayFilter(maxSenders: 2);
        Assert.True(bounded.TryAccept("a", 1));
        Assert.True(bounded.TryAccept("b", 1));
        Assert.True(bounded.TryAccept("c", 1));    // evicts the oldest, memory stays bounded
    }

    // -- Tampering ------------------------------------------------------------

    [Fact]
    public void ChatTamperedCiphertextTagOrHeader_FailsDecryption()
    {
        var msg = new ShareChatMessage("m1", "Alex", "secret text", DateTime.UtcNow);
        var wire = RoomMessageCrypto.Encrypt(msg, _room.ViewerToken, sequence: 3, sender: "00000000000000dd");
        Assert.DoesNotContain("secret text", Encoding.UTF8.GetString(wire));

        // Edit the parsed envelope, not its text: the serializer escapes characters such as '+'.
        byte[] Tamper(Action<System.Text.Json.Nodes.JsonObject> change)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(wire)!.AsObject();
            change(node);
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }

        string Flip(string b64)
        {
            var bytes = Convert.FromBase64String(b64);
            bytes[0] ^= 0x01;
            return Convert.ToBase64String(bytes);
        }

        foreach (var tampered in new[]
                 {
                     Tamper(n => n["Ciphertext"] = Flip(n["Ciphertext"]!.GetValue<string>())),
                     Tamper(n => n["Tag"] = Flip(n["Tag"]!.GetValue<string>())),
                     Tamper(n => n["Nonce"] = Flip(n["Nonce"]!.GetValue<string>())),
                     Tamper(n => n["Sender"] = "00000000000000de"),
                     Tamper(n => n["Sequence"] = 4)
                 })
        {
            Assert.False(RoomMessageCrypto.TryDecrypt(tampered, _room.ViewerToken, new ReplayFilter(), out _, out _));
        }
        Assert.True(RoomMessageCrypto.TryDecrypt(Tamper(_ => { }), _room.ViewerToken, new ReplayFilter(), out _, out _));

        Assert.False(RoomMessageCrypto.TryDecrypt(wire, "wrong-room-token-wrong-room-tok", new ReplayFilter(), out _, out _));
    }

    [Fact]
    public void VoiceTamperedCiphertextTagOrHeader_FailsDecryption()
    {
        var wire = RoomVoiceCrypto.Encrypt(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, _room.ViewerToken, sequence: 9);
        foreach (var index in new[] { 3 /* sender */, 20 /* nonce */, 35 /* tag */, wire.Length - 1 /* ciphertext */ })
        {
            var tampered = (byte[])wire.Clone();
            tampered[index] ^= 0x80;
            Assert.False(RoomVoiceCrypto.TryDecrypt(tampered, _room.ViewerToken, new ReplayFilter(), out _, out _));
        }
        Assert.False(RoomVoiceCrypto.TryDecrypt(wire, "wrong-room-token-wrong-room-tok", new ReplayFilter(), out _, out _));
    }

    // -- P3: control frame integrity and ordering -----------------------------

    [Fact]
    public async Task ControlFrame_WithBadMac_DisconnectsAndReleases()
    {
        var code = _host.EnableControl(Screen);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.ControlHolderChanged += (_, taken) => { if (!taken) released.TrySetResult(true); };
        try
        {
            var (query, frameKey) = await ControlQueryAsync(code);
            using var controller = await UpgradeAsync($"/{_room.PathName}/ws?{query}");
            Assert.Equal(101, controller.Status);

            await SendTextAsync(controller.Stream, SignedControlFrame(frameKey, 1, ReleaseEvent));
            var forged = $"2|{Convert.ToBase64String(new byte[ReleaseEvent.Length + 16])}";
            await SendTextAsync(controller.Stream, forged);

            Assert.True(await ServerClosesAsync(controller.Stream, 3000));
            Assert.True(await Task.WhenAny(released.Task, Task.Delay(3000)) == released.Task);
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ControlFrame_OutOfOrder_Disconnects()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            var (query, frameKey) = await ControlQueryAsync(code);
            using var controller = await UpgradeAsync($"/{_room.PathName}/ws?{query}");
            Assert.Equal(101, controller.Status);

            await SendTextAsync(controller.Stream, SignedControlFrame(frameKey, 2, ReleaseEvent));
            await SendTextAsync(controller.Stream, SignedControlFrame(frameKey, 1, ReleaseEvent)); // replay/reorder

            Assert.True(await ServerClosesAsync(controller.Stream, 3000));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public void ControlFrames_AreNotReadableOnTheWire()
    {
        var frameKey = ShareScreenCrypto.DeriveControlFrameKey(ShareScreenCrypto.DeriveControlKey(_room.ViewerToken, "ABCDEFGH"), "n");
        const string keystroke = "{\"t\":\"k\",\"c\":80,\"p\":1}";
        var wire = SignedControlFrame(frameKey, 1, keystroke);
        Assert.DoesNotContain("\"t\"", wire);
        var sealedBytes = Convert.FromBase64String(wire.Split('|')[1]);
        Assert.True(sealedBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(keystroke)) < 0, "plaintext visible in the frame");
        Assert.Equal(Encoding.UTF8.GetByteCount(keystroke) + 16, sealedBytes.Length);
        Assert.NotEqual(SignedControlFrame(frameKey, 2, keystroke).Split('|')[1], wire.Split('|')[1]); // fresh nonce per frame

        long last = 0;
        Assert.True(ShareViewerHost.TryOpenControlFrame(wire, frameKey, ref last, out var opened));
        Assert.Equal(keystroke, opened);
    }

    [Fact]
    public void ControlFrames_FromAnotherConnection_DoNotVerify()
    {
        var key = ShareScreenCrypto.DeriveControlKey(_room.ViewerToken, "ABCDEFGH");
        var frameKeyA = ShareScreenCrypto.DeriveControlFrameKey(key, "nonce-a");
        var frameKeyB = ShareScreenCrypto.DeriveControlFrameKey(key, "nonce-b");
        long last = 0;
        Assert.False(ShareViewerHost.TryOpenControlFrame(SignedControlFrame(frameKeyA, 1, ReleaseEvent), frameKeyB, ref last, out _));
        Assert.True(ShareViewerHost.TryOpenControlFrame(SignedControlFrame(frameKeyB, 1, ReleaseEvent), frameKeyB, ref last, out var json));
        Assert.Equal(ReleaseEvent, json);
        Assert.False(ShareViewerHost.TryOpenControlFrame("not a frame", frameKeyB, ref last, out _));
    }

    // -- §4.7: sizes, slow heads, idle sockets --------------------------------

    [Fact]
    public async Task OversizedControlFrame_TerminatesConnection()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            var (query, _) = await ControlQueryAsync(code);
            using var controller = await UpgradeAsync($"/{_room.PathName}/ws?{query}");
            Assert.Equal(101, controller.Status);

            await controller.Stream.WriteAsync(MaskedFrame(new byte[ShareViewerHost.MaxControlFrameBytes + 1], 0x1));
            Assert.True(await ServerClosesAsync(controller.Stream, 3000));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task OversizedVoiceFrame_TerminatesConnection()
    {
        using var voice = await UpgradeAsync($"/{_room.PathName}/voice?{await AuthQueryAsync("voice")}");
        Assert.Equal(101, voice.Status);

        await voice.Stream.WriteAsync(MaskedFrame(new byte[ShareViewerHost.MaxVoiceFrameBytes + 1], 0x2));
        Assert.True(await ServerClosesAsync(voice.Stream, 3000));
    }

    [Fact]
    public async Task SlowRequestHead_IsDropped()
    {
        _host.RequestHeadTimeout = TimeSpan.FromMilliseconds(300);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /{_room.PathName} HTTP/1.1\r\nHost: 127.0.0.1"));

        Assert.True(await ServerClosesAsync(stream, 3000));
    }

    [Fact]
    public async Task IdleSocket_IsClosed()
    {
        _host.IdleTimeout = TimeSpan.FromMilliseconds(400);
        _host.PingInterval = TimeSpan.FromMinutes(10);
        using var chat = await UpgradeAsync($"/{_room.PathName}/chat?{await AuthQueryAsync("chat")}");
        Assert.Equal(101, chat.Status);

        Assert.True(await ServerClosesAsync(chat.Stream, 3000));
    }

    [Fact]
    public async Task ServerPings_KeepAnAnsweringClientAlive()
    {
        _host.IdleTimeout = TimeSpan.FromMilliseconds(600);
        _host.PingInterval = TimeSpan.FromMilliseconds(150);
        using var chat = await UpgradeAsync($"/{_room.PathName}/chat?{await AuthQueryAsync("chat")}");
        Assert.Equal(101, chat.Status);

        // Answer every ping with a pong, the way a browser does, for well past the idle limit.
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        var buffer = new byte[64];
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(400);
            int read;
            try { read = await chat.Stream.ReadAsync(buffer, cts.Token); }
            catch (OperationCanceledException) { continue; }
            Assert.NotEqual(0, read);
            if ((buffer[0] & 0x0F) == 0x9) await chat.Stream.WriteAsync(MaskedFrame(Array.Empty<byte>(), 0xA));
        }

        Assert.Single(_host.GetActiveParticipants());
    }

    // -- S8: no takeover ------------------------------------------------------

    [Fact]
    public async Task SecondController_IsRejectedWhileControlIsHeld()
    {
        var code = _host.EnableControl(Screen);
        try
        {
            var (first, _) = await ControlQueryAsync(code);
            using var holder = await UpgradeAsync($"/{_room.PathName}/ws?{first}");
            Assert.Equal(101, holder.Status);

            var (second, _) = await ControlQueryAsync(code);
            Assert.Equal(409, await UpgradeStatusAsync($"/{_room.PathName}/ws?{second}"));

            // The holder is still connected.
            Assert.Contains(_host.GetActiveParticipants(), p => p.Role == "Controller");
        }
        finally
        {
            _host.DisableControl();
        }
    }

    // -- R3: the host's Allow prompt ---------------------------------------------

    [Fact]
    public async Task ControlRequest_IsRefusedWhenTheHostDenies()
    {
        var code = _host.EnableControl(Screen);
        _host.ApproveControl = (_, _) => Task.FromResult(false);
        try
        {
            var (query, _) = await ControlQueryAsync(code);
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{query}"));
            Assert.DoesNotContain(_host.GetActiveParticipants(), p => p.Role == "Controller");

            // The slot is free again: an approved attempt gets in.
            _host.ApproveControl = (_, _) => Task.FromResult(true);
            var (again, _) = await ControlQueryAsync(code);
            Assert.Equal(101, await UpgradeStatusAsync($"/{_room.PathName}/ws?{again}"));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ControlRequest_IsRefusedWithNobodyToApprove()
    {
        var code = _host.EnableControl(Screen);
        _host.ApproveControl = null;
        try
        {
            var (query, _) = await ControlQueryAsync(code);
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{query}"));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ControlRequest_LapsesWithoutAnAnswer()
    {
        var code = _host.EnableControl(Screen);
        _host.ApprovalTimeout = TimeSpan.FromMilliseconds(300);
        _host.ApproveControl = (_, _) => new TaskCompletionSource<bool>().Task; // the host never answers
        try
        {
            var (query, _) = await ControlQueryAsync(code);
            var started = DateTime.UtcNow;
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{query}"));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task ControlRequest_WhileAwaitingApproval_HoldsTheSlot()
    {
        var code = _host.EnableControl(Screen);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource<ControlApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.ApproveControl = (request, _) => { asked.TrySetResult(request); return answer.Task; };
        try
        {
            var (first, _) = await ControlQueryAsync(code);
            var firstUpgrade = UpgradeAsync($"/{_room.PathName}/ws?{first}");
            var request = await asked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.StartsWith("127.0.0.1:", request.Endpoint);

            var (second, _) = await ControlQueryAsync(code);
            Assert.Equal(409, await UpgradeStatusAsync($"/{_room.PathName}/ws?{second}"));

            answer.SetResult(true);
            using var holder = await firstUpgrade;
            Assert.Equal(101, holder.Status);
        }
        finally
        {
            _host.DisableControl();
        }
    }

    [Fact]
    public async Task Service_RefusesControlWithoutAnswer_AndPassesTheHostsChoice()
    {
        var log = new LogService();
        var viewerHost = new ShareViewerHost(log, new RemoteInputService(log));
        using var service = new ShareScreenService(log, new FriendsHubTests.FakeToolProvider(), new FriendsHubTests.FakeDeviceProvider(),
            new ShareEncoderProbe(log), new MediaMtxServer(log), new ShareEncodePipeline(log), new ShareAudioPump(log), viewerHost);
        var request = new ControlApprovalRequest("192.168.1.9:5000", "Chrome", DateTime.UtcNow);

        Assert.NotNull(viewerHost.ApproveControl);
        Assert.False(await viewerHost.ApproveControl!(request, CancellationToken.None)); // nobody listening

        service.ControlApprovalRequested += (_, pending) => { Assert.Equal(request, pending.Request); pending.Allow(); };
        Assert.True(await viewerHost.ApproveControl!(request, CancellationToken.None));

        // Shown but never answered: the request lapses to a refusal when its time runs out.
        var quietHost = new ShareViewerHost(log, new RemoteInputService(log));
        using var quiet = new ShareScreenService(log, new FriendsHubTests.FakeToolProvider(), new FriendsHubTests.FakeDeviceProvider(),
            new ShareEncoderProbe(log), new MediaMtxServer(log), new ShareEncodePipeline(log), new ShareAudioPump(log), quietHost);
        quiet.ControlApprovalRequested += (_, _) => { };
        using var lapsed = new CancellationTokenSource(100);
        Assert.False(await quietHost.ApproveControl!(request, lapsed.Token));
    }

    [Fact]
    public void Nonces_AreCappedPerClientAddress()
    {
        var nonces = new AdmissionNonces(maxOutstanding: 64, maxPerClient: 8);
        for (var i = 0; i < 8; i++) Assert.NotNull(nonces.Issue("192.168.1.50"));
        Assert.Null(nonces.Issue("192.168.1.50"));   // this address has used its share
        Assert.NotNull(nonces.Issue("192.168.1.51")); // everyone else can still join
    }

    // -- Stale sessions and the exit criterion --------------------------------

    [Fact]
    public async Task StaleSessionSecrets_FromPreviousRoom_AreRejected()
    {
        var oldRoom = _room;
        var newRoom = ShareRoomFactory.Create();
        _host.Start(newRoom, "127.0.0.1", webrtcPort: 18889, preferredPort: 19100);

        // The old path no longer exists at all.
        Assert.Equal(404, await UpgradeStatusAsync($"/{oldRoom.PathName}/chat?n=x&p=y"));

        // A proof made with the old room's token fails on the new room.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _host.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /{newRoom.PathName}/nonce HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        var text = await ReadAllAsync(stream);
        var nonce = JsonDocument.Parse(text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).RootElement.GetProperty("n").GetString()!;
        var staleProof = ShareScreenCrypto.ComputeProof(ShareScreenCrypto.DeriveAuthKey(oldRoom.ViewerToken), "chat", nonce);
        Assert.Equal(403, await UpgradeStatusAsync($"/{newRoom.PathName}/chat?n={nonce}&p={staleProof}"));
    }

    [Fact]
    public async Task RemoteInput_NeedsCurrentSessionProofAndHostEnabledControl()
    {
        // Control off: even a correctly formed proof for some code is refused.
        var (offQuery, _) = await ControlQueryAsync("ABCDEFGH");
        Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{offQuery}"));

        var code = _host.EnableControl(Screen);
        try
        {
            // Right code, wrong room token.
            var (wrongToken, _) = await ControlQueryAsync(code, viewerToken: "ffffffffffffffffffffffffffffffff");
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{wrongToken}", fromAddress: IPAddress.Parse("127.0.0.11")));

            // Right token, wrong code.
            var (wrongCode, _) = await ControlQueryAsync("ZZZZZZZZ");
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{wrongCode}", fromAddress: IPAddress.Parse("127.0.0.12")));

            // A chat admission proof is not a control proof.
            var chatProof = await AuthQueryAsync("control");
            Assert.Equal(403, await UpgradeStatusAsync($"/{_room.PathName}/ws?{chatProof}", fromAddress: IPAddress.Parse("127.0.0.13")));

            // Both, and the host has control on: admitted.
            var (good, _) = await ControlQueryAsync(code);
            using var controller = await UpgradeAsync($"/{_room.PathName}/ws?{good}", fromAddress: IPAddress.Parse("127.0.0.14"));
            Assert.Equal(101, controller.Status);
        }
        finally
        {
            _host.DisableControl();
        }
    }

    // -- R-1/R-2: revocation ---------------------------------------------------

    [Fact]
    public void DisableControl_ImmediatelyRevokesInputGate()
    {
        _host.EnableControl(Screen);
        Assert.True(_input.IsAccepting);

        _host.DisableControl();
        Assert.False(_input.IsAccepting);
    }

    [Fact]
    public async Task ControllerDisconnect_ReleasesHeldKeysAndUpdatesControlState()
    {
        var code = _host.EnableControl(Screen);
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.ControlHolderChanged += (_, taken) =>
        {
            if (taken) connected.TrySetResult(true);
            else disconnected.TrySetResult(true);
        };

        try
        {
            var (query, _) = await ControlQueryAsync(code);
            using (var controller = await UpgradeAsync($"/{_room.PathName}/ws?{query}"))
            {
                Assert.Equal(101, controller.Status);
                Assert.True(await Task.WhenAny(connected.Task, Task.Delay(2000)) == connected.Task);
            }

            Assert.True(await Task.WhenAny(disconnected.Task, Task.Delay(3000)) == disconnected.Task);
            Assert.DoesNotContain(_host.GetActiveParticipants(), p => p.Role == "Controller");
        }
        finally
        {
            _host.DisableControl();
        }
    }

    // -- V1: who is in the room ------------------------------------------------

    [Fact]
    public async Task ParticipantTracking_ListsActiveParticipants()
    {
        Assert.Empty(_host.GetActiveParticipants());

        using (var chat = await UpgradeAsync($"/{_room.PathName}/chat?{await AuthQueryAsync("chat")}"))
        using (var voice = await UpgradeAsync($"/{_room.PathName}/voice?{await AuthQueryAsync("voice")}"))
        {
            Assert.Equal(101, chat.Status);
            Assert.Equal(101, voice.Status);
            var active = _host.GetActiveParticipants();
            Assert.Equal(2, active.Count);
            Assert.Contains(active, p => p.Role == "Chat/Viewer");
            Assert.Contains(active, p => p.Role == "Voice");
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_host.GetActiveParticipants().Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Empty(_host.GetActiveParticipants());
    }

    [Fact]
    public void MediaMtxSessions_AreParsedIntoVideoViewers()
    {
        // Shape of MediaMTX's /v3/webrtcsessions/list (see threat model R8).
        var json = $$"""
            {"itemCount":3,"pageCount":1,"items":[
              {"id":"5b9e3f2a-1111-2222-3333-444455556666","created":"2026-10-04T10:15:00Z","remoteAddr":"192.168.1.20:53000",
               "peerConnectionEstablished":true,"state":"read","path":"{{_room.PathName}}","bytesReceived":10,"bytesSent":2000},
              {"id":"aaaaaaaa-1111-2222-3333-444455556666","created":"2026-10-04T10:16:00Z","remoteAddr":"127.0.0.1:50000",
               "state":"publish","path":"{{_room.PathName}}"},
              {"id":"bbbbbbbb-1111-2222-3333-444455556666","created":"2026-10-04T10:17:00Z","remoteAddr":"10.0.0.9:40000",
               "state":"read","path":"someotherpath"}
            ]}
            """;

        var viewers = MediaMtxServer.ParseWebRtcViewers(json, _room.PathName);
        var viewer = Assert.Single(viewers);
        Assert.Equal("Video viewer", viewer.Role);
        Assert.Equal("192.168.1.20:53000", viewer.Endpoint);
        Assert.Equal(new DateTime(2026, 10, 4, 10, 15, 0, DateTimeKind.Utc), viewer.ConnectedAt.ToUniversalTime());

        Assert.Empty(MediaMtxServer.ParseWebRtcViewers("not json", _room.PathName));
        Assert.Empty(MediaMtxServer.ParseWebRtcViewers("{\"items\":{}}", _room.PathName));
    }

    // -- N1: untrusted networks -------------------------------------------------

    [Fact]
    public async Task UntrustedNetwork_RefusesRoom_UnlessAllowedForSession()
    {
        var log = new LogService();
        var input = new RemoteInputService(log);
        using var service = new ShareScreenService(
            log,
            new FriendsHubTests.FakeToolProvider(),
            new FriendsHubTests.FakeDeviceProvider(),
            new ShareEncoderProbe(log),
            new MediaMtxServer(log),
            new ShareEncodePipeline(log),
            new ShareAudioPump(log),
            new ShareViewerHost(log, input),
            _ => new NetworkClassificationResult
            {
                Category = NetworkSecurityCategory.Public,
                IsPublicOrUnclassified = true,
                NetworkName = "Cafe",
                WarningMessage = "Public"
            });

        var settings = new ShareScreenSettings { ViewerHostOverride = "127.0.0.1", ViewerPort = 19300 };
        var refused = await service.StartRoomAsync(settings, CancellationToken.None);
        Assert.Equal(ShareSessionState.Idle, refused.State);
        Assert.True(refused.UntrustedNetworkRefused);
        Assert.Null(refused.Room);

        settings.AllowUntrustedNetwork = true;
        var opened = await service.StartRoomAsync(settings, CancellationToken.None);
        Assert.Equal(ShareSessionState.RoomOpen, opened.State);
        Assert.False(opened.UntrustedNetworkRefused);

        // The override is never saved with the settings.
        Assert.DoesNotContain("AllowUntrustedNetwork", JsonSerializer.Serialize(settings), StringComparison.OrdinalIgnoreCase);
        await service.StopAsync();
    }

    // -- The page and the host agree on every derivation -----------------------

    [Fact]
    public void ViewerPageHmac_MatchesHostDerivation()
    {
        var node = FindNode();
        if (node == null) return; // Node is not installed on this machine; nothing to run the page script with.

        var html = ShareViewerPage.WithScripts();
        var start = html.IndexOf("// AXE-CRYPTO-BEGIN", StringComparison.Ordinal);
        var end = html.IndexOf("// AXE-CRYPTO-END", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        const string token = "00112233445566778899aabbccddeeff";
        const string code = "K7M2Q9XW";
        const string nonce = "AbCdEfGhIjKlMnOpQrStUv";
        const string json = "{\"t\":\"k\",\"c\":65,\"p\":1}";

        var script = html[start..end] + $$"""

            var key = AxeCrypto.controlKey('{{token}}', '{{code}}');
            var fk = AxeCrypto.frameKey(key, '{{nonce}}');
            console.log(AxeCrypto.hex(key));
            console.log(AxeCrypto.proof(key, 'control', '{{nonce}}'));
            console.log(AxeCrypto.sealFrame(fk, 7, '{{json}}'));
            console.log(AxeCrypto.hex(AxeCrypto.authKey('{{token}}')));
            """;
        var path = Path.Combine(Path.GetTempPath(), "axe-viewer-crypto-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, script);
        try
        {
            var psi = new ProcessStartInfo(node, $"\"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            process.WaitForExit(10000);

            var key = ShareScreenCrypto.DeriveControlKey(token, code);
            Assert.Equal(Convert.ToHexString(key).ToLowerInvariant(), output[0]);
            Assert.Equal(ShareScreenCrypto.ComputeProof(key, "control", nonce), output[1]);
            // GCM with a sequence nonce is deterministic, so the page's sealed frame must match byte for byte.
            Assert.Equal(ShareScreenCrypto.SealControlFrame(ShareScreenCrypto.DeriveControlFrameKey(key, nonce), 7, json), output[2]);
            Assert.Equal(Convert.ToHexString(ShareScreenCrypto.DeriveAuthKey(token)).ToLowerInvariant(), output[3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindNode()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public void Dispose()
    {
        _host.Dispose();
        _input.RevokeAll();
    }
}
