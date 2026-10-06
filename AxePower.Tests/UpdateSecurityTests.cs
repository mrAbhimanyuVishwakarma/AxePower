using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Update;
using Xunit;

namespace AxePower.Tests;

public class UpdateSecurityTests
{
    private class FakeAuthenticodeVerifier : IAuthenticodeVerifier
    {
        public bool IsValid { get; set; } = true;
        public string Subject { get; set; } = "CN=Abhimanyu Vishwakarma, O=AxePower, C=US";
        public string FailureReason { get; set; } = "Untrusted or invalid Authenticode signature";

        public AuthenticodeVerifyResult Verify(string filePath, string expectedPublisher)
        {
            if (!IsValid)
            {
                return AuthenticodeVerifyResult.Failure(FailureReason);
            }

            if (!string.IsNullOrWhiteSpace(expectedPublisher) &&
                Subject.IndexOf(expectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return AuthenticodeVerifyResult.Failure($"Publisher mismatch: expected '{expectedPublisher}' but found '{Subject}'.");
            }

            return AuthenticodeVerifyResult.Success(Subject);
        }
    }

    private static UpdateManifest CreateValidManifest(ECDsa privateKey, string assetName, byte[] payloadBytes, string version = "2.2.0")
    {
        using var sha = SHA256.Create();
        var hashHex = Convert.ToHexString(sha.ComputeHash(payloadBytes)).ToLowerInvariant();

        var manifest = new UpdateManifest
        {
            Version = version,
            AssetName = assetName,
            ByteLength = payloadBytes.Length,
            Sha256 = hashHex,
            PublishedAt = DateTimeOffset.UtcNow
        };

        manifest.Signature = ManifestSigner.Sign(manifest, privateKey);
        return manifest;
    }

    [Fact]
    public async Task TamperedManifest_ThrowsUpdateVerificationException()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var payload = Encoding.UTF8.GetBytes("Valid installer binary content");
        var manifest = CreateValidManifest(privateKey, "AxePower_Setup.exe", payload);

        // Tamper with byte length after signature was calculated
        manifest.ByteLength += 100;

        var options = new UpdateSecurityOptions
        {
            PinnedPublicKeyPem = publicKeyPem,
            AllowHttpForTesting = true
        };
        var service = new UpdateService(new LogService(), options, new FakeAuthenticodeVerifier());

        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadUpdateAsync("https://github.com/mrAbhimanyuVishwakarma/AxePower/releases/download/v2.2.0/AxePower_Setup.exe",
                new Progress<double>(), manifest: manifest));

