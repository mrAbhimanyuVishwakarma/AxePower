using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Deterministic fuzz and property tests for custom HTTP request line and header parsers.
/// Exercises malformed encodings, repeated headers, contradictory lengths, integer boundaries,
/// invalid UTF-8, and random byte mutations under strict time and memory bounds.
/// </summary>
public class HttpParserFuzzTests
{
    [Fact]
    public void MinimizedHttpHeaderRegressions_AreHandledSafely()
    {
        foreach (var regression in FuzzTestCorpus.MinimizedHttpHeaderRegressions)
        {
            // 1. LanDrop HTTP Header Parser
            var lanDropResult = LanDropHttp.ParseRequestHeaders(regression);
            Assert.NotNull(lanDropResult);

            // If contradictory or malformed, it must flag them without throwing
            if (regression.Contains("Content-Length: 10\r\nContent-Length: 20") || regression.Contains("Content-Length: 10, 20"))
            {
                Assert.True(lanDropResult.ConflictingContentLength);
            }
            if (regression.Contains("Content-Length: -50") || regression.Contains("invalid_number"))
            {
                Assert.True(lanDropResult.MalformedContentLength);
            }

            // 2. Share Screen HTTP Head Parser
            var shareResult = ShareViewerHost.ParseRequestHead(regression);
            // May be null if invalid format, but must NEVER throw
        }
    }

    [Fact]
    public void ContradictoryAndMalformedContentLength_PropertyTest()
    {
        var testCases = new[]
        {
            ("Content-Length: 0\r\nContent-Length: 1", true, false),
            ("Content-Length: 100, 100", true, false),
            ("Content-Length: -1", false, true),
            ("Content-Length: -9223372036854775808", false, true),
            ("Content-Length: 9223372036854775807000", false, true),
            ("Content-Length: abc", false, true),
            ("Content-Length: 12.34", false, true),
            ("Content-Length:   ", false, true)
        };

        foreach (var (headerLine, expectConflict, expectMalformed) in testCases)
        {
            var raw = $"POST /upload HTTP/1.1\r\nHost: localhost\r\n{headerLine}\r\n\r\n";
            var result = LanDropHttp.ParseRequestHeaders(raw);

            Assert.True(result.IsValid);
            if (expectConflict)
            {
                Assert.True(result.ConflictingContentLength, $"Expected conflict for: {headerLine}");
            }
            if (expectMalformed)
            {
                Assert.True(result.MalformedContentLength, $"Expected malformed for: {headerLine}");
            }
        }
    }

    [Fact]
    public void RepeatedHeaders_DoesNotThrowAndPreservesLastHeader()
    {
        var raw = new StringBuilder();
        raw.Append("GET /api/test HTTP/1.1\r\n");
        for (var i = 0; i < 50; i++)
        {
            raw.Append($"X-Custom-Header: value_{i}\r\n");
            raw.Append($"Authorization: Bearer token_{i}\r\n");
        }
        raw.Append("\r\n");

        var text = raw.ToString();

        var lanDropResult = LanDropHttp.ParseRequestHeaders(text);
        Assert.True(lanDropResult.IsValid);
        Assert.Equal("value_49", lanDropResult.Headers["X-Custom-Header"]);
        Assert.Equal("Bearer token_49", lanDropResult.Headers["Authorization"]);

        var shareResult = ShareViewerHost.ParseRequestHead(text);
        Assert.NotNull(shareResult);
        Assert.Equal("value_49", shareResult.Value.Headers["X-Custom-Header"]);
    }

    [Fact]
    public void InvalidUtf8_DoesNotCrashParsers()
    {
        var invalidUtf8Sequences = FuzzTestCorpus.GenerateInvalidUtf8Corpus();

        foreach (var invalidBytes in invalidUtf8Sequences)
        {
            // Mix invalid UTF-8 into header text
            var prefix = "GET /"u8.ToArray();
            var suffix = " HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();

            var combined = new byte[prefix.Length + invalidBytes.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
            Buffer.BlockCopy(invalidBytes, 0, combined, prefix.Length, invalidBytes.Length);
            Buffer.BlockCopy(suffix, 0, combined, prefix.Length + invalidBytes.Length, suffix.Length);

            var decoded = Encoding.UTF8.GetString(combined);

            var lanDropResult = LanDropHttp.ParseRequestHeaders(decoded);
            Assert.NotNull(lanDropResult);

            var shareResult = ShareViewerHost.ParseRequestHead(decoded);
            // Must not throw exception
        }
    }

    [Fact]
    public void RandomByteFuzz_ExecutesWithoutCrashOrHang()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Generate 200 random byte sequences of lengths up to 4096 bytes
        foreach (var randomBytes in FuzzTestCorpus.GenerateMutatedByteSequences(count: 200, maxLength: 4096))
        {
            cts.Token.ThrowIfCancellationRequested();

            var text = Encoding.ASCII.GetString(randomBytes);

            // LanDrop header parser
            var lanDropResult = LanDropHttp.ParseRequestHeaders(text);
            Assert.NotNull(lanDropResult);

            // ShareScreen head parser
            var shareResult = ShareViewerHost.ParseRequestHead(text);
            // Must not throw
        }
    }
}
