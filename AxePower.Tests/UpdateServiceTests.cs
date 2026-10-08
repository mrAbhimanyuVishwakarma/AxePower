using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void UpdateService_InitializesProperly()
    {
        var logService = new LogService();
        var updateService = new UpdateService(logService);

        var currentVersion = updateService.GetCurrentVersion();
        Assert.NotNull(currentVersion);

        var versionString = updateService.GetCurrentVersionString();
        Assert.False(string.IsNullOrWhiteSpace(versionString));
    }

    [Fact]
    public void FormatReleaseNotesForDisplay_RemovesMarkdownNoise()
    {
        const string markdown = "# AxePower v2.1.0\n\n## Highlights\n\n- **System Care** is unified.\n- Download `Setup.exe`.";

        var formatted = UpdateService.FormatReleaseNotesForDisplay(markdown);

        Assert.DoesNotContain("#", formatted);
        Assert.DoesNotContain("**", formatted);
        Assert.DoesNotContain("`", formatted);
        Assert.DoesNotContain("AxePower v2.1.0", formatted);
        Assert.Contains("Highlights", formatted);
        Assert.Contains("• System Care is unified.", formatted);
        Assert.Contains("• Download Setup.exe.", formatted);
    }

    [Fact]
    public async Task DownloadUpdateAsync_ConcurrentRequestsShareOneVerifiedDownload()
    {
        var payload = Enumerable.Repeat((byte)0x5A, 128 * 1024).ToArray();
        payload[0] = (byte)'M';
        payload[1] = (byte)'Z';

        using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pubKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hashHex = Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fileName = $"AxePower-test-{Guid.NewGuid():N}.exe";
        var url = $"http://127.0.0.1:{port}/{fileName}";
        var destination = Path.Combine(Path.GetTempPath(), "AxePower_Update", fileName);
        var requestCount = 0;

        var manifest = new AxePower.Core.Services.Update.UpdateManifest
        {
            Version = "2.5.0",
            AssetName = fileName,
            ByteLength = payload.Length,
            Sha256 = hashHex,
            PublishedAt = DateTimeOffset.UtcNow
        };
        manifest.Signature = AxePower.Core.Services.Update.ManifestSigner.Sign(manifest, ecdsa);

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            Interlocked.Increment(ref requestCount);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            // Hold the first caller long enough for the second to wait on the single-flight gate.
            await Task.Delay(100);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        try
        {
            var options = new AxePower.Core.Services.Update.UpdateSecurityOptions
            {
                PinnedPublicKeyPem = pubKeyPem,
                AllowHttpForTesting = true
            };
            options.AllowedHosts.Add("127.0.0.1");

            var mockAuth = new TestAuthenticodeVerifier();
            var service = new UpdateService(new LogService(), options, mockAuth);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = service.DownloadUpdateAsync(url, new Progress<double>(), timeout.Token, manifest);
            var second = service.DownloadUpdateAsync(url, new Progress<double>(), timeout.Token, manifest);

            var paths = await Task.WhenAll(first, second);
            await server;

            Assert.Equal(1, requestCount);
            Assert.Equal(destination, paths[0]);
            Assert.Equal(destination, paths[1]);
            Assert.Equal(payload.Length, new FileInfo(destination).Length);
            var prefix = File.ReadAllBytes(destination).Take(2).ToArray();
            Assert.Equal(new byte[] { (byte)'M', (byte)'Z' }, prefix);
        }
        finally
        {
            listener.Stop();
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private class TestAuthenticodeVerifier : AxePower.Core.Services.Update.IAuthenticodeVerifier
    {
        public AxePower.Core.Services.Update.AuthenticodeVerifyResult Verify(string filePath, string expectedPublisher)
        {
            return AxePower.Core.Services.Update.AuthenticodeVerifyResult.Success("CN=Abhimanyu Vishwakarma");
        }
    }
}
