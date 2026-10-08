using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.ShareScreen;
using Xunit;
using static AxePower.Tests.Fuzzing.Mutator;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Property-based fuzzing of every hand-written parser that reads network input, each under the
/// harness's time and allocation limits. Seeds are valid inputs; mutants are deterministic.
/// None of these touch the disk outside a per-test temp folder, and none can drive real input:
/// the remote-input gate is never opened here.
/// </summary>
[Collection(FuzzCollection.Name)]
public class ParserPropertyFuzzTests : IDisposable
{
    private const int Seed = FuzzTestCorpus.DeterministicSeed;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "AxePower_FuzzParsers_" + Guid.NewGuid().ToString("N"));

    public ParserPropertyFuzzTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    // -- HTTP request heads ----------------------------------------------------

    private static readonly byte[][] HttpSeeds =
    {
        Utf8("GET /api/status?t=abc&id=1 HTTP/1.1\r\nHost: 192.168.1.5:5050\r\nUser-Agent: Android\r\nCookie: axe_session=abc; x=1\r\n\r\n"),
        Utf8("POST /api/upload HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: multipart/form-data; boundary=XyZ\r\nContent-Length: 42\r\n\r\n"),
        Utf8("GET /axe0011223344556677889a/chat?n=AbC&p=00ff HTTP/1.1\r\nHost: 127.0.0.1:18990\r\nOrigin: http://127.0.0.1:18990\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"),
    };

    [Fact]
    public void LanDropRequestHead_Properties()
    {
        FuzzHarness.Check("landrop-http-head", Mutants(HttpSeeds, 3000, Seed), input =>
        {
            var result = LanDropHttp.ParseRequestHeaders(Latin1(input));
            if (!result.IsValid) return;

            // The caller rejects conflicting or malformed lengths before using one, so the only
            // contract that matters is: a length it may use is never negative, and none is
            // invented when the header is absent.
            if (result.HasContentLength && !result.MalformedContentLength && !result.ConflictingContentLength)
                Assert.True(result.ContentLength >= 0);
            if (!result.HasContentLength) Assert.Equal(-1, result.ContentLength);
        }, FuzzHarness.Budget.Parser);
    }

    [Fact]
    public void ShareViewerRequestHead_Properties()
    {
        FuzzHarness.Check("share-http-head", Mutants(HttpSeeds, 3000, Seed + 1), input =>
        {
            var result = ShareViewerHost.ParseRequestHead(Latin1(input));
            // Any method, even an empty one, is answered 405 unless it is GET; not throwing is the contract.
            _ = result;
        }, FuzzHarness.Budget.Parser);
    }

    // -- Query strings and cookies --------------------------------------------

    private static readonly byte[][] QuerySeeds =
    {
        Utf8("t=abc&id=0123&value=%20x"), Utf8("n=AbCdEf_-&p=00112233"), Utf8("code=4/0Ab&state=xyz&scope=email%20openid"),
        Utf8("a=1&a=2&b&=c&d=="), Utf8("")
    };

    [Fact]
    public void QueryParsers_Properties()
    {
        FuzzHarness.Check("query-string", Mutants(QuerySeeds, 4000, Seed + 2), input =>
        {
            var text = Latin1(input);
            var a = LanDropHttp.ParseQueryString(text);
            var b = ShareViewerHost.ParseQuery(text);
            var c = OAuthLoopback.ParseQuery("/callback?" + text);
            Assert.True(a.Count <= LanDropHttp.MaxQueryParameters);
            Assert.True(b.Count <= ShareViewerHost.MaxQueryParameters);
            Assert.True(c.Count <= 200);
        }, // Three parsers per input, each building its own dictionary: still linear in the input.
        new FuzzHarness.Budget(TimeSpan.FromSeconds(1), length => 64 * 1024 + 256L * length));
    }

    [Fact]
    public void SessionCookie_Properties()
    {
        var seeds = new[] { Utf8("axe_session=tok123; other=1"), Utf8("a=b;axe_session=;axe_session=x"), Utf8("axe_session") };
        FuzzHarness.Check("session-cookie", Mutants(seeds, 3000, Seed + 3), input =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = Latin1(input) };
            if (LanDropHttp.TryGetCookie(headers, LanDropService.SessionCookieName, out var value))
            {
                // Plain checks: xUnit's collection asserts box every character and would blow the budget.
                Assert.True(value.Length > 0);
                Assert.True(value.IndexOf(';') < 0);
            }
        }, FuzzHarness.Budget.Parser);
    }

    // -- Share Screen authenticated formats -----------------------------------

    [Fact]
    public void ControlFrames_OnlyGenuineContentIsAccepted()
    {
        var key = ShareScreenCrypto.DeriveControlFrameKey(ShareScreenCrypto.DeriveControlKey("00112233445566778899aabbccddeeff", "ABCDEFGH"), "nonce");
        const string json = "{\"t\":\"r\"}";
        var genuine = ShareScreenCrypto.SealControlFrame(key, 5, json);

        FuzzHarness.Check("control-frame", Mutants(new[] { Utf8(genuine) }, 3000, Seed + 4), input =>
        {
            long last = 0;
            if (ShareViewerHost.TryOpenControlFrame(Encoding.UTF8.GetString(input), key, ref last, out var accepted))
            {
                Assert.Equal(json, accepted);
                Assert.Equal(5, last);
            }
        }, FuzzHarness.Budget.Parser);
    }

    [Fact]
    public void ChatEnvelopes_OnlyGenuineMessagesDecrypt()
    {
        const string token = "00112233445566778899aabbccddeeff";
        var message = new ShareChatMessage("m1", "Alex", "hello there", DateTime.UtcNow);
        var wire = RoomMessageCrypto.Encrypt(message, token, sequence: 9, sender: "0123456789abcdef");

        FuzzHarness.Check("chat-envelope", Mutants(new[] { wire }, 2000, Seed + 5), input =>
        {
            if (RoomMessageCrypto.TryDecrypt(input, token, new ReplayFilter(), out var decoded, out var seq))
            {
                Assert.Equal("hello there", decoded!.Text);
                Assert.Equal(9, seq);
            }
        }, FuzzHarness.Budget.Parser);
    }

    [Fact]
    public void VoicePackets_OnlyGenuinePacketsDecrypt()
    {
        const string token = "00112233445566778899aabbccddeeff";
        var opus = new byte[] { 0xFC, 1, 2, 3, 4, 5, 6, 7 };
        var wire = RoomVoiceCrypto.Encrypt(opus, token, sequence: 3);

        FuzzHarness.Check("voice-packet", Mutants(new[] { wire }, 2000, Seed + 6, maxLength: 8192), input =>
        {
            if (RoomVoiceCrypto.TryDecrypt(input, token, new ReplayFilter(), out var decoded, out var seq))
            {
                Assert.Equal(opus, decoded);
                Assert.Equal(3, seq);
            }
        }, FuzzHarness.Budget.Parser);
    }

    [Fact]
    public void ControlEvents_NeverThrow_IncludingDeepNesting()
    {
        var input = new RemoteInputService(new LogService()); // never allowed: no event can reach the desktop
        using var host = new ShareViewerHost(new LogService(), input);
        var seeds = new[]
        {
            Utf8("{\"t\":\"m\",\"x\":0.5,\"y\":0.25}"), Utf8("{\"t\":\"k\",\"c\":65,\"p\":1}"), Utf8("{\"t\":\"w\",\"d\":-120}"),
            Utf8(new string('[', 5000) + new string(']', 5000)),
            Utf8("{\"t\":\"m\",\"x\":" + new string('[', 2000) + "1" + new string(']', 2000) + "}")
        };
        FuzzHarness.Check("control-event", Mutants(seeds, 3000, Seed + 7), bytes =>
        {
            host.ApplyEvent(Encoding.UTF8.GetString(bytes));
        }, FuzzHarness.Budget.Parser);
        Assert.False(input.IsAccepting);
    }

    [Fact]
    public void MediaMtxSessionList_NeverThrows_AndOnlyReportsTheRoomPath()
    {
        const string path = "axe00112233445566778899";
        var seed = Utf8("{\"items\":[{\"id\":\"5b9e3f2a-1111\",\"created\":\"2026-10-04T10:15:00Z\",\"remoteAddr\":\"192.168.1.20:53000\",\"state\":\"read\",\"path\":\"" + path + "\"}]}");
        FuzzHarness.Check("mediamtx-sessions", Mutants(new[] { seed, Utf8("{\"items\":" + new string('[', 3000) + "}") }, 3000, Seed + 8), input =>
        {
            var viewers = MediaMtxServer.ParseWebRtcViewers(Encoding.UTF8.GetString(input), path);
            Assert.All(viewers, v => Assert.Equal("Video viewer", v.Role));
        }, FuzzHarness.Budget.Parser);
    }

    // -- Upload filenames --------------------------------------------------------

    [Fact]
    public void MultipartFilenameHeader_NeverThrows()
    {
        var seeds = new[]
        {
            Utf8("Content-Disposition: form-data; name=\"file\"; filename=\"photo.jpg\"\r\nContent-Type: image/jpeg"),
            Utf8("Content-Disposition: form-data; filename*=UTF-8''na%C3%AFve%20file.txt"),
            Utf8("Content-Disposition: form-data; name=\"note\"")
        };
        FuzzHarness.Check("multipart-filename-header", Mutants(seeds, 3000, Seed + 9), input =>
        {
            _ = StreamingMultipartParser.ExtractFilenameFromHeaders(Encoding.UTF8.GetString(input));
        }, FuzzHarness.Budget.Parser);
    }

    [Fact]
    public void UploadFilenames_AlwaysNormalizeToASafeNameInsideTheFolder()
    {
        var seeds = new[]
        {
            Utf8("report.pdf"), Utf8("../../Windows/win.ini"), Utf8("CON.txt"), Utf8("a." + new string('x', 400)),
            Utf8(new string('n', 400) + ".txt"), Utf8("name\u0000with\u0001controls.bin"), Utf8(" . . "), Utf8("C:\\evil:stream.exe"),
            Utf8("photo." + new string('é', 300))
        };
        var invalid = Path.GetInvalidFileNameChars();
        FuzzHarness.Check("upload-filename", Mutants(seeds, 3000, Seed + 10), input =>
        {
            var raw = Encoding.UTF8.GetString(input);
            var safe = LanDropPathSanitizer.NormalizeFileName(raw);

            Assert.False(string.IsNullOrWhiteSpace(safe));
            Assert.True(safe.IndexOfAny(invalid) < 0, $"invalid character in '{safe}'");
            Assert.True(safe.Length <= 255, $"{safe.Length}-character file name cannot be created on Windows");

            var full = LanDropPathSanitizer.GetUniqueContainedPath(_sandbox, safe);
            Assert.Equal(Path.GetFullPath(_sandbox), Path.GetDirectoryName(full));
        }, FuzzHarness.Budget.Parser);
    }

    // -- WebSocket frames --------------------------------------------------------

    private static byte[] Frame(int opcode, byte[] payload, bool mask)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        var maskBit = mask ? 0x80 : 0;
        if (payload.Length <= 125) header.Add((byte)(maskBit | payload.Length));
        else if (payload.Length <= 65535) { header.Add((byte)(maskBit | 126)); header.Add((byte)(payload.Length >> 8)); header.Add((byte)payload.Length); }
        else { header.Add((byte)(maskBit | 127)); for (var i = 7; i >= 0; i--) header.Add((byte)((long)payload.Length >> (8 * i))); }
        var key = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 };
        if (mask) header.AddRange(key);
        header.AddRange(mask ? payload.Select((b, i) => (byte)(b ^ key[i % 4])) : payload);
        return header.ToArray();
    }

    [Fact]
    public void WebSocketFrames_Properties()
    {
        const int max = 8192;
        var seeds = new[]
        {
            Frame(0x1, Utf8("1|00112233445566778899aabbccddeeff|{\"t\":\"r\"}"), mask: true),
            Frame(0x2, new byte[300], mask: true),
            Frame(0x2, new byte[max], mask: false),
            Frame(0x9, Array.Empty<byte>(), mask: true),
            Frame(0x8, new byte[] { 0x03, 0xE8 }, mask: true)
        };
        var scratch = new byte[4096];
        FuzzHarness.Check("websocket-frame", Mutants(seeds, 3000, Seed + 11, maxLength: 3 * max), input =>
        {
            using var stream = new MemoryStream(input);
            var frame = ShareViewerHost.ReadFrameAsync(stream, scratch, max, CancellationToken.None).GetAwaiter().GetResult();
            if (frame != null) Assert.True(frame.Value.Payload.Length <= max);
        }, new FuzzHarness.Budget(TimeSpan.FromSeconds(1), length => 64 * 1024 + 4L * length + 2L * max));
    }

    [Fact]
    public async Task WebSocketFrames_RoundTripAtEveryLengthBoundary()
    {
        var scratch = new byte[4096];
        foreach (var length in new[] { 0, 1, 125, 126, 127, 4095, 4096, 4097, 65535, 65536, 70000 })
        {
            foreach (var mask in new[] { true, false })
            {
                var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
                using var stream = new MemoryStream(Frame(0x2, payload, mask));
                var frame = await ShareViewerHost.ReadFrameAsync(stream, scratch, 80_000, CancellationToken.None);
                Assert.NotNull(frame);
                Assert.Equal(payload, frame.Value.Payload);

                using var tooBig = new MemoryStream(Frame(0x2, payload, mask));
                if (length > 0) Assert.Null(await ShareViewerHost.ReadFrameAsync(tooBig, scratch, length - 1, CancellationToken.None));
            }
        }
    }

    // -- Saved regressions ---------------------------------------------------------

    [Fact]
    public void SavedRegressions_StillPass()
    {
        // Each folder under Fuzzing/Regressions replays through the same property as its suite.
        FuzzHarness.Check("upload-filename", FuzzHarness.RegressionInputs("upload-filename"), input =>
        {
            var safe = LanDropPathSanitizer.NormalizeFileName(Encoding.UTF8.GetString(input));
            Assert.True(safe.Length <= 255);
            var full = LanDropPathSanitizer.GetUniqueContainedPath(_sandbox, safe);
            Assert.Equal(Path.GetFullPath(_sandbox), Path.GetDirectoryName(full));
            // Windows accepts the name and stores it exactly as recorded (no silent trimming).
            File.WriteAllBytes(full, Array.Empty<byte>());
            var onDisk = Path.GetFileName(Assert.Single(Directory.GetFiles(_sandbox)));
            File.Delete(Path.Combine(_sandbox, onDisk));
            Assert.Equal(Path.GetFileName(full), onDisk);
        }, FuzzHarness.Budget.Parser with { MaxAllocatedBytes = _ => 1024 * 1024 });

        FuzzHarness.Check("mediamtx-sessions", FuzzHarness.RegressionInputs("mediamtx-sessions"), input =>
        {
            Assert.Empty(MediaMtxServer.ParseWebRtcViewers(Encoding.UTF8.GetString(input), "axe00"));
        }, FuzzHarness.Budget.Parser);

        Assert.NotEmpty(FuzzHarness.RegressionInputs("upload-filename"));
        Assert.NotEmpty(FuzzHarness.RegressionInputs("mediamtx-sessions"));
    }
}
