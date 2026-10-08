using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Helpers;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.Music;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// Adversarial security and resilience tests for LanDropService:
/// Oversized headers, huge declared bodies, malformed Content-Length, slow clients,
/// early disconnect, cancellation, path traversal, duplicate names, concurrency throttling,
/// and disk exhaustion.
/// </summary>
public sealed class LanDropSecurityTests
{
    private readonly LogService _logService = new();

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private (LanDropService service, string downloadDir, PortalAccessToken token, int port) CreateTestServer(
        LanDropSecurityLimits? limits = null,
        Func<string, long>? diskSpaceChecker = null)
    {
        var token = new PortalAccessToken(new InMemorySecretStore());
        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_LanDropTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var serverLimits = limits ?? new LanDropSecurityLimits
        {
            ReadTimeout = TimeSpan.FromSeconds(5),
            WriteTimeout = TimeSpan.FromSeconds(5)
        };

        var service = new LanDropService(_logService, token, null, serverLimits, diskSpaceChecker, downloadDirectory: tempDir)
        {
            DownloadDirectory = tempDir
        };

        // Pick an available port dynamically
        using var tempListener = new TcpListener(IPAddress.Loopback, 0);
        tempListener.Start();
        int port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
        tempListener.Stop();

        bool started = service.StartServerAsync("127.0.0.1", port).GetAwaiter().GetResult();
        Assert.True(started, "Test server failed to bind and start.");

        return (service, tempDir, token, port);
    }

    private static async Task<string> SendRawHttpRequestAsync(
        int port,
        string rawRequest,
        int readTimeoutMs = 5000)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        byte[] requestBytes = Encoding.UTF8.GetBytes(rawRequest);
        await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
        await stream.FlushAsync();

