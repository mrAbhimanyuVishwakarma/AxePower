using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.LanDrop;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// LAN Drop session lifecycle: refusing untrusted networks, binding to the chosen adapter,
/// ending or rotating the session on expiry and network changes, the cookie that takes the
/// token out of the URL, and keeping every token value out of the log.
/// </summary>
public sealed class LanDropSessionLifecycleTests : IDisposable
{
    private readonly LogService _logService = new();
    private readonly ConcurrentQueue<string> _logMessages = new();
    private readonly List<string> _tempDirectories = new();
    private readonly List<LanDropService> _services = new();

    public LanDropSessionLifecycleTests()
    {
        _logService.OnLogAdded += (_, entry) => _logMessages.Enqueue(entry.Message);
    }

    private static NetworkClassificationResult Network(NetworkSecurityCategory category) => new()
    {
        Category = category,
        IsPublicOrUnclassified = category is NetworkSecurityCategory.Public or NetworkSecurityCategory.Unknown,
        NetworkName = "Test " + category,
        WarningMessage = category is NetworkSecurityCategory.Public or NetworkSecurityCategory.Unknown ? "untrusted" : null
    };

    private LanDropService CreateService(
        PortalAccessToken token,
        Func<string?, NetworkClassificationResult>? classifier = null,
        Func<IPAddress, bool>? addressIsLocal = null,
        LanDropSecurityLimits? limits = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AxePower_LanDropLifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);

        var service = new LanDropService(_logService, token, null,
            limits ?? new LanDropSecurityLimits
            {
                ReadTimeout = TimeSpan.FromSeconds(5),
                WriteTimeout = TimeSpan.FromSeconds(5),
                // The tests drive the health check themselves.
                SessionWatchdogInterval = TimeSpan.FromHours(1)
            },
            networkClassifier: classifier,
            addressIsLocal: addressIsLocal,
            downloadDirectory: dir)
        {
            DownloadDirectory = dir
        };
        _services.Add(service);
        return service;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> GetAsync(int port, string pathAndQuery, string? extraHeaders = null, IPAddress? from = null)
    {
        using var client = from == null ? new TcpClient() : new TcpClient(new IPEndPoint(from, 0));
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        string request = $"GET {pathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: Android test\r\n{extraHeaders}\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);

        using var cts = new CancellationTokenSource(5000);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                ms.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task Start_IsRefusedOnPublicNetwork_UnlessAllowedForThisSession()
    {
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(NetworkSecurityCategory.Public));
        int port = FreePort();

        Assert.False(await service.StartServerAsync("127.0.0.1", port));
        Assert.False(service.IsRunning);
        Assert.NotNull(service.LastRefusedNetwork);
        Assert.Equal(NetworkSecurityCategory.Public, service.LastRefusedNetwork!.Category);

        Assert.True(await service.StartServerAsync("127.0.0.1", port, allowUntrustedNetwork: true));
        Assert.True(service.IsRunning);
        Assert.Null(service.LastRefusedNetwork);

        // The override does not outlive the session: a plain restart is refused again.
        service.StopServer();
        Assert.False(await service.StartServerAsync("127.0.0.1", port));
    }

    [Fact]
    public async Task Start_IsRefusedWhenTheNetworkCannotBeClassified()
    {
        var service = CreateService(new PortalAccessToken(), _ => Network(NetworkSecurityCategory.Unknown));
        Assert.False(await service.StartServerAsync("127.0.0.1", FreePort()));
        Assert.Equal(NetworkSecurityCategory.Unknown, service.LastRefusedNetwork!.Category);
    }

    [Fact]
    public async Task Start_OnPrivateOrDomainNetwork_Succeeds()
    {
        var privateService = CreateService(new PortalAccessToken(), _ => Network(NetworkSecurityCategory.Private));
        Assert.True(await privateService.StartServerAsync("127.0.0.1", FreePort()));

        var domainService = CreateService(new PortalAccessToken(), _ => Network(NetworkSecurityCategory.Domain));
        Assert.True(await domainService.StartServerAsync("127.0.0.1", FreePort()));
    }

