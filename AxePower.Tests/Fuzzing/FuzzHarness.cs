using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// The fuzz suites share one collection that never runs alongside other tests, so the
/// allocation measurements below see only the parser under test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FuzzCollection
{
    public const string Name = "Fuzz";
}

/// <summary>
/// Runs a property over many inputs with a hard time limit and an allocation limit per input.
///
/// Each input runs on its own thread. If it throws something the target does not allow, overruns
/// its time, or allocates past its budget, the input is shrunk (delta debugging) to a minimal one
/// that still fails, written to <see cref="ArtifactDirectory"/>, and the test fails naming the
/// file. Copy that file into <c>Fuzzing/Regressions/&lt;target&gt;/</c> when fixing the bug, and the
/// replay test keeps it fixed.
/// </summary>
internal static class FuzzHarness
{
    public static readonly string ArtifactDirectory = Path.Combine(Path.GetTempPath(), "AxePower_FuzzArtifacts");

    /// <param name="ProcessWide">
    /// Measure every thread's allocations, for async code whose continuations hop threads. Safe
    /// because the fuzz collection never runs alongside other tests.
    /// </param>
    public sealed record Budget(TimeSpan PerInput, Func<int, long> MaxAllocatedBytes, bool ProcessWide = false)
    {
        /// <summary>For synchronous parsers: 1 s, and 64 KB plus 64 bytes per input byte.</summary>
        public static Budget Parser { get; } = new(TimeSpan.FromSeconds(1), length => 64 * 1024 + 64L * length);
    }

    private enum Outcome { Pass, Threw, Hung, OverAllocated }

    public static void Check(
        string target,
        IEnumerable<byte[]> inputs,
        Action<byte[]> property,
        Budget budget,
        params Type[] allowedExceptions)
    {
        foreach (var input in inputs)
        {
            var (outcome, detail) = RunOne(input, property, budget, allowedExceptions);
            if (outcome == Outcome.Pass) continue;

            // Every attempt on a hang costs the full time limit (and leaves a thread spinning), so
            // hangs get only a few shrinking steps.
            var minimal = Minimize(input, candidate => RunOne(candidate, property, budget, allowedExceptions).Outcome == outcome,
                maxAttempts: outcome == Outcome.Hung ? 8 : 400);
            var artifact = SaveArtifact(target, minimal);
            throw new XunitException(
                $"[{target}] {outcome}: {detail}\n" +
                $"Minimized input ({minimal.Length} bytes, from {input.Length}) saved to {artifact}\n" +
                $"Hex: {Convert.ToHexString(minimal.Take(256).ToArray())}{(minimal.Length > 256 ? "…" : string.Empty)}");
        }
    }

    private static (Outcome Outcome, string Detail) RunOne(byte[] input, Action<byte[]> property, Budget budget, Type[] allowed)
    {
        Exception? failure = null;
        long allocated = 0;
        var thread = new Thread(() =>
        {
            long Measure() => budget.ProcessWide ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();
            var before = Measure();
            try
            {
                property(input);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            allocated = Measure() - before;
        }, maxStackSize: 4 * 1024 * 1024) { IsBackground = true };

        thread.Start();
        if (!thread.Join(budget.PerInput))
        {
            return (Outcome.Hung, $"did not finish within {budget.PerInput.TotalMilliseconds:F0} ms");
        }

        if (failure != null && !allowed.Any(t => t.IsInstanceOfType(failure)))
        {
            return (Outcome.Threw, failure.GetType().Name + ": " + failure.Message);
        }

        var limit = budget.MaxAllocatedBytes(input.Length);
        return allocated > limit
            ? (Outcome.OverAllocated, $"allocated {allocated:N0} bytes, budget {limit:N0}")
            : (Outcome.Pass, string.Empty);
    }

    /// <summary>ddmin: remove ever smaller chunks while the failure persists. Bounded so a hang shrinks in finite time.</summary>
    internal static byte[] Minimize(byte[] input, Func<byte[], bool> stillFails, int maxAttempts = 400)
    {
        var current = input;
        var granularity = 2;
        var attempts = 0;
        while (current.Length >= 2 && attempts < maxAttempts)
        {
            var chunk = (int)Math.Ceiling(current.Length / (double)granularity);
            var reduced = false;
            for (var start = 0; start < current.Length && attempts < maxAttempts; start += chunk)
            {
                var candidate = current.Take(start).Concat(current.Skip(start + chunk)).ToArray();
                attempts++;
                if (candidate.Length < current.Length && stillFails(candidate))
                {
                    current = candidate;
                    granularity = Math.Max(granularity - 1, 2);
                    reduced = true;
                    break;
                }
            }
            if (!reduced)
            {
                if (granularity >= current.Length) break;
                granularity = Math.Min(granularity * 2, current.Length);
            }
        }
        return current;
    }

    private static string SaveArtifact(string target, byte[] input)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        var name = $"{target}-{Convert.ToHexString(SHA256.HashData(input))[..12].ToLowerInvariant()}.bin";
        var path = Path.Combine(ArtifactDirectory, name);
        File.WriteAllBytes(path, input);
        return path;
    }

    // -- Regression fixtures ---------------------------------------------------