        using var cts = new CancellationTokenSource(readTimeoutMs);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];

        try
        {
            while (!cts.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) break;
                ms.Write(buffer, 0, read);

                string current = Encoding.UTF8.GetString(ms.ToArray());
                // Break if header ended and Content-Length satisfied or closed
                if (current.Contains("\r\n\r\n") && (current.Contains("Content-Length: 0") || current.Contains("Connection: close")))
                {
                    // Give a tiny window for remaining body if any
                    await Task.Delay(50);
                    while (stream.DataAvailable)
                    {
                        int extra = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                        if (extra == 0) break;
                        ms.Write(buffer, 0, extra);
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task OversizedHeaders_RejectedWith431()
    {
        var limits = new LanDropSecurityLimits
        {
            MaxHeaderSizeBytes = 4096, // 4 KB limit for test
            ReadTimeout = TimeSpan.FromSeconds(5)
        };
        var (service, dir, token, port) = CreateTestServer(limits);

        try
        {
            string hugeHeader = new string('X', 5000);
            string rawRequest = $"GET /api/status?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"X-Padded: {hugeHeader}\r\n\r\n";

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("431 Request Header Fields Too Large", response);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task HugeDeclaredBody_RejectedEarlyWith413()
    {
        var limits = new LanDropSecurityLimits
        {
            MaxTotalRequestSizeBytes = 100 * 1024, // 100 KB max
            ReadTimeout = TimeSpan.FromSeconds(5)
        };
        var (service, dir, token, port) = CreateTestServer(limits);

        try
        {
            // Declare 50 MB upload body
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: 52428800\r\n" +
                                $"Content-Type: application/octet-stream\r\n\r\n";

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("413 Payload Too Large", response);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task HugeTextMessage_RejectedWith413()
    {
        var limits = new LanDropSecurityLimits
        {
            MaxTextMessageSizeBytes = 1024 // 1 KB max for quick text
        };
        var (service, dir, token, port) = CreateTestServer(limits);

        try
        {
            string rawRequest = $"POST /api/text?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: 2048\r\n\r\n";

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("413 Payload Too Large", response);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("Content-Length: -50\r\n")]
    [InlineData("Content-Length: notanumber\r\n")]
    [InlineData("Content-Length: 10\r\nContent-Length: 20\r\n")]
    [InlineData("Content-Length: 10, 20\r\n")]
    public async Task MalformedOrConflictingContentLength_RejectedWith400(string clHeader)
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                clHeader + "\r\n";

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("400 Bad Request", response);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MissingContentLengthOnPost_RejectedWith411()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            string rawRequest = $"POST /api/text?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n\r\n";

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("411 Length Required", response);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("..\\..\\windows\\system32\\calc.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("file.txt:stream")]
    [InlineData("%2e%2e/traversal.txt")]
    public async Task PathTraversalInFilename_RejectedWith400(string maliciousFilename)
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            byte[] body = "malicious payload"u8.ToArray();
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: {body.Length}\r\n" +
                                $"X-File-Name: {WebUtility.UrlEncode(maliciousFilename)}\r\n\r\n" +
                                Encoding.UTF8.GetString(body);

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("400 Bad Request", response);
            // Verify no files escaped or were written to download directory
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultipartPathTraversal_RejectedWith400()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            string boundary = "----TestBoundary123";
            string partHeader = $"--{boundary}\r\n" +
                                $"Content-Disposition: form-data; name=\"file\"; filename=\"../../boot.ini\"\r\n" +
                                $"Content-Type: text/plain\r\n\r\n" +
                                "fake boot config\r\n" +
                                $"--{boundary}--\r\n";

            byte[] partBytes = Encoding.UTF8.GetBytes(partHeader);

            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: {partBytes.Length}\r\n" +
                                $"Content-Type: multipart/form-data; boundary={boundary}\r\n\r\n" +
                                partHeader;

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("400 Bad Request", response);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DuplicateFilenames_SavedWithoutCollision()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            byte[] file1 = "Version 1"u8.ToArray();
            string req1 = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                          $"Host: 127.0.0.1:{port}\r\n" +
                          $"Content-Length: {file1.Length}\r\n" +
                          $"X-File-Name: notes.txt\r\n\r\n" +
                          Encoding.UTF8.GetString(file1);

            string resp1 = await SendRawHttpRequestAsync(port, req1);
            Assert.Contains("200 OK", resp1);

            byte[] file2 = "Version 2"u8.ToArray();
            string req2 = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                          $"Host: 127.0.0.1:{port}\r\n" +
                          $"Content-Length: {file2.Length}\r\n" +
                          $"X-File-Name: notes.txt\r\n\r\n" +
                          Encoding.UTF8.GetString(file2);

            string resp2 = await SendRawHttpRequestAsync(port, req2);
            Assert.Contains("200 OK", resp2);

            string[] files = Directory.GetFiles(dir);
            Assert.Equal(2, files.Length);

            string path1 = Path.Combine(dir, "notes.txt");
            string path2 = Path.Combine(dir, "notes (1).txt");

            Assert.True(File.Exists(path1));
            Assert.True(File.Exists(path2));
            Assert.Equal("Version 1", File.ReadAllText(path1));
            Assert.Equal("Version 2", File.ReadAllText(path2));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task EarlyDisconnect_CleansUpStagingFile_LeavesNoFinalFiles()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var stream = client.GetStream();

                // Declare 100 KB upload
                string headers = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                 $"Host: 127.0.0.1:{port}\r\n" +
                                 $"Content-Length: 102400\r\n" +
                                 $"X-File-Name: unfinished.bin\r\n\r\n";

                byte[] headerBytes = Encoding.UTF8.GetBytes(headers);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);

                // Send only 512 bytes, then abruptly close socket
                byte[] partial = new byte[512];
                await stream.WriteAsync(partial, 0, partial.Length);
                await stream.FlushAsync();

                // Abrupt disconnect
                client.Close();
            }

            // Allow server to handle disconnect and cleanup
            await Task.Delay(300);

            // Verify no files exist in download directory
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Assert.Empty(files);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CancellationDuringUpload_CleansUpStagingFile()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            var uploadTask = Task.Run(async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var stream = client.GetStream();

                string headers = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                 $"Host: 127.0.0.1:{port}\r\n" +
                                 $"Content-Length: 500000\r\n" +
                                 $"X-File-Name: cancelled.bin\r\n\r\n";

                byte[] hBytes = Encoding.UTF8.GetBytes(headers);
                await stream.WriteAsync(hBytes, 0, hBytes.Length);

                byte[] chunk = new byte[1024];
                for (int i = 0; i < 50; i++)
                {
                    await stream.WriteAsync(chunk, 0, chunk.Length);
                    await Task.Delay(20);
                }
            });

            await Task.Delay(100);
            // Cancel / stop server while upload in progress
            service.StopServer();

            try { await uploadTask; } catch { }
            await Task.Delay(200);

            // Verify staging files were deleted
            string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            Assert.Empty(files);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SlowClient_TimeoutTerminatesConnection()
    {
        var limits = new LanDropSecurityLimits
        {
            ReadTimeout = TimeSpan.FromMilliseconds(300)
        };
        var (service, dir, token, port) = CreateTestServer(limits);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            // Send partial request line without completing headers
            byte[] initial = "GET /api/st"u8.ToArray();
            await stream.WriteAsync(initial, 0, initial.Length);
            await stream.FlushAsync();

            // Wait longer than read timeout without sending more data
            await Task.Delay(600);

            // Attempting to read should result in connection closed (0 bytes read) or socket error
            var buf = new byte[128];
            using var cts = new CancellationTokenSource(1000);
            int read = 0;
            try
            {
                read = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
            }
            catch (IOException) { }

            Assert.Equal(0, read);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ConcurrencyLimit_RejectsExcessConnectionsWith503()
    {
        var limits = new LanDropSecurityLimits
        {
            MaxConcurrentClients = 2,
            ReadTimeout = TimeSpan.FromSeconds(5)
        };
        var (service, dir, token, port) = CreateTestServer(limits);

        try
        {
            // Connect client 1 and hold connection
            using var client1 = new TcpClient();
            await client1.ConnectAsync(IPAddress.Loopback, port);
            using var stream1 = client1.GetStream();
            await stream1.WriteAsync("GET "u8.ToArray());

            // Connect client 2 and hold connection
            using var client2 = new TcpClient();
            await client2.ConnectAsync(IPAddress.Loopback, port);
            using var stream2 = client2.GetStream();
            await stream2.WriteAsync("GET "u8.ToArray());

            await Task.Delay(100);

            // Connect client 3 (exceeds concurrency limit of 2)
            string resp3 = await SendRawHttpRequestAsync(port, $"GET /api/status?t={token.Value} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");

            Assert.Contains("503 Service Unavailable", resp3);
            Assert.Contains("Server concurrency limit reached", resp3);
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DiskExhaustion_RejectsUploadWith507()
    {
        // Simulate a disk with only 5 MB free, whereas MinFreeDiskSpaceBytes is 100 MB
        long simulatedFreeSpace = 5L * 1024 * 1024;
        var limits = new LanDropSecurityLimits
        {
            MinFreeDiskSpaceBytes = 100L * 1024 * 1024
        };
        var (service, dir, token, port) = CreateTestServer(limits, diskSpaceChecker: _ => simulatedFreeSpace);

        try
        {
            byte[] fileData = "Important file data"u8.ToArray();
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: {fileData.Length}\r\n" +
                                $"X-File-Name: document.pdf\r\n\r\n" +
                                Encoding.UTF8.GetString(fileData);

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("507 Insufficient Storage", response);
            Assert.Contains("Insufficient disk space on server", response);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultipartValidUpload_SavesFileCorrectly()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            string boundary = "----WebKitFormBoundaryXYZ";
            string fileContent = "Hello from valid multipart file upload!";
            string body = $"--{boundary}\r\n" +
                          $"Content-Disposition: form-data; name=\"file\"; filename=\"hello.txt\"\r\n" +
                          $"Content-Type: text/plain\r\n\r\n" +
                          $"{fileContent}\r\n" +
                          $"--{boundary}--\r\n";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: {bodyBytes.Length}\r\n" +
                                $"Content-Type: multipart/form-data; boundary={boundary}\r\n\r\n" +
                                body;

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("200 OK", response);
            string savedPath = Path.Combine(dir, "hello.txt");
            Assert.True(File.Exists(savedPath));
            Assert.Equal(fileContent, File.ReadAllText(savedPath));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PathSanitizer_NormalizesReservedWindowsNames()
    {
        string safe1 = LanDropPathSanitizer.NormalizeFileName("CON.txt");
        Assert.Equal("_CON.txt", safe1);

        string safe2 = LanDropPathSanitizer.NormalizeFileName("NUL");
        Assert.Equal("_NUL", safe2);

        string safe3 = LanDropPathSanitizer.NormalizeFileName("COM1.pdf");
        Assert.Equal("_COM1.pdf", safe3);
    }

    [Fact]
    public void PathSanitizer_DetectsTraversalMarkers()
    {
        Assert.True(LanDropPathSanitizer.IsPathTraversalAttempt("../foo"));
        Assert.True(LanDropPathSanitizer.IsPathTraversalAttempt("..\\foo"));
        Assert.True(LanDropPathSanitizer.IsPathTraversalAttempt("/etc/passwd"));
        Assert.True(LanDropPathSanitizer.IsPathTraversalAttempt("C:\\autoexec.bat"));
        Assert.True(LanDropPathSanitizer.IsPathTraversalAttempt("foo:stream"));
        Assert.True(LanDropPathSanitizer.IsPathTraversalAttempt("%2e%2e/evil"));

        Assert.False(LanDropPathSanitizer.IsPathTraversalAttempt("normal_file.txt"));
        Assert.False(LanDropPathSanitizer.IsPathTraversalAttempt("photo 2026.jpg"));
    }

    [Fact]
    public void PathSanitizer_ValidatesContainment()
    {
        string root = Path.Combine(Path.GetTempPath(), "containment_test");
        Directory.CreateDirectory(root);

        try
        {
            string valid = LanDropPathSanitizer.ValidateContainment(root, "safe.txt");
            Assert.StartsWith(Path.GetFullPath(root), valid);

            Assert.Throws<SecurityException>(() =>
                LanDropPathSanitizer.ValidateContainment(root, "../outside.txt"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StreamingUpload_MemoryUsageIsBoundedIndependentlyOfUploadSize()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "streaming_mem_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            const int targetUploadSize = 5 * 1024 * 1024; // 5 MB
            string boundary = "----StreamingBoundaryXYZ";

            // A synthetic stream that generates 5 MB without storing 5 MB in RAM
            using var generatorStream = new SyntheticMultipartStream(boundary, "bigfile.bin", targetUploadSize);

            var limits = new LanDropSecurityLimits
            {
                MaxFileSizeBytes = 10 * 1024 * 1024,
                MaxTotalRequestSizeBytes = 20 * 1024 * 1024
            };

            long memBefore = GC.GetTotalMemory(true);

            var results = await StreamingMultipartParser.ParseAndStageAsync(
                generatorStream,
                boundary,
                tempDir,
                limits,
                _ => 100L * 1024 * 1024 * 1024, // 100 GB available
                CancellationToken.None);

            long memAfter = GC.GetTotalMemory(false);
            long memAllocated = Math.Max(0, memAfter - memBefore);

            Assert.Single(results);
            Assert.Equal("bigfile.bin", results[0].OriginalFileName);
            Assert.Equal(targetUploadSize, results[0].FileSizeBytes);

            // Memory allocated must be substantially less than the 5 MB file size (bounded to chunk buffer)
            Assert.True(memAllocated < 2 * 1024 * 1024,
                $"Expected memory allocation to be bounded independently of upload size, but measured {memAllocated} bytes for 5 MB upload.");

            Assert.True(File.Exists(results[0].FinalFilePath));
            Assert.Equal(targetUploadSize, new FileInfo(results[0].FinalFilePath).Length);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task MultipartMultipleFiles_AllSavedWithContainment()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            string boundary = "----MultiFileBoundary123";
            string body = $"--{boundary}\r\n" +
                          "Content-Disposition: form-data; name=\"description\"\r\n\r\n" +
                          "photo album\r\n" +
                          $"--{boundary}\r\n" +
                          "Content-Disposition: form-data; name=\"file1\"; filename=\"image1.png\"\r\n" +
                          "Content-Type: image/png\r\n\r\n" +
                          "PNGDATA1\r\n" +
                          $"--{boundary}\r\n" +
                          "Content-Disposition: form-data; name=\"file2\"; filename=\"image2.png\"\r\n" +
                          "Content-Type: image/png\r\n\r\n" +
                          "PNGDATA2\r\n" +
                          $"--{boundary}--\r\n";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: {bodyBytes.Length}\r\n" +
                                $"Content-Type: multipart/form-data; boundary={boundary}\r\n\r\n" +
                                body;

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("200 OK", response);
            string file1 = Path.Combine(dir, "image1.png");
            string file2 = Path.Combine(dir, "image2.png");

            Assert.True(File.Exists(file1));
            Assert.True(File.Exists(file2));
            Assert.Equal("PNGDATA1", File.ReadAllText(file1));
            Assert.Equal("PNGDATA2", File.ReadAllText(file2));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultipartTruncatedStream_AbortsAndLeavesNoFinalFiles()
    {
        var (service, dir, token, port) = CreateTestServer();

        try
        {
            string boundary = "----TruncatedBoundary123";
            string header = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                            $"Host: 127.0.0.1:{port}\r\n" +
                            "Content-Length: 50000\r\n" +
                            $"Content-Type: multipart/form-data; boundary={boundary}\r\n\r\n" +
                            $"--{boundary}\r\n" +
                            "Content-Disposition: form-data; name=\"file\"; filename=\"corrupted.dat\"\r\n\r\n" +
                            "First few bytes of corrupted file";

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();

            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            await stream.FlushAsync();

            // Abrupt disconnect without ending boundary
            client.Close();

            await Task.Delay(300);

            // Verify no files exist
            Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MultipartHeaderOversized_RejectedWith400()
    {
        var limits = new LanDropSecurityLimits
        {
            MaxMultipartPartHeaderSizeBytes = 512
        };
        var (service, dir, token, port) = CreateTestServer(limits);

        try
        {
            string boundary = "----OversizedPartBoundary";
            string hugePartHeader = new string('A', 1024);
            string body = $"--{boundary}\r\n" +
                          $"X-Custom-Header: {hugePartHeader}\r\n" +
                          "Content-Disposition: form-data; name=\"file\"; filename=\"sample.txt\"\r\n\r\n" +
                          "sample content\r\n" +
                          $"--{boundary}--\r\n";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string rawRequest = $"POST /api/upload?t={token.Value} HTTP/1.1\r\n" +
                                $"Host: 127.0.0.1:{port}\r\n" +
                                $"Content-Length: {bodyBytes.Length}\r\n" +
                                $"Content-Type: multipart/form-data; boundary={boundary}\r\n\r\n" +
                                body;

            string response = await SendRawHttpRequestAsync(port, rawRequest);

            Assert.Contains("400 Bad Request", response);
            Assert.Empty(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            service.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Helper stream that simulates a large multipart request body of arbitrary size without buffering in RAM.
    /// </summary>
    private sealed class SyntheticMultipartStream : Stream
    {
        private readonly byte[] _headerBytes;
        private readonly byte[] _trailerBytes;
        private readonly long _payloadSizeBytes;
        private long _payloadBytesSent;
        private int _headerBytesSent;
        private int _trailerBytesSent;

        public SyntheticMultipartStream(string boundary, string filename, long payloadSizeBytes)
        {
            _payloadSizeBytes = payloadSizeBytes;
            string header = $"--{boundary}\r\n" +
                            $"Content-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n" +
                            "Content-Type: application/octet-stream\r\n\r\n";
            _headerBytes = Encoding.UTF8.GetBytes(header);
            _trailerBytes = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _headerBytes.Length + _payloadSizeBytes + _trailerBytes.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_headerBytesSent < _headerBytes.Length)
            {
                int toCopy = Math.Min(count, _headerBytes.Length - _headerBytesSent);
                Array.Copy(_headerBytes, _headerBytesSent, buffer, offset, toCopy);
                _headerBytesSent += toCopy;
                return toCopy;
            }

            if (_payloadBytesSent < _payloadSizeBytes)
            {
                int toSend = (int)Math.Min(count, _payloadSizeBytes - _payloadBytesSent);
                for (int i = 0; i < toSend; i++)
                {
                    buffer[offset + i] = (byte)(_payloadBytesSent % 256);
                    _payloadBytesSent++;
                }
                return toSend;
            }

            if (_trailerBytesSent < _trailerBytes.Length)
            {
                int toCopy = Math.Min(count, _trailerBytes.Length - _trailerBytesSent);
                Array.Copy(_trailerBytes, _trailerBytesSent, buffer, offset, toCopy);
                _trailerBytesSent += toCopy;
                return toCopy;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
