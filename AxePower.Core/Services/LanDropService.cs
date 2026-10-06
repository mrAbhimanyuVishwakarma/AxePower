using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services.LanDrop;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services;

/// <summary>
/// LAN Drop and the phone portal: the listener and its session lifecycle, plus the public API
/// the app uses. The work is done by the components in <c>Services/LanDrop</c>:
/// <see cref="LanDropRequestHandler"/> (per-connection pipeline and routing),
/// <see cref="LanDropHttp"/> (parsing and responses), <see cref="LanDropAuthenticator"/>,
/// <see cref="LanDropClientRegistry"/>, <see cref="LanDropStorage"/> and
/// <see cref="LanDropClipboard"/>, <see cref="LanDropEndpoints"/> and
/// <see cref="LanDropMusicEndpoints"/>, <see cref="IRemoteMediaActions"/>, and
/// <see cref="LanDropPortalPage"/>.
/// </summary>
public class LanDropService : IDisposable, ILanDropHost
{
    private readonly LogService _logService;

    /// <summary>
    /// Null only where the portal is constructed without security wired up (older tests). The
    /// server then refuses to start rather than listening unprotected.
    /// </summary>
    private readonly PortalAccessToken? _token;

    private readonly LanDropSecurityLimits _limits;
    private readonly LanDropStorage _storage;
    private readonly LanDropClipboard _clipboard;
    private readonly LanDropAuthenticator _auth;
    private readonly LanDropClientRegistry _clients;
    private readonly LanDropRequestHandler _handler;

    /// <summary>For the container test: the token and music remote DI is meant to supply.</summary>
    internal bool HasAccessToken => _token is not null;
    internal bool HasMusicRemote { get; }
    private readonly SemaphoreSlim _connectionThrottle;
    private readonly Func<string?, NetworkClassificationResult> _networkClassifier;
    private readonly Func<IPAddress, bool> _addressIsLocal;

    /// <summary>Re-checks token expiry, idle clients and the network profile while serving.</summary>
    private Timer? _watchdog;
    private NetworkSecurityCategory _sessionNetworkCategory;
    private bool _untrustedNetworkAllowed;

    /// <summary>The session cookie the portal page authenticates with once the QR link has been opened.</summary>
    internal const string SessionCookieName = LanDropAuthenticator.SessionCookieName;

    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public event EventHandler<LanDropFileItem>? OnFileReceived;
    public event EventHandler<LanDropTextMessage>? OnTextReceived;
    public event EventHandler<LanDropServerState>? OnStateChanged;
    public event EventHandler? OnClientsChanged;

    /// <summary>
    /// The network a start was last refused on because Windows classifies it as Public or cannot
    /// classify it. Null after a successful start. The UI offers a one-session override from it.
    /// </summary>
    public NetworkClassificationResult? LastRefusedNetwork { get; private set; }

    public string CurrentIpAddress { get; private set; } = string.Empty;
    public int CurrentPort { get; private set; } = 5050;
    public bool IsRunning => _tcpListener != null && _cts != null && !_cts.IsCancellationRequested;
    public string ServerUrl => IsRunning && !string.IsNullOrEmpty(CurrentIpAddress)
        ? $"http://{CurrentIpAddress}:{CurrentPort}/"
        : string.Empty;

    public LanDropSecurityLimits Limits => _limits;
    public AuthenticationRateLimiter RateLimiter => _auth.RateLimiter;

    public IReadOnlyList<LanDropClientSession> GetConnectedClients() => _clients.Snapshot();

    public NetworkClassificationResult CurrentNetworkClassification => _networkClassifier(CurrentIpAddress);

    /// <summary>Where received files land. Setting it clears staging files a crash left there.</summary>
    public string DownloadDirectory
    {
        get => _storage.DownloadDirectory;
        set => _storage.DownloadDirectory = value;
    }

    /// <summary>
    /// The address to open on a phone, including the access token. This is what the QR code
    /// encodes: the link is the credential, so it must not be shared any further than intended.
    /// </summary>
    public string PortalUrl => IsRunning && _token is not null && !string.IsNullOrEmpty(CurrentIpAddress)
        ? $"http://{CurrentIpAddress}:{CurrentPort}/?{PortalAccessToken.QueryName}={_token.Value}"
        : string.Empty;