    [Fact]
    public async Task Server_IsBoundToTheSelectedAddressOnly()
    {
        var other = LanDropService.GetLocalIpAddresses()
            .Select(IPAddress.Parse)
            .FirstOrDefault(ip => !IPAddress.IsLoopback(ip));
        if (other is null) return; // No second address on this machine to prove it against.

        var service = CreateService(new PortalAccessToken(), _ => Network(NetworkSecurityCategory.Private));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(other, port));
    }

    [Fact]
    public async Task NetworkProfileChangingToPublic_StopsTheServer()
    {
        var category = NetworkSecurityCategory.Private;
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(category));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        string oldToken = token.Value;

        category = NetworkSecurityCategory.Public;
        service.CheckSessionHealth();

        Assert.False(service.IsRunning);
        Assert.False(token.IsValid(oldToken));
        Assert.Equal(NetworkSecurityCategory.Public, service.LastRefusedNetwork!.Category);
    }

    [Fact]
    public async Task NetworkProfileChangingBetweenTrustedProfiles_RotatesAccess()
    {
        var category = NetworkSecurityCategory.Private;
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(category));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        string oldToken = token.Value;

        category = NetworkSecurityCategory.Domain;
        service.CheckSessionHealth();

        Assert.True(service.IsRunning);
        Assert.NotEqual(oldToken, token.Value);
        Assert.StartsWith("HTTP/1.1 401", await GetAsync(port, $"/api/status?t={oldToken}"));
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, $"/api/status?t={token.Value}"));
    }

    [Fact]
    public async Task BoundAddressDisappearing_StopsTheServer()
    {
        bool present = true;
        var service = CreateService(new PortalAccessToken(), _ => Network(NetworkSecurityCategory.Private), _ => present);
        Assert.True(await service.StartServerAsync("127.0.0.1", FreePort()));

        present = false;
        service.CheckSessionHealth();

        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task ExpiredSession_IssuesANewLinkAndRefusesTheOldOne()
    {
        var token = new PortalAccessToken(sessionLifetime: TimeSpan.FromMilliseconds(200));
        var service = CreateService(token, _ => Network(NetworkSecurityCategory.Private));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        string oldToken = token.Value;
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, $"/api/status?t={oldToken}"));

        await Task.Delay(400);
        Assert.StartsWith("HTTP/1.1 401", await GetAsync(port, $"/api/status?t={oldToken}"));

        service.CheckSessionHealth();
        Assert.True(service.IsRunning);
        Assert.NotEqual(oldToken, token.Value);
        Assert.Contains(token.Value, service.PortalUrl);
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, $"/api/status?t={token.Value}"));
    }

    [Fact]
    public async Task IdleClients_AreDroppedFromTheConnectedList()
    {
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(NetworkSecurityCategory.Private), limits: new LanDropSecurityLimits
        {
            ClientIdleTimeout = TimeSpan.FromMilliseconds(150),
            SessionWatchdogInterval = TimeSpan.FromHours(1)
        });
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));

        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, $"/api/status?t={token.Value}"));
        Assert.Single(service.GetConnectedClients());

        await Task.Delay(400);
        Assert.Empty(service.GetConnectedClients());
    }

    [Fact]
    public async Task OpeningTheQrLink_SetsAStrictSessionCookie_ThatRotationRevokes()
    {
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(NetworkSecurityCategory.Private));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        string sessionToken = token.Value;

        string page = await GetAsync(port, $"/?t={sessionToken}");
        Assert.StartsWith("HTTP/1.1 200", page);
        string cookieLine = page.Split("\r\n").Single(l => l.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"{LanDropService.SessionCookieName}={sessionToken}", cookieLine);
        Assert.Contains("HttpOnly", cookieLine);
        Assert.Contains("SameSite=Strict", cookieLine);
        Assert.DoesNotContain("Expires", cookieLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Max-Age", cookieLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cache-Control: no-store", page);

        string cookieHeader = $"Cookie: other=1; {LanDropService.SessionCookieName}={sessionToken}\r\n";
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, "/api/status", cookieHeader));

        service.DisconnectAllClientsAndRotateAccess();
        Assert.StartsWith("HTTP/1.1 401", await GetAsync(port, "/api/status", cookieHeader));
    }

    [Fact]
    public async Task DisconnectingOneDevice_KeepsItOut_WhileOthersStay()
    {
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(NetworkSecurityCategory.Private));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        var phone = IPAddress.Parse("127.0.0.2");
        var tablet = IPAddress.Parse("127.0.0.3");
        var cookie = $"Cookie: {LanDropService.SessionCookieName}={token.Value}\r\n";

        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, "/api/status", cookie, phone));
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, "/api/status", cookie, tablet));
        var phoneSession = service.GetConnectedClients().Single(c => c.ClientIp == "127.0.0.2");

        // The UI disconnects by session id.
        service.DisconnectClient(phoneSession.Id);

        Assert.StartsWith("HTTP/1.1 403", await GetAsync(port, "/api/status", cookie, phone));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync(port, $"/?t={token.Value}", null, phone)); // the link from history too
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, "/api/status", cookie, tablet));
        Assert.DoesNotContain(service.GetConnectedClients(), c => c.ClientIp == "127.0.0.2");

        // Rotating access lifts the bar along with every old credential.
        service.DisconnectAllClientsAndRotateAccess();
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(port, $"/api/status?t={token.Value}", null, phone));
    }

    [Fact]
    public async Task PortalPage_StripsTheTokenFromTheAddressBar_AndNeverPutsItInRequestUrls()
    {
        var token = new PortalAccessToken();
        var service = CreateService(token, _ => Network(NetworkSecurityCategory.Private));
        int port = FreePort();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));

        string page = await GetAsync(port, $"/?t={token.Value}");
        int bodyStart = page.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        string html = page[bodyStart..];

        Assert.Contains("history.replaceState", html);
        Assert.DoesNotContain(token.Value, html);
        Assert.DoesNotContain("'t=' +", html);
        Assert.DoesNotContain("localStorage", html);
        Assert.DoesNotContain("sessionStorage", html);
    }

    [Fact]
    public async Task TokenValues_NeverAppearInTheLog()
    {
        var token = new PortalAccessToken(sessionLifetime: TimeSpan.FromMilliseconds(300));
        var category = NetworkSecurityCategory.Private;
        var service = CreateService(token, _ => Network(category));
        var issued = new List<string>();
        int port = FreePort();

        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        issued.Add(token.Value);
        await GetAsync(port, $"/?t={token.Value}");
        await GetAsync(port, $"/api/status?t={token.Value}");
        await GetAsync(port, "/api/status?t=wrong-guess");
        await GetAsync(port, "/api/status", $"Cookie: {LanDropService.SessionCookieName}={token.Value}\r\n");
        await GetAsync(port, "/api/status", $"{PortalAccessToken.HeaderName}: {token.Value}\r\n");

        service.DisconnectAllClientsAndRotateAccess();
        issued.Add(token.Value);
        await GetAsync(port, $"/api/status?t={issued[0]}");

        await Task.Delay(400);
        service.CheckSessionHealth(); // expiry rotation
        issued.Add(token.Value);

        category = NetworkSecurityCategory.Domain;
        service.CheckSessionHealth(); // profile-change rotation
        issued.Add(token.Value);

        service.StopServer();
        Assert.True(await service.StartServerAsync("127.0.0.1", port));
        issued.Add(token.Value);
        service.StopServer();

        Assert.NotEmpty(_logMessages);
        foreach (var value in issued.Distinct())
        {
            Assert.DoesNotContain(_logMessages, m => m.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void NetworkProfileClassifier_UsesTheLiveAdapterProfile_WithoutThrowing()
    {
        // The real Network List Manager path, for every address this machine has. On a PC
        // with a connected adapter at least one of these comes back from Windows itself.
        foreach (var ipText in LanDropService.GetLocalIpAddresses())
        {
            var result = NetworkProfileClassifier.ClassifyCurrentNetwork(ipText);
            Assert.False(string.IsNullOrEmpty(result.NetworkName));
            Assert.Equal(result.Category is NetworkSecurityCategory.Public or NetworkSecurityCategory.Unknown,
                result.IsPublicOrUnclassified);
        }

        Assert.True(NetworkProfileClassifier.ClassifyCurrentNetwork(null).IsPublicOrUnclassified);
        Assert.True(NetworkProfileClassifier.ClassifyCurrentNetwork("not an ip").IsPublicOrUnclassified);
        // A private-range address that is not on this PC cannot be vouched for.
        Assert.Equal(NetworkSecurityCategory.Unknown, NetworkProfileClassifier.ClassifyCurrentNetwork("10.254.254.254").Category);
    }

    [Theory]
    [InlineData("AxePower.iss")]
    [InlineData("AxePowerSetup.iss")]
    public void Installers_AllowLanDropOnPrivateAndDomainNetworksOnly_AndRemoveTheRuleOnUninstall(string script)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "AxePower.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
        string iss = File.ReadAllText(Path.Combine(dir, "Installer", script));

        string runSection = iss[iss.IndexOf("[Run]", StringComparison.Ordinal)..];
        string addRule = runSection.Split('\n').Single(l => l.Contains("firewall add rule", StringComparison.Ordinal));
        Assert.Contains("name=\"\"AxePower LAN Drop\"\"", addRule);
        Assert.Contains("dir=in", addRule);
        Assert.Contains("protocol=TCP localport=5050", addRule);
        Assert.Contains("profile=private,domain", addRule);
        Assert.DoesNotContain("public", addRule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("program=", addRule);

        string uninstall = iss[iss.IndexOf("[UninstallRun]", StringComparison.Ordinal)..];
        Assert.Contains("firewall delete rule name=\"\"AxePower LAN Drop\"\"", uninstall);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            try { service.Dispose(); } catch { }
        }
        foreach (var dir in _tempDirectories)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}
