using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using AxePower.Core.Services.LanDrop;
using Xunit;
using static AxePower.Tests.Fuzzing.Mutator;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Fuzzes the streaming multipart parser end to end, writing only inside a per-test sandbox.
/// An input is <c>boundary</c>, a newline, then the body, so mutation reaches the boundary as
/// well as the content. Properties: it finishes in time and within memory, it either stages
/// files or refuses with one of its documented exceptions, every staged file is inside the
/// upload folder and within the size limit, and no staging file is ever left behind.
/// </summary>
[Collection(FuzzCollection.Name)]
public class MultipartStagingFuzzTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "AxePower_FuzzMultipart_" + Guid.NewGuid().ToString("N"));
    private readonly string _uploads;

    private readonly LanDropSecurityLimits _limits = new()
    {
        MaxMultipartPartHeaderSizeBytes = 4096,
        MaxFileSizeBytes = 256 * 1024,
        MaxTotalRequestSizeBytes = 512 * 1024,
        MaxTextMessageSizeBytes = 16 * 1024,
        MinFreeDiskSpaceBytes = 1024,
        ReadTimeout = TimeSpan.FromSeconds(1)
    };

    private static readonly Type[] Refusals = { typeof(InvalidDataException), typeof(SecurityException), typeof(IOException), typeof(ArgumentException) };

    public MultipartStagingFuzzTests()
    {
        _uploads = Path.Combine(_sandbox, "uploads");
        Directory.CreateDirectory(_uploads);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private static byte[] Input(string boundary, string body) => Utf8(boundary + "\n" + body);

    private static string Body(string b, params (string Headers, string Content)[] parts) =>
        "preamble\r\n" + string.Concat(parts.Select(p => $"--{b}\r\n{p.Headers}\r\n\r\n{p.Content}\r\n")) + $"--{b}--\r\n";

    private static readonly byte[][] Seeds =
    {
        Input("XyZ", Body("XyZ", ("Content-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\nContent-Type: text/plain", "hello world"))),
        Input("----WebKitFormBoundary7MA4YWxkTrZu0gW", Body("----WebKitFormBoundary7MA4YWxkTrZu0gW",
            ("Content-Disposition: form-data; name=\"note\"", "just a field"),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"photo.jpg\"", new string('J', 3000)),
            ("Content-Disposition: form-data; name=\"file\"; filename*=UTF-8''na%C3%AFve.txt", "second"))),
        Input("a", Body("a", ("Content-Disposition: form-data; filename=\"x.bin\"", "--a-not-a-boundary\r\n--ab\r\n-- a"))),
        Input(new string('B', 70), Body(new string('B', 70), ("Content-Disposition: form-data; filename=\"empty.dat\"", ""))),
    };

    private void ResetUploads()
    {
        foreach (var f in Directory.GetFiles(_uploads)) File.Delete(f);
    }

    private void Property(byte[] input)
    {
        ResetUploads();
        var newline = Array.IndexOf(input, (byte)'\n');
        var boundary = Encoding.Latin1.GetString(input, 0, Math.Max(newline, 0));
        var body = newline < 0 ? Array.Empty<byte>() : input[(newline + 1)..];

        List<StagedUploadResult>? staged = null;
        try
        {
            using var stream = new MemoryStream(body);
            staged = StreamingMultipartParser.ParseAndStageAsync(
                stream, boundary, _uploads, _limits, _ => long.MaxValue, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            // Whatever happened, nothing is staged half-way and nothing escaped the upload folder.
            Assert.Empty(Directory.GetFiles(_uploads, ".landrop_*.tmp"));
            Assert.Equal(new[] { _uploads }, Directory.GetFileSystemEntries(_sandbox));
        }

        Assert.True(staged.Count <= StreamingMultipartParser.MaxMultipartParts);
        Assert.Equal(staged.Count, Directory.GetFiles(_uploads).Length);
        foreach (var file in staged)
        {
            Assert.Equal(Path.GetFullPath(_uploads), Path.GetDirectoryName(file.FinalFilePath));
            Assert.True(File.Exists(file.FinalFilePath));
            Assert.Equal(file.FileSizeBytes, new FileInfo(file.FinalFilePath).Length);
            Assert.True(file.FileSizeBytes <= _limits.MaxFileSizeBytes);
        }
    }

    private static readonly FuzzHarness.Budget MultipartBudget =
        new(TimeSpan.FromSeconds(3), length => 16L * 1024 * 1024 + 64L * length, ProcessWide: true);

    [Fact]
    public void MutatedBodies_StageSafelyOrRefuse()
    {
        FuzzHarness.Check("multipart", Mutants(Seeds, 1500, FuzzTestCorpus.DeterministicSeed + 20, maxLength: 128 * 1024),
            Property, MultipartBudget, Refusals);
    }

    [Fact]
    public void LongBoundaries_DoNotBlowUpTheScan()
    {
        // The boundary comes from the request's Content-Type, which may be up to the header size.
        // A body that keeps almost matching a long boundary is the worst case for a naive scan.
        var seeds = new List<byte[]>();
        // 15,000 characters with 1.5 MB took about 4.6 s before the fix; 70 is the RFC 2046 limit.
        foreach (var length in new[] { 70, 71, 15000 })
        {
            var boundary = new string('-', length);
            var nearMiss = string.Concat(Enumerable.Repeat(new string('-', length + 1) + "x", 1536 * 1024 / (length + 2)));
            seeds.Add(Input(boundary, nearMiss));
            seeds.Add(Input(boundary, $"--{boundary}\r\nContent-Disposition: form-data; filename=\"f.bin\"\r\n\r\n" + nearMiss));
        }
        FuzzHarness.Check("multipart-long-boundary", seeds, Property, MultipartBudget, Refusals);
    }

    [Fact]
    public void SavedRegressions_StillPass()
    {
        FuzzHarness.Check("multipart", FuzzHarness.RegressionInputs("multipart"), Property, MultipartBudget, Refusals);
        FuzzHarness.Check("multipart-long-boundary", FuzzHarness.RegressionInputs("multipart-long-boundary"), Property, MultipartBudget, Refusals);
    }
}
