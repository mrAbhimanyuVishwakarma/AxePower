using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.LanDrop;
using Xunit;
using static AxePower.Tests.Fuzzing.Mutator;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Property-based fuzzing of every hand-written parser that reads network input, each under the
/// harness's time and allocation limits. Seeds are valid inputs; mutants are deterministic.
/// None of these touch the disk outside a per-test temp folder.
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

    // -- Query strings and cookies --------------------------------------------

    private static readonly byte[][] QuerySeeds =
    {
        Utf8("t=abc&id=0123&value=%20x"), Utf8("code=4/0Ab&state=xyz&scope=email%20openid"),
        Utf8("a=1&a=2&b&=c&d=="), Utf8("")
    };

    [Fact]
    public void QueryParsers_Properties()
    {
        FuzzHarness.Check("query-string", Mutants(QuerySeeds, 4000, Seed + 2), input =>
        {
            var text = Latin1(input);
            var a = LanDropHttp.ParseQueryString(text);
            var c = OAuthLoopback.ParseQuery("/callback?" + text);
            Assert.True(a.Count <= LanDropHttp.MaxQueryParameters);
            Assert.True(c.Count <= 200);
        }, // Two parsers per input, each building its own dictionary: still linear in the input.
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

        Assert.NotEmpty(FuzzHarness.RegressionInputs("upload-filename"));
    }
}
