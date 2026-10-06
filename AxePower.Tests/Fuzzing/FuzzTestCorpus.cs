using System;
using System.Collections.Generic;
using System.Text;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Deterministic corpus generator and minimized regression fixtures for network parser fuzzing.
/// Uses fixed-seed pseudo-random generators to guarantee 100% reproducible test executions.
/// </summary>
public static class FuzzTestCorpus
{
    public const int DeterministicSeed = 1337;

    /// <summary>
    /// Minimized regression inputs covering discovered issues and critical edge cases.
    /// </summary>
    public static readonly IReadOnlyList<string> MinimizedQueryStringRegressions = new[]
    {
        // Malformed percent-encoding triggers UriFormatException in unhardened Uri.UnescapeDataString
        "code=%",
        "code=%Z",
        "code=%ZZ",
        "code=%1",
        "code=%%%&foo=bar",
        "code=%u0000",
        "&=&=&",
        "key==value",
        "===",
        "?&&&",
        "key=value%20with%20spaces&empty=&noequal",
        // Repeated keys
        "token=first&token=second&token=third",
        // High count of parameters
        string.Join("&", GeneratePairs(300))
    };

    /// <summary>
    /// Minimized regression inputs for HTTP request line and headers.
    /// </summary>
    public static readonly IReadOnlyList<string> MinimizedHttpHeaderRegressions = new[]
    {
        // Contradictory Content-Length headers
        "POST /api/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nContent-Length: 20\r\n\r\n",
        "POST /api/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10, 20\r\n\r\n",
        // Malformed / negative Content-Length
        "POST /api/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: -50\r\n\r\n",
        "POST /api/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 999999999999999999999999999999999\r\n\r\n",
        "POST /api/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: invalid_number\r\n\r\n",
        // Malformed request lines
        "GARBAGE_NO_SPACES\r\nHost: localhost\r\n\r\n",
        "GET\r\nHost: localhost\r\n\r\n",
        "GET / HTTP/1.1\r\nNoColonHeader\r\n\r\n",
        ":LeadingColon\r\n\r\n",
        // Repeated headers
        "GET / HTTP/1.1\r\nHost: first\r\nHost: second\r\nAuthorization: Bearer 1\r\nAuthorization: Bearer 2\r\n\r\n"
    };

    /// <summary>
    /// Minimized regression inputs for control JSON events.
    /// </summary>
    public static readonly IReadOnlyList<string> MinimizedControlEventRegressions = new[]
    {
        // Number overflow (exceeds Int32 / Int64)
        "{\"t\":\"k\",\"c\":1000000000000000000000000000000,\"p\":1}",
        "{\"t\":\"k\",\"c\":-9999999999999999999999999999999,\"p\":1}",
        "{\"t\":\"m\",\"x\":1e999,\"y\":0}",
        // Type confusion
        "{\"t\":\"k\",\"c\":\"not_an_int\",\"p\":1}",
        "{\"t\":\"k\",\"c\":[1,2,3],\"p\":1}",
        "{\"t\":\"m\",\"x\":null,\"y\":null}",
        "{\"t\":\"d\",\"b\":\"button_string\"}",
        // Malformed JSON syntax
        "{",
        "{\"t\":",
        "{\"t\":\"k\",",
        "",
        "NOT_JSON"
    };

    /// <summary>
    /// Generates invalid UTF-8 byte sequences (overlong encodings, invalid start bytes, orphaned trail bytes).
    /// </summary>
    public static byte[][] GenerateInvalidUtf8Corpus()
    {
        return new[]
        {
            new byte[] { 0xFF, 0xFE },                         // BOM-like or invalid start bytes
            new byte[] { 0xC0, 0xAF },                         // Overlong slash
            new byte[] { 0xE0, 0x80, 0xAF },                   // Overlong 3-byte
            new byte[] { 0xF0, 0x80, 0x80, 0xAF },             // Overlong 4-byte
            new byte[] { 0x80, 0x81, 0x82 },                   // Unexpected continuation bytes
            new byte[] { 0xC2 },                               // Truncated 2-byte sequence
            new byte[] { 0xE0, 0xA0 },                         // Truncated 3-byte sequence
            new byte[] { 0xF0, 0x90, 0x80 },                   // Truncated 4-byte sequence
            new byte[] { 0xF5, 0x80, 0x80, 0x80 }              // Out of Unicode range (> 0x10FFFF)
        };
    }

    /// <summary>
    /// Generates deterministic pseudo-random mutated byte arrays.
    /// </summary>
    public static IEnumerable<byte[]> GenerateMutatedByteSequences(int count, int maxLength, int seed = DeterministicSeed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int length = rng.Next(0, maxLength);
            var buffer = new byte[length];
            rng.NextBytes(buffer);
            yield return buffer;
        }
    }

    private static IEnumerable<string> GeneratePairs(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return $"key{i}=value{i}";
        }
    }
}