        Assert.Contains("manifest signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongKey_ThrowsUpdateVerificationException()
    {
        using var privateKeyA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var privateKeyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPemB = privateKeyB.ExportSubjectPublicKeyInfoPem();

        var payload = Encoding.UTF8.GetBytes("Installer payload");
        var manifest = CreateValidManifest(privateKeyA, "AxePower_Setup.exe", payload);

        // Configure service with a different public key
        var options = new UpdateSecurityOptions
        {
            PinnedPublicKeyPem = publicKeyPemB,
            AllowHttpForTesting = true
        };
        var service = new UpdateService(new LogService(), options, new FakeAuthenticodeVerifier());

        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadUpdateAsync("https://github.com/mrAbhimanyuVishwakarma/AxePower/releases/download/v2.2.0/AxePower_Setup.exe",
                new Progress<double>(), manifest: manifest));

        Assert.Contains("manifest signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangedInstaller_HashMismatch_ThrowsUpdateVerificationException()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var originalPayload = Encoding.UTF8.GetBytes("Original expected content");
        var tamperedPayload = Encoding.UTF8.GetBytes("Corrupted / modified payload from server");

        var manifest = CreateValidManifest(privateKey, "AxePower_Setup.exe", originalPayload);
        // Set manifest length to match tampered payload so it gets past length check and fails on SHA-256
        manifest.ByteLength = tamperedPayload.Length;
        manifest.Signature = ManifestSigner.Sign(manifest, privateKey);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/AxePower_Setup.exe";

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {tamperedPayload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(tamperedPayload);
            await stream.FlushAsync();
        });

        try
        {
            var options = new UpdateSecurityOptions
            {
                PinnedPublicKeyPem = publicKeyPem,
                AllowHttpForTesting = true
            };
            options.AllowedHosts.Add("127.0.0.1");

            var service = new UpdateService(new LogService(), options, new FakeAuthenticodeVerifier());

            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                service.DownloadUpdateAsync(url, new Progress<double>(), manifest: manifest));

            Assert.Contains("hash mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task WrongPublisher_ThrowsUpdateVerificationException()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var payload = Encoding.UTF8.GetBytes("Valid payload bytes");
        var manifest = CreateValidManifest(privateKey, "AxePower_Setup.exe", payload);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/AxePower_Setup.exe";

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        try
        {
            var options = new UpdateSecurityOptions
            {
                PinnedPublicKeyPem = publicKeyPem,
                ExpectedPublisher = "Abhimanyu Vishwakarma",
                RequireAuthenticode = true, // this test is about the Authenticode layer, off by default while releases are unsigned
                AllowHttpForTesting = true
            };
            options.AllowedHosts.Add("127.0.0.1");

            var verifier = new FakeAuthenticodeVerifier
            {
                IsValid = true,
                Subject = "CN=Malicious Attacker, O=Evil Corp, C=US"
            };

            var service = new UpdateService(new LogService(), options, verifier);

            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                service.DownloadUpdateAsync(url, new Progress<double>(), manifest: manifest));

            Assert.Contains("Authenticode signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Publisher mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UnexpectedRedirect_ThrowsUpdateVerificationException()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var payload = Encoding.UTF8.GetBytes("Some payload");
        var manifest = CreateValidManifest(privateKey, "AxePower_Setup.exe", payload);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/AxePower_Setup.exe";

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            // Redirect to untrusted / malicious external host
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: https://evil.attacker.example.com/trojan.exe\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.FlushAsync();
        });

        try
        {
            var options = new UpdateSecurityOptions
            {
                PinnedPublicKeyPem = publicKeyPem,
                AllowHttpForTesting = true
            };
            options.AllowedHosts.Add("127.0.0.1");

            var service = new UpdateService(new LogService(), options, new FakeAuthenticodeVerifier());

            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                service.DownloadUpdateAsync(url, new Progress<double>(), manifest: manifest));

            Assert.Contains("not in the allow-list", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DowngradeOrReplay_ThrowsUpdateVerificationException()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var payload = Encoding.UTF8.GetBytes("Older payload");

        // App is currently at 2.1.0; propose older version 2.0.0
        var manifest = CreateValidManifest(privateKey, "AxePower_Setup.exe", payload, version: "2.0.0");

        var options = new UpdateSecurityOptions
        {
            PinnedPublicKeyPem = publicKeyPem,
            AllowHttpForTesting = true
        };
        var service = new UpdateService(new LogService(), options, new FakeAuthenticodeVerifier());

        var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            service.DownloadUpdateAsync("https://github.com/mrAbhimanyuVishwakarma/AxePower/releases/download/v2.0.0/AxePower_Setup.exe",
                new Progress<double>(), manifest: manifest));

        Assert.Contains("Downgrade and replay attacks are prohibited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TruncatedFile_ThrowsUpdateVerificationException()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var fullPayload = Encoding.UTF8.GetBytes("Full payload expected by manifest");
        var truncatedPayload = Encoding.UTF8.GetBytes("Full payload"); // Partial bytes

        var manifest = CreateValidManifest(privateKey, "AxePower_Setup.exe", fullPayload);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/AxePower_Setup.exe";

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            // Content-Length matches truncated payload
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {truncatedPayload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(truncatedPayload);
            await stream.FlushAsync();
        });

        try
        {
            var options = new UpdateSecurityOptions
            {
                PinnedPublicKeyPem = publicKeyPem,
                AllowHttpForTesting = true
            };
            options.AllowedHosts.Add("127.0.0.1");

            var service = new UpdateService(new LogService(), options, new FakeAuthenticodeVerifier());

            var ex = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                service.DownloadUpdateAsync(url, new Progress<double>(), manifest: manifest));

            Assert.True(
                ex.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("does not match manifest byte length", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ValidSignedFixture_CompletesFlowSuccessfully()
    {
        using var privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem();

        var payload = Encoding.UTF8.GetBytes("Valid cryptographic installer payload bytes with proper signature");
        var fileName = $"AxePower_Setup_{Guid.NewGuid():N}.exe";
        var manifest = CreateValidManifest(privateKey, fileName, payload, version: "2.5.0");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/{fileName}";
        var expectedDestination = Path.Combine(Path.GetTempPath(), "AxePower_Update", fileName);

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        });

        try
        {
            var options = new UpdateSecurityOptions
            {
                PinnedPublicKeyPem = publicKeyPem,
                ExpectedPublisher = "Abhimanyu Vishwakarma",
                RequireAuthenticode = true, // this test is about the Authenticode layer, off by default while releases are unsigned
                AllowHttpForTesting = true
            };
            options.AllowedHosts.Add("127.0.0.1");

            var verifier = new FakeAuthenticodeVerifier
            {
                IsValid = true,
                Subject = "CN=Abhimanyu Vishwakarma"
            };

            var service = new UpdateService(new LogService(), options, verifier);

            var downloadedPath = await service.DownloadUpdateAsync(url, new Progress<double>(), manifest: manifest);

            Assert.Equal(expectedDestination, downloadedPath);
            Assert.True(File.Exists(downloadedPath));
            Assert.Equal(payload.Length, new FileInfo(downloadedPath).Length);
        }
        finally
        {
            listener.Stop();
            if (File.Exists(expectedDestination))
            {
                File.Delete(expectedDestination);
            }
        }
    }

    [Fact]
    public void MzOnlyHeader_WithoutAuthenticodeOrManifest_IsRejected()
    {
        // Prove that an arbitrary file with an MZ header is refused by ApplyUpdate
        var dummyFile = Path.Combine(Path.GetTempPath(), $"axepower_fake_installer_{Guid.NewGuid():N}.exe");
        try
        {
            var fakePayload = new byte[1024];
            fakePayload[0] = (byte)'M';
            fakePayload[1] = (byte)'Z';
            File.WriteAllBytes(dummyFile, fakePayload);

            var options = new UpdateSecurityOptions
            {
                RequireAuthenticode = true
            };

            // Using production WindowsAuthenticodeVerifier: an unsigned file with MZ fails!
            var service = new UpdateService(new LogService(), options, new WindowsAuthenticodeVerifier());

            bool result = service.ApplyUpdate(dummyFile);
            Assert.False(result, "ApplyUpdate must refuse unsigned binaries even if they begin with MZ.");
        }
        finally
        {
            if (File.Exists(dummyFile))
            {
                File.Delete(dummyFile);
            }
        }
    }

    [Fact]
    public void WindowsAuthenticodeVerifier_VerifiesSignedSystemBinaryOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        string hostExe = Environment.ProcessPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(hostExe)) return;

        var verifier = new WindowsAuthenticodeVerifier(requireTrustedRoot: true);

        // 1. Verify with matching Microsoft publisher
        var validResult = verifier.Verify(hostExe, "Microsoft");
        Assert.True(validResult.IsValid, validResult.FailureReason);
        Assert.NotNull(validResult.Subject);
        Assert.Contains("Microsoft", validResult.Subject, StringComparison.OrdinalIgnoreCase);

        // 2. Verify with wrong publisher fails
        var wrongPublisherResult = verifier.Verify(hostExe, "NonExistentPublisherXYZ");
        Assert.False(wrongPublisherResult.IsValid);
        Assert.Contains("Publisher", wrongPublisherResult.FailureReason, StringComparison.OrdinalIgnoreCase);
    }
}
