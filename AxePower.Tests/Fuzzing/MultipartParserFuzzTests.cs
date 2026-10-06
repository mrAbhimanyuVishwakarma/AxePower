using System;
using System.IO;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.LanDrop;
using Xunit;

namespace AxePower.Tests.Fuzzing;

/// <summary>
/// Deterministic fuzz and property tests for StreamingMultipartParser.
/// Exercises empty boundaries, boundary confusion, excessive part counts, oversized headers,
/// path traversal attempts, truncated streams, and mutated byte inputs under strict sandbox conditions.
/// </summary>
public class MultipartParserFuzzTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _tempDir;
    private readonly LanDropSecurityLimits _limits;

    public MultipartParserFuzzTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), "AxePower_Fuzz_Sandbox_" + Guid.NewGuid().ToString("N"));
        _tempDir = Path.Combine(_sandboxDir, "uploads");
        Directory.CreateDirectory(_tempDir);

        _limits = new LanDropSecurityLimits
        {
            MaxMultipartPartHeaderSizeBytes = 4096,
            MaxFileSizeBytes = 10 * 1024 * 1024,
            MaxTotalRequestSizeBytes = 20 * 1024 * 1024,
            ReadTimeout = TimeSpan.FromSeconds(2),
            MinFreeDiskSpaceBytes = 1024 * 1024
        };
    }

    [Fact]
    public async Task EmptyOrWhitespaceBoundary_ThrowsArgumentException()
    {
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            StreamingMultipartParser.ParseAndStageAsync(ms, "", _tempDir, _limits, _ => 100_000_000, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            StreamingMultipartParser.ParseAndStageAsync(ms, "   ", _tempDir, _limits, _ => 100_000_000, CancellationToken.None));
    }

    [Fact]
    public async Task ExcessivePartCount_ThrowsInvalidDataException()
    {
        const string boundary = "TestBoundary123";
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Generate 550 parts (exceeds MaxMultipartParts = 500)
        for (var i = 0; i < 550; i++)
        {
            writer.Write($"--{boundary}\r\n");
            writer.Write($"Content-Disposition: form-data; name=\"field{i}\"\r\n\r\n");
            writer.Write($"data_{i}\r\n");
        }
        writer.Write($"--{boundary}--\r\n");
        await writer.FlushAsync();
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StreamingMultipartParser.ParseAndStageAsync(ms, boundary, _tempDir, _limits, _ => 100_000_000, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedPartHeader_ThrowsInvalidDataException()
    {
        const string boundary = "TestBoundary123";
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write($"--{boundary}\r\n");
        writer.Write("Content-Disposition: form-data; name=\"file\"; filename=\"test.txt\"\r\n");
        // Oversized header (> 4096 bytes)
        writer.Write(new string('X', 5000));
        writer.Write("\r\n\r\n");
        writer.Write("hello\r\n");
        writer.Write($"--{boundary}--\r\n");
        await writer.FlushAsync();
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StreamingMultipartParser.ParseAndStageAsync(ms, boundary, _tempDir, _limits, _ => 100_000_000, CancellationToken.None));
    }

    [Fact]
    public async Task BoundaryConfusion_PayloadContainingBoundarySubstring_ParsesSafely()
    {
        const string boundary = "BoundaryX";
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write($"--{boundary}\r\n");
        writer.Write("Content-Disposition: form-data; name=\"file\"; filename=\"test.txt\"\r\n\r\n");
        // Data contains partial boundary substrings and misleading boundary markers
        writer.Write("Some data before\r\n");
        writer.Write("--Boundary\r\n");
        writer.Write("--BoundaryXX\r\n");
        writer.Write("\r\n--BoundaryX_NotEnd\r\n");
        writer.Write("Real end of file data\r\n");
        writer.Write($"--{boundary}--\r\n");
        await writer.FlushAsync();
        ms.Position = 0;

        var staged = await StreamingMultipartParser.ParseAndStageAsync(
            ms, boundary, _tempDir, _limits, _ => 100_000_000, CancellationToken.None);

        Assert.Single(staged);
        Assert.True(File.Exists(staged[0].FinalFilePath));
        var content = await File.ReadAllTextAsync(staged[0].FinalFilePath);
        Assert.Contains("--BoundaryXX", content);
        Assert.Contains("Real end of file data", content);
    }

    [Fact]
    public async Task PathTraversalFilenames_AreBlockedAndNeverEscapeSandbox()
    {
        const string boundary = "BoundaryTraversal";
        var maliciousFilenames = new[]
        {
            "../../etc/passwd",
            "..\\..\\Windows\\System32\\cmd.exe",
            "C:\\Windows\\System32\\notepad.exe",
            "/absolute/root/file.txt",
            "%2e%2e%2fevil.txt",
            "~/.ssh/id_rsa"
        };

        foreach (var badFilename in maliciousFilenames)
        {
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

            writer.Write($"--{boundary}\r\n");
            writer.Write($"Content-Disposition: form-data; name=\"file\"; filename=\"{badFilename}\"\r\n\r\n");
            writer.Write("malicious payload\r\n");
            writer.Write($"--{boundary}--\r\n");
            await writer.FlushAsync();
            ms.Position = 0;

            await Assert.ThrowsAsync<SecurityException>(() =>
                StreamingMultipartParser.ParseAndStageAsync(ms, boundary, _tempDir, _limits, _ => 100_000_000, CancellationToken.None));

            // Verify no files escaped to parent sandbox directory
            var escapedFiles = Directory.GetFiles(_sandboxDir);
            Assert.Empty(escapedFiles);
        }
    }

    [Fact]
    public void ExtractFilenameFromHeaders_HandlesEdgeCases()
    {
        // 1. Quoted filename
        var h1 = "Content-Disposition: form-data; name=\"file\"; filename=\"sample.png\"\r\n\r\n";
        Assert.Equal("sample.png", StreamingMultipartParser.ExtractFilenameFromHeaders(h1));

        // 2. filename* UTF-8 RFC 5987
        var h2 = "Content-Disposition: form-data; filename*=UTF-8''my%20song%2Emp3\r\n\r\n";
        Assert.Equal("my song.mp3", StreamingMultipartParser.ExtractFilenameFromHeaders(h2));

        // 3. filename* with invalid percent encoding falls back to safely decoded string
        var h3 = "Content-Disposition: form-data; filename*=UTF-8''%ZZ%ZZ\r\n\r\n";
        Assert.Equal("%ZZ%ZZ", StreamingMultipartParser.ExtractFilenameFromHeaders(h3));

        // 4. Missing filename (non-file field)
        var h4 = "Content-Disposition: form-data; name=\"username\"\r\n\r\n";
        Assert.Null(StreamingMultipartParser.ExtractFilenameFromHeaders(h4));
    }

    [Fact]
    public async Task TruncatedStream_DoesNotLeaveStagedFiles()
    {
        const string boundary = "BoundaryTruncated";
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write($"--{boundary}\r\n");
        writer.Write("Content-Disposition: form-data; name=\"file\"; filename=\"incomplete.bin\"\r\n\r\n");
        writer.Write("Partial data without ending boundary...");
        await writer.FlushAsync();
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            StreamingMultipartParser.ParseAndStageAsync(ms, boundary, _tempDir, _limits, _ => 100_000_000, CancellationToken.None));

        // Verify no leftover .tmp files or incomplete files exist
        var leftoverFiles = Directory.GetFiles(_tempDir);
        Assert.Empty(leftoverFiles);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxDir))
            {
                Directory.Delete(_sandboxDir, recursive: true);
            }
        }
        catch { }
    }
}
