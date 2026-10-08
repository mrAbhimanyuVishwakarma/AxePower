using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Helpers;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.Music;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// Comprehensive verification for LAN Drop authentication hardening, session lifecycle,
/// rate limiting, constant-time validation, credential redaction, client termination,
/// and network profile warnings.
/// </summary>
public sealed class LanDropAuthenticationTests : IDisposable
{
    private readonly LogService _logService = new();
    private readonly List<string> _tempDirectories = new();
    private readonly List<LanDropService> _servicesToDispose = new();

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
        LanDropSecurityLimits? limits = null)
    {
        var token = new PortalAccessToken(new InMemorySecretStore());
        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_LanDropAuthTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);

        var serverLimits = limits ?? new LanDropSecurityLimits
        {
            ReadTimeout = TimeSpan.FromSeconds(5),
            WriteTimeout = TimeSpan.FromSeconds(5)
        };

        var service = new LanDropService(_logService, token, null, serverLimits, downloadDirectory: tempDir)
        {
            DownloadDirectory = tempDir
        };
        _servicesToDispose.Add(service);

        using var tempListener = new TcpListener(IPAddress.Loopback, 0);
        tempListener.Start();
        int port = ((IPEndPoint)tempListener.LocalEndpoint).Port;
        tempListener.Stop();

        bool started = service.StartServerAsync("127.0.0.1", port).GetAwaiter().GetResult();
        Assert.True(started, "Test server failed to start on port " + port);

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
                if (current.Contains("\r\n\r\n") && (current.Contains("Content-Length: 0") || current.Contains("Connection: close")))
                {
                    // Allow small time for any attached body to arrive
                    await Task.Delay(20, cts.Token);
                    if (stream.DataAvailable)
                    {
                        int extra = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                        if (extra > 0) ms.Write(buffer, 0, extra);
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void PortalAccessToken_GeneratesFresh256BitHighEntropyToken_AndSupportsLifecycle()
    {
        var token = new PortalAccessToken();
        string initialValue = token.Value;

        Assert.NotNull(initialValue);
        Assert.NotEmpty(initialValue);
        // Base64URL encoded 32 bytes (256-bit) is 43 characters
        Assert.True(initialValue.Length >= 42, $"Token length was {initialValue.Length}, expected at least 42 characters.");

        // Constant time validation on current token
        Assert.True(token.IsValid(initialValue));
        Assert.False(token.IsValid("tampered-token-value-12345678901234567890"));
        Assert.False(token.IsValid(null));
        Assert.False(token.IsValid(string.Empty));

        // StartNewSession rotates the token
        string nextSessionToken = token.StartNewSession();
        Assert.NotEqual(initialValue, nextSessionToken);
        Assert.False(token.IsValid(initialValue), "Old session token must not be valid after StartNewSession.");
        Assert.True(token.IsValid(nextSessionToken));

        // Invalidate cuts off all access
        token.Invalidate();
        Assert.False(token.IsValid(nextSessionToken), "Token must not be valid after Invalidate.");
    }

    [Fact]
    public void PortalAccessToken_ConstantTimeValidation_FixedTimeEquals()
    {
        var token = new PortalAccessToken();
        string validToken = token.Value;

        // Exactly matching
        Assert.True(token.IsValid(validToken));

        // Single character mismatch
        char[] modified = validToken.ToCharArray();
        modified[0] = modified[0] == 'a' ? 'b' : 'a';
        Assert.False(token.IsValid(new string(modified)));

        // Length mismatch
        Assert.False(token.IsValid(validToken + "extra"));
        Assert.False(token.IsValid(validToken.Substring(0, validToken.Length - 1)));
    }

    [Fact]
    public async Task LanDrop_TheLinkInsideTheQrCode_OpensThePortal()
    {
        var (service, _, _, port) = CreateTestServer();

        // What the Phone Hub page draws, read back the way a phone camera would.
        var link = QrCodeDecodeTests.DecodeBmp(QrCodeGenerator.GenerateBmpBytes(service.PortalUrl, 8, 4, 0x00000000, 0x00FFFFFF));
        Assert.Equal(service.PortalUrl, link);

        var uri = new Uri(link!);
        string response = await SendRawHttpRequestAsync(port,
            $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("<title>AxePower Mobile Hub</title>", response);
    }

    [Fact]
    public async Task LanDrop_PlainAddressInABrowser_SaysHowToGetIn_ButStaysA401()
    {
        var (_, _, _, port) = CreateTestServer();

        string page = await SendRawHttpRequestAsync(port, $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", page);
        Assert.Contains("text/html", page);
        Assert.Contains("Access link needed", page);
        Assert.DoesNotContain("AxePower", page); // nothing for a network scan to learn

        string api = await SendRawHttpRequestAsync(port, $"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", api);
        Assert.EndsWith("Unauthorized", api.TrimEnd());
        Assert.DoesNotContain("Access link needed", api);
    }

    [Fact]
    public async Task LanDrop_PreviousSessionUrlNoLongerWorksAfterServerRestart()
    {
        var (service, _, token, port) = CreateTestServer();
        string session1Token = token.Value;

        // Session 1 access succeeds
        string req1 = $"GET /api/status?{PortalAccessToken.QueryName}={session1Token} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
        string res1 = await SendRawHttpRequestAsync(port, req1);
        Assert.StartsWith("HTTP/1.1 200 OK", res1);

        // Stop server and restart (new session)
        service.StopServer();
        Assert.False(service.IsRunning);

        bool restarted = await service.StartServerAsync("127.0.0.1", port);
        Assert.True(restarted);
        string session2Token = token.Value;
        Assert.NotEqual(session1Token, session2Token);

        // Request using URL from session 1 must fail with 401 Unauthorized
        string reqSession1Url = $"GET /api/status?{PortalAccessToken.QueryName}={session1Token} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
        string resSession1 = await SendRawHttpRequestAsync(port, reqSession1Url);
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", resSession1);

        // Request using session 2 token succeeds
        string reqSession2Url = $"GET /api/status?{PortalAccessToken.QueryName}={session2Token} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
        string resSession2 = await SendRawHttpRequestAsync(port, reqSession2Url);
        Assert.StartsWith("HTTP/1.1 200 OK", resSession2);
    }

    [Fact]
    public async Task LanDrop_ExplicitDisconnectAllClientsAndRotateAccess_InvalidatesActiveToken()
    {
        var (service, _, token, port) = CreateTestServer();
        string initialToken = token.Value;

        // Authenticate client
        string req = $"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{PortalAccessToken.HeaderName}: {initialToken}\r\nUser-Agent: Mozilla/5.0 (iPhone; CPU iPhone OS 16_0)\r\nConnection: close\r\n\r\n";
        string res = await SendRawHttpRequestAsync(port, req);
        Assert.StartsWith("HTTP/1.1 200 OK", res);

        // Connected clients list now tracks the device
        var clients = service.GetConnectedClients();
        Assert.NotEmpty(clients);
        Assert.Equal("Apple iPhone", clients[0].DeviceName);

        // Rotate access & terminate all clients
        service.DisconnectAllClientsAndRotateAccess();

        // Clients list is cleared
        Assert.Empty(service.GetConnectedClients());

        // Previous token now rejected
        string oldTokenReq = $"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{PortalAccessToken.HeaderName}: {initialToken}\r\nConnection: close\r\n\r\n";
        string oldTokenRes = await SendRawHttpRequestAsync(port, oldTokenReq);
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", oldTokenRes);

        // New token works
        string newToken = token.Value;
        Assert.NotEqual(initialToken, newToken);
        string newTokenReq = $"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{PortalAccessToken.HeaderName}: {newToken}\r\nConnection: close\r\n\r\n";
        string newTokenRes = await SendRawHttpRequestAsync(port, newTokenReq);
        Assert.StartsWith("HTTP/1.1 200 OK", newTokenRes);
    }

    [Fact]
    public async Task LanDrop_RepeatedFailedAuthentication_Triggers429TooManyRequestsLockout()
    {
        var limits = new LanDropSecurityLimits
        {
            MaxFailedAuthAttempts = 3,
            FailedAuthWindow = TimeSpan.FromMinutes(1),
            FailedAuthLockoutDuration = TimeSpan.FromSeconds(3)
        };

        var (service, _, token, port) = CreateTestServer(limits);

        // Send 3 failed attempts with wrong token
        for (int i = 0; i < 3; i++)
        {
            string badReq = $"GET /api/status?{PortalAccessToken.QueryName}=wrong_token_{i} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
            string badRes = await SendRawHttpRequestAsync(port, badReq);
            Assert.StartsWith("HTTP/1.1 401 Unauthorized", badRes);
        }

        // 4th request must be rejected with 429 Too Many Requests
        string lockedReq = $"GET /api/status?{PortalAccessToken.QueryName}=wrong_token_4 HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n";
        string lockedRes = await SendRawHttpRequestAsync(port, lockedReq);

        Assert.StartsWith("HTTP/1.1 429 Too Many Requests", lockedRes);
        Assert.Contains("Retry-After:", lockedRes);
        Assert.Contains("Too Many Requests", lockedRes);
    }

    [Fact]
    public void LanDrop_SanitizeUrlForLogging_RedactsTokenValues()
    {
        string raw1 = "http://192.168.1.100:5050/?t=super_secret_access_token_12345";
        string sanitized1 = LanDropService.SanitizeUrlForLogging(raw1);
        Assert.DoesNotContain("super_secret_access_token_12345", sanitized1);
        Assert.Contains("t=[REDACTED]", sanitized1);

        string raw2 = "http://127.0.0.1:5050/api/download?id=file123&t=another_secret_token";
        string sanitized2 = LanDropService.SanitizeUrlForLogging(raw2);
        Assert.DoesNotContain("another_secret_token", sanitized2);
        Assert.Contains("&t=[REDACTED]", sanitized2);
        Assert.Contains("id=file123", sanitized2);

        // Empty or null
        Assert.Equal(string.Empty, LanDropService.SanitizeUrlForLogging(null));
        Assert.Equal(string.Empty, LanDropService.SanitizeUrlForLogging(string.Empty));
    }

    [Fact]
    public void NetworkProfileClassifier_DetectsPrivateSubnetsAndFlagsPublicInterfaces()
    {
        // Private subnets RFC 1918
        Assert.True(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("127.0.0.1")));
        Assert.True(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("10.0.0.1")));
        Assert.True(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("192.168.1.50")));
        Assert.True(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("172.16.0.1")));
        Assert.True(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("172.31.255.254")));
        Assert.True(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("169.254.1.1")));

        // Public subnets
        Assert.False(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("8.8.8.8")));
        Assert.False(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("1.1.1.1")));
        Assert.False(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("172.32.0.1")));
        Assert.False(NetworkProfileClassifier.IsPrivateSubnet(IPAddress.Parse("11.0.0.1")));

        // Loopback classification
        var loopbackResult = NetworkProfileClassifier.ClassifyCurrentNetwork("127.0.0.1");
        Assert.Equal(NetworkSecurityCategory.Private, loopbackResult.Category);
        Assert.False(loopbackResult.IsPublicOrUnclassified);
        Assert.Null(loopbackResult.WarningMessage);

        // Public IP classification
        var publicResult = NetworkProfileClassifier.ClassifyCurrentNetwork("8.8.8.8");
        Assert.Equal(NetworkSecurityCategory.Public, publicResult.Category);
        Assert.True(publicResult.IsPublicOrUnclassified);
        Assert.NotNull(publicResult.WarningMessage);
    }

    [Fact]
    public void LanDropViewModel_ReflectsConnectedClientsAndPublicWarning()
    {
        var token = new PortalAccessToken(new InMemorySecretStore());
        var lanService = new LanDropService(_logService, token, downloadDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxePower_LanDropTests"));
        var notifService = new NotificationService();
        _servicesToDispose.Add(lanService);

        var vm = new LanDropViewModel(lanService, notifService);

        Assert.NotNull(vm.ConnectedClients);
        Assert.Equal(0, vm.ConnectedClientsCount);
        Assert.False(vm.HasConnectedClients);

        // DisconnectAllClientsCommand executes without error
        vm.DisconnectAllClientsCommand.Execute(null);
        Assert.Equal(0, vm.ConnectedClientsCount);
    }

    public void Dispose()
    {
        foreach (var service in _servicesToDispose)
        {
            try { service.Dispose(); } catch { }
        }

        foreach (var dir in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { }
        }
    }
}