    public LanDropService(
        LogService logService,
        PortalAccessToken? token = null,
        MusicRemote? music = null,
        LanDropSecurityLimits? limits = null,
        Func<string, long>? diskSpaceChecker = null,
        Func<string?, NetworkClassificationResult>? networkClassifier = null,
        Func<IPAddress, bool>? addressIsLocal = null,
        string? downloadDirectory = null,
        IRemoteMediaActions? remoteActions = null)
    {
        _logService = logService;
        _networkClassifier = networkClassifier ?? NetworkProfileClassifier.ClassifyCurrentNetwork;
        _addressIsLocal = addressIsLocal ?? NetworkProfileClassifier.IsLocalAddress;
        _token = token;
        _limits = limits ?? new LanDropSecurityLimits();
        _connectionThrottle = new SemaphoreSlim(_limits.MaxConcurrentClients, _limits.MaxConcurrentClients);

        // Tests pass their own folder so constructing the service never touches the real Downloads.
        var downloads = downloadDirectory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "AxePower_LanDrop");
        _storage = new LanDropStorage(downloads, _limits, diskSpaceChecker);
        _clipboard = new LanDropClipboard(_limits.MaxClipboardMessagesCount);
        _auth = new LanDropAuthenticator(token, _limits);
        _clients = new LanDropClientRegistry(_limits.ClientIdleTimeout);
        var endpoints = new LanDropEndpoints(this, _storage, _clipboard, remoteActions ?? new WindowsRemoteMediaActions(), _auth, _limits, logService);
        HasMusicRemote = music is not null;
        _handler = new LanDropRequestHandler(_limits, _auth, _clients, endpoints, new LanDropMusicEndpoints(music),
            () => OnClientsChanged?.Invoke(this, EventArgs.Empty), logService);

        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }
        catch (Exception ex)
        {
            // Without this, a network change is caught only by the periodic session check, so the
            // portal may stay reachable on a new network until it runs. Worth a warning.
            logService.LogWarning("[LanDrop] Cannot watch for network changes; relying on the periodic session check", ex);
        }

        CurrentIpAddress = GetLocalIpAddresses().FirstOrDefault() ?? "127.0.0.1";
    }

    public static void CleanUpOrphanStagingFiles(string directory) => LanDropStorage.CleanUpOrphanStagingFiles(directory);

    public static List<string> GetLocalIpAddresses()
    {
        var result = new List<string>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ThenByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

            foreach (var ni in interfaces)
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    {
                        var ipStr = addr.Address.ToString();
                        if (!result.Contains(ipStr)) result.Add(ipStr);
                    }
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // The adapter list could not be read: callers fall back to loopback and say so.
        }

        // Prioritize RFC 1918 private subnets over public/unclassified ones
        result = result
            .OrderByDescending(ipStr => IPAddress.TryParse(ipStr, out var ip) && NetworkProfileClassifier.IsPrivateSubnet(ip))
            .ToList();

        if (result.Count == 0) result.Add("127.0.0.1");
        return result;
    }

    // -- Session lifecycle ----------------------------------------------------------------

    /// <summary>
    /// Starts a fresh portal session bound to <paramref name="ipAddress"/> only. On a network
    /// Windows classifies as Public, or cannot classify, the start is refused unless
    /// <paramref name="allowUntrustedNetwork"/> is set; that override lasts for this session only.
    /// </summary>
    public Task<bool> StartServerAsync(string? ipAddress = null, int port = 5050, bool allowUntrustedNetwork = false)
    {
        try
        {
            if (IsRunning) StopServer();

            if (!string.IsNullOrEmpty(ipAddress)) CurrentIpAddress = ipAddress;
            if (_token is null)
            {
                // Listening on every interface with no way to tell an intended request from
                // any other is not something to do quietly, so it is refused outright.
                _logService.LogError("[LanDrop] Refusing to start: the portal has no access token configured.");
                return Task.FromResult(false);
            }

            if (!IPAddress.TryParse(CurrentIpAddress, out var bindAddress))
            {
                _logService.LogError("[LanDrop] Refusing to start: no valid network adapter address is selected.");
                return Task.FromResult(false);
            }

            var classification = CurrentNetworkClassification;
            if (classification.IsPublicOrUnclassified)
            {
                if (!allowUntrustedNetwork)
                {
                    LastRefusedNetwork = classification;
                    _logService.LogWarning($"[LanDrop] Refusing to start on an untrusted network: {classification.WarningMessage}");
                    NotifyStateChanged();
                    return Task.FromResult(false);
                }
                _logService.LogWarning($"[LanDrop] Starting on an untrusted network because the user allowed it for this session: {classification.WarningMessage}");
            }
            LastRefusedNetwork = null;
            _sessionNetworkCategory = classification.Category;
            _untrustedNetworkAllowed = allowUntrustedNetwork;

            CurrentPort = port;

            // Start a fresh server session: fresh high-entropy token, reset rate limiter, clear previous clients
            _token.StartNewSession();
            _auth.Reset();
            _clients.EndAll();
            OnClientsChanged?.Invoke(this, EventArgs.Empty);

            // Bound to the chosen adapter's address, not every interface, so the portal is not
            // reachable from a VPN, hotspot or other adapter the user did not pick.
            _cts = new CancellationTokenSource();
            _tcpListener = new TcpListener(bindAddress, CurrentPort);
            _tcpListener.Start();

            _listenTask = Task.Run(() => AcceptConnectionsAsync(_tcpListener, _cts.Token));
            _watchdog = new Timer(_ => CheckSessionHealth(), null, _limits.SessionWatchdogInterval, _limits.SessionWatchdogInterval);
            _logService.LogInfo($"[LanDrop] Local HTTP server started on {ServerUrl} ({classification.Category} network)");

            NotifyStateChanged();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logService.LogError($"[LanDrop] Failed to start TcpListener on port {port}", ex);
            StopServer();
            return Task.FromResult(false);
        }
    }

    public void StopServer()
    {
        try
        {
            _watchdog?.Dispose();
            _watchdog = null;
            _cts?.Cancel();
            _tcpListener?.Stop();
            _tcpListener = null;
            _token?.Invalidate();
            _clients.EndAll();
            _auth.ClearBars();
            OnClientsChanged?.Invoke(this, EventArgs.Empty);
            _logService.LogInfo("[LanDrop] Server stopped and access tokens invalidated.");
        }
        catch (Exception ex)
        {
            _logService.LogError("[LanDrop] Error stopping server", ex);
        }
        finally
        {
            NotifyStateChanged();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            _logService.LogWarning("[LanDrop] Network address change detected. Invalidating active tokens and disconnecting clients.");
            DisconnectAllClientsAndRotateAccess();
            CheckSessionHealth();
        }
    }

    /// <summary>
    /// Ends or rotates the session when it should no longer be trusted: the token has passed
    /// its lifetime, the bound address has gone, or the network's Windows profile has changed.
    /// Runs on a timer while serving and after every network address change.
    /// </summary>
    internal void CheckSessionHealth()
    {
        if (!IsRunning || _token is null) return;
        try
        {
            if (_clients.PruneIdle()) OnClientsChanged?.Invoke(this, EventArgs.Empty);

            if (_token.IsExpired)
            {
                _logService.LogInfo("[LanDrop] Session access expired. Issuing a new link.");
                DisconnectAllClientsAndRotateAccess();
            }

            if (IPAddress.TryParse(CurrentIpAddress, out var bound) && !_addressIsLocal(bound))
            {
                _logService.LogWarning("[LanDrop] The adapter address the portal was bound to is gone. Stopping the server.");
                StopServer();
                return;
            }

            var classification = CurrentNetworkClassification;
            if (classification.Category == _sessionNetworkCategory) return;

            if (classification.IsPublicOrUnclassified && !_untrustedNetworkAllowed)
            {
                LastRefusedNetwork = classification;
                _logService.LogWarning($"[LanDrop] Network profile changed to {classification.Category}. Stopping the server: {classification.WarningMessage}");
                StopServer();
                return;
            }

            _logService.LogWarning($"[LanDrop] Network profile changed from {_sessionNetworkCategory} to {classification.Category}. Rotating access.");
            _sessionNetworkCategory = classification.Category;
            DisconnectAllClientsAndRotateAccess();
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[LanDrop] Session health check failed: {ex.Message}");
        }
    }

    public void DisconnectAllClientsAndRotateAccess()
    {
        _clients.EndAll();
        _auth.ClearBars();
        _token?.Rotate();
        _auth.RateLimiter.Reset();
        OnClientsChanged?.Invoke(this, EventArgs.Empty);
        NotifyStateChanged();
        _logService.LogInfo("[LanDrop] Rotated access token and terminated all client sessions.");
    }

    public void DisconnectClient(string clientId)
    {
        if (_clients.Remove(clientId) is not { } client) return;
        _auth.Bar(client.ClientIp);
        OnClientsChanged?.Invoke(this, EventArgs.Empty);
        _logService.LogInfo($"[LanDrop] Disconnected client {client.DeviceName} ({client.ClientIp}).");
    }

    public static string SanitizeUrlForLogging(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(url, @"([?&])" + PortalAccessToken.QueryName + @"=[^&#\s]+", "$1t=[REDACTED]");
    }

    // -- Files and quick text ---------------------------------------------------------------

    public void AddSharedFile(string filePath)
    {
        if (_storage.AddShared(filePath) is { } item)
        {
            _logService.LogInfo($"[LanDrop] Added a {AxePower.Core.Services.LanDrop.LanDropLogText.Kind(item.FileName)} file for sharing ({item.FormattedSize}).");
        }
    }

    public void RemoveSharedFile(string id) => _storage.RemoveShared(id);

    public void ClearSharedFiles() => _storage.ClearShared();

    public IReadOnlyList<LanDropFileItem> GetSharedFiles() => _storage.GetShared();

    public IReadOnlyList<LanDropFileItem> GetReceivedFiles() => _storage.GetReceived();

    public void ClearReceivedFiles() => _storage.ClearReceived();

    public IReadOnlyList<LanDropTextMessage> GetClipboardMessages() =>
        _clipboard.GetAll().OrderByDescending(m => m.Timestamp).ToList();

    public void PostTextMessageFromPc(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var msg = new LanDropTextMessage
        {
            Content = content.Trim(),
            Timestamp = DateTime.Now,
            SenderDevice = Environment.MachineName,
            IsFromPc = true
        };
        _clipboard.Add(msg);
        OnTextReceived?.Invoke(this, msg);
    }

    void ILanDropHost.FileReceived(LanDropFileItem item) => OnFileReceived?.Invoke(this, item);

    void ILanDropHost.TextReceived(LanDropTextMessage message) => OnTextReceived?.Invoke(this, message);

    // -- Listener -----------------------------------------------------------------------------

    private async Task AcceptConnectionsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientWithThrottleAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logService.LogError("[LanDrop] Socket accept error", ex);
                }
            }
        }
    }

    private async Task HandleClientWithThrottleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = (int)_limits.ReadTimeout.TotalMilliseconds;
                client.SendTimeout = (int)_limits.WriteTimeout.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // The client disconnected before its first byte; the request below fails at once.
            }

            bool acquired = await _connectionThrottle.WaitAsync(0, ct);
            if (!acquired)
            {
                try
                {
                    await using var stream = client.GetStream();
                    await LanDropHttp.SendAsync(stream, 503, "Service Unavailable", "application/json",
                        Encoding.UTF8.GetBytes("{\"error\":\"Server concurrency limit reached.\"}"));
                    await Task.Delay(100, ct);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                {
                    // Best-effort refusal: the client may already have gone.
                }
                return;
            }

            try
            {
                await using var networkStream = client.GetStream();
                var clientIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
                await _handler.HandleAsync(networkStream, clientIp, ct);
            }
            finally
            {
                _connectionThrottle.Release();
            }
        }
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke(this, new LanDropServerState
        {
            IsRunning = IsRunning,
            SelectedIp = CurrentIpAddress,
            Port = CurrentPort,
            PortalUrl = PortalUrl
        });
    }

    public void Dispose()
    {
        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
        catch (Exception)
        {
            // Best-effort teardown: subscribing may have failed in the first place (see the constructor).
        }
        StopServer();
        _cts?.Dispose();
        _connectionThrottle.Dispose();
    }
}