    public static string RegressionDirectory(string target)
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "AxePower.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
        return Path.Combine(dir, "AxePower.Tests", "Fuzzing", "Regressions", target);
    }

    /// <summary>
    /// Every saved minimized input for <paramref name="target"/>. Inputs that must stay large to
    /// reproduce (a hang that needs megabytes) are stored gzipped as <c>*.bin.gz</c>.
    /// </summary>
    public static IReadOnlyList<byte[]> RegressionInputs(string target)
    {
        var dir = RegressionDirectory(target);
        if (!Directory.Exists(dir)) return Array.Empty<byte[]>();
        return Directory.GetFiles(dir)
            .Where(f => f.EndsWith(".bin", StringComparison.Ordinal) || f.EndsWith(".bin.gz", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f =>
            {
                if (!f.EndsWith(".gz", StringComparison.Ordinal)) return File.ReadAllBytes(f);
                using var gzip = new System.IO.Compression.GZipStream(File.OpenRead(f), System.IO.Compression.CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                gzip.CopyTo(buffer);
                return buffer.ToArray();
            })
            .ToList();
    }
}

/// <summary>
/// Deterministic structure-aware mutation: start from valid seeds and apply a few edits that
/// tend to break parsers (boundary integers, CR/LF and delimiter injection, truncation,
/// duplication, invalid UTF-8, bit flips). The same seed always yields the same corpus.
/// </summary>
internal static class Mutator
{
    private static readonly byte[][] Tokens =
    {
        "\r\n"u8.ToArray(), "\r\n\r\n"u8.ToArray(), "\n"u8.ToArray(), "\r"u8.ToArray(), ":"u8.ToArray(), " "u8.ToArray(),
        "="u8.ToArray(), "&"u8.ToArray(), "?"u8.ToArray(), "%"u8.ToArray(), "%zz"u8.ToArray(), "%00"u8.ToArray(), "%2e%2e%2f"u8.ToArray(),
        "-1"u8.ToArray(), "0"u8.ToArray(), "2147483647"u8.ToArray(), "2147483648"u8.ToArray(), "4294967296"u8.ToArray(),
        "9223372036854775807"u8.ToArray(), "9223372036854775808"u8.ToArray(), "18446744073709551616"u8.ToArray(),
        "99999999999999999999999999"u8.ToArray(), "1e999"u8.ToArray(), "NaN"u8.ToArray(),
        "Content-Length: "u8.ToArray(), "Content-Length: 5, 6"u8.ToArray(), "Transfer-Encoding: chunked"u8.ToArray(),
        "--"u8.ToArray(), "../"u8.ToArray(), "..\\"u8.ToArray(), "C:\\"u8.ToArray(), "\""u8.ToArray(), ";"u8.ToArray(),
        "filename=\""u8.ToArray(), "filename*=UTF-8''"u8.ToArray(), "CON"u8.ToArray(),
        new byte[] { 0xC0, 0xAF }, new byte[] { 0xE0, 0x80, 0xAF }, new byte[] { 0xFF, 0xFE }, new byte[] { 0xED, 0xA0, 0x80 },
        new byte[] { 0x00 }, new byte[] { 0x7F }, new byte[] { 0x80 }, new byte[] { 0xFF }
    };

    public static IEnumerable<byte[]> Mutants(IReadOnlyList<byte[]> seeds, int count, int seed, int maxLength = 64 * 1024,
        IReadOnlyList<byte[]>? extraTokens = null)
    {
        var rng = new Random(seed);
        var tokens = extraTokens == null ? Tokens : Tokens.Concat(extraTokens).ToArray();
        foreach (var s in seeds) yield return s;
        for (var i = 0; i < count; i++)
        {
            var data = new List<byte>(seeds[rng.Next(seeds.Count)]);
            var edits = 1 + rng.Next(6);
            for (var e = 0; e < edits; e++) Apply(data, rng, tokens);
            if (data.Count > maxLength) data.RemoveRange(maxLength, data.Count - maxLength);
            yield return data.ToArray();
        }
    }

    private static void Apply(List<byte> data, Random rng, byte[][] tokens)
    {
        int Pos() => data.Count == 0 ? 0 : rng.Next(data.Count + 1);
        switch (rng.Next(9))
        {
            case 0: // flip a bit
                if (data.Count > 0) { var i = rng.Next(data.Count); data[i] ^= (byte)(1 << rng.Next(8)); }
                break;
            case 1: // insert a token
                data.InsertRange(Pos(), tokens[rng.Next(tokens.Length)]);
                break;
            case 2: // overwrite with a token
                if (data.Count > 0)
                {
                    var t = tokens[rng.Next(tokens.Length)];
                    var at = rng.Next(data.Count);
                    for (var k = 0; k < t.Length && at + k < data.Count; k++) data[at + k] = t[k];
                }
                break;
            case 3: // delete a range
                if (data.Count > 0) { var at = rng.Next(data.Count); data.RemoveRange(at, Math.Min(data.Count - at, 1 + rng.Next(32))); }
                break;
            case 4: // truncate
                if (data.Count > 0) { var keep = rng.Next(data.Count); data.RemoveRange(keep, data.Count - keep); }
                break;
            case 5: // duplicate a range, sometimes many times (repeated headers, excessive fields)
                if (data.Count > 0)
                {
                    var at = rng.Next(data.Count);
                    var len = Math.Min(data.Count - at, 1 + rng.Next(64));
                    var slice = data.GetRange(at, len);
                    var times = rng.Next(4) == 0 ? 50 + rng.Next(400) : 1;
                    for (var k = 0; k < times; k++) data.InsertRange(at, slice);
                }
                break;
            case 6: // random bytes
                {
                    var buf = new byte[1 + rng.Next(16)];
                    rng.NextBytes(buf);
                    data.InsertRange(Pos(), buf);
                }
                break;
            case 7: // swap two bytes
                if (data.Count > 1) { var a = rng.Next(data.Count); var b = rng.Next(data.Count); (data[a], data[b]) = (data[b], data[a]); }
                break;
            default: // set a byte to an interesting value
                if (data.Count > 0) data[rng.Next(data.Count)] = new byte[] { 0, 0xFF, 0x7F, 0x80, (byte)'\r', (byte)'\n', (byte)'-', (byte)'"' }[rng.Next(8)];
                break;
        }
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
