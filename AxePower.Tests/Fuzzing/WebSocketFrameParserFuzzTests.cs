using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Deterministic fuzz and property tests for custom WebSocket frame parsing (ShareViewerHost.ReadFrameAsync).
/// Tests truncated headers, 64-bit integer overflow boundaries, oversized payloads, opcode boundaries,
/// unmasking integrity, and random mutated byte streams.
/// </summary>
public class WebSocketFrameParserFuzzTests
{
    private const int MaxPayload = 8192;

    [Fact]
    public async Task TruncatedFrameHeaders_ReturnNullSafely()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scratch = new byte[1024];

        var truncatedCases = new byte[][]
        {
            Array.Empty<byte>(),                                          // 0 bytes
            new byte[] { 0x81 },                                          // 1 byte only (needs 2)
            new byte[] { 0x81, 126 },                                     // 126 length needs 2 more bytes
            new byte[] { 0x81, 126, 0x00 },                               // 126 length with only 1 ext byte
            new byte[] { 0x81, 127, 0x00, 0x00, 0x00, 0x00 },             // 127 length with only 4 ext bytes
            new byte[] { 0x81, 0x85 },                                    // Masked 5 bytes, but 0 mask bytes follow
            new byte[] { 0x81, 0x85, 0x01, 0x02 },                        // Masked 5 bytes, but only 2 mask bytes follow
            new byte[] { 0x81, 0x05, 0x41, 0x42 }                         // Declared 5 bytes unmasked, only 2 payload bytes
        };

        foreach (var truncated in truncatedCases)
        {
            using var ms = new MemoryStream(truncated);
            var frame = await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
            Assert.Null(frame);
        }
    }

    [Fact]
    public async Task IntegerBoundaryAndOverflow_ReturnsNullSafely()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scratch = new byte[1024];

        // 1. Bit 63 set (negative long in signed 64-bit integer)
        var negativeInt64Frame = new byte[]
        {
            0x81, 127,
            0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
        };
        using (var ms = new MemoryStream(negativeInt64Frame))
        {
            var frame = await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
            Assert.Null(frame);
        }

        // 2. Length exceeds maxPayloadBytes (e.g. 64 KB when max is 8 KB)
        var oversizedFrame = new byte[]
        {
            0x81, 126,
            0x40, 0x00 // 16,384 bytes
        };
        using (var ms = new MemoryStream(oversizedFrame))
        {
            var frame = await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
            Assert.Null(frame);
        }

        // 3. Huge length (e.g. long.MaxValue)
        var maxInt64Frame = new byte[]
        {
            0x81, 127,
            0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
        };
        using (var ms = new MemoryStream(maxInt64Frame))
        {
            var frame = await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
            Assert.Null(frame);
        }
    }

    [Fact]
    public async Task ValidMaskedAndUnmaskedFrames_ParseCorrectly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scratch = new byte[1024];

        // Masked text frame: "Hello"
        var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var plain = "Hello"u8.ToArray();
        var maskedPayload = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++) maskedPayload[i] = (byte)(plain[i] ^ mask[i % 4]);

        var frameBytes = new byte[2 + 4 + plain.Length];
        frameBytes[0] = 0x81; // FIN + text opcode 0x1
        frameBytes[1] = (byte)(0x80 | plain.Length); // Mask bit + 5 bytes length
        Buffer.BlockCopy(mask, 0, frameBytes, 2, 4);
        Buffer.BlockCopy(maskedPayload, 0, frameBytes, 6, plain.Length);

        using (var ms = new MemoryStream(frameBytes))
        {
            var frame = await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
            Assert.NotNull(frame);
            Assert.Equal(0x1, frame.Value.Opcode);
            Assert.Equal("Hello", System.Text.Encoding.UTF8.GetString(frame.Value.Payload));
        }

        // Zero-length ping frame
        var pingFrame = new byte[] { 0x89, 0x00 };
        using (var ms = new MemoryStream(pingFrame))
        {
            var frame = await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
            Assert.NotNull(frame);
            Assert.Equal(0x9, frame.Value.Opcode);
            Assert.Empty(frame.Value.Payload);
        }
    }

    [Fact]
    public async Task RandomByteStreamFuzz_NeverCrashesOrHangs()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scratch = new byte[1024];

        foreach (var randomBytes in FuzzTestCorpus.GenerateMutatedByteSequences(count: 200, maxLength: 2048))
        {
            cts.Token.ThrowIfCancellationRequested();

            using var ms = new MemoryStream(randomBytes);
            // Must return either a valid frame or null, never throw unhandled exception or hang
            await ShareViewerHost.ReadFrameAsync(ms, scratch, MaxPayload, cts.Token);
        }
    }
}
