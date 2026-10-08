using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services.LanDrop;
using Microsoft.Win32;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Serves the viewer page and carries remote input, chat, and voice back from it.
///
/// It speaks HTTP and WebSocket over a raw socket rather than using <c>HttpListener</c>, because
/// binding anything but loopback with HttpListener needs either administrator rights or a
/// pre-registered URL reservation, and AxePower is meant to run as a standard user.
///
/// The threat model is docs/security/SHARE_SCREEN_THREAT_MODEL.md. In short: no secret is ever
/// sent on the wire (clients answer a single-use nonce with an HMAC), Origin and Host are both
/// checked, control needs the viewer token and the host's current control code, control frames
/// are encrypted and authenticated with a strictly increasing sequence, and every socket is bounded in size, rate,
/// count and idle time. Whatever ends a control connection lets go of every held key.
/// </summary>
public sealed class ShareViewerHost : IDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public const int MaxControlFrameBytes = 8192;
    public const int MaxChatFrameBytes = 65536;
    public const int MaxVoiceFrameBytes = 4096;
    public const int MaxChatSockets = 10;
    public const int MaxVoiceSockets = 10;
    public const int MaxConcurrentSockets = 64;
    public const int MaxRequestHeadBytes = 8192;

    /// <summary>Control events applied per second; extras are dropped.</summary>
    public const int ControlEventsPerSecond = 180;
    /// <summary>Control events in one second that end the connection.</summary>
    public const int ControlEventsAbusePerSecond = 500;
    public const int ChatFramesAbusePerSecond = 100;
    public const int VoiceFramesAbusePerSecond = 200;

    /// <summary>Failed control attempts in one session that switch control off.</summary>
    public const int MaxFailedControlAttemptsPerSession = 20;

    private readonly LogService _logService;
    private readonly RemoteInputService _input;
    private readonly SemaphoreSlim _socketSlots = new(MaxConcurrentSockets, MaxConcurrentSockets);
    private readonly AdmissionNonces _nonces = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private ShareRoom? _room;
    private byte[]? _authKey;

    /// <summary>A random path per session for the media server's auth checks.</summary>
    private string _mediaAuthPath = string.Empty;
    private int _webrtcPort;
    private string _hostName = string.Empty;

    /// <summary>The code a viewer must present to take control. Null while control is off.</summary>
    private string? _controlCode;
    private int _failedControlAttempts;

    /// <summary>The one connection currently holding control, if any.</summary>
    private Connection? _controller;

    /// <summary>The certificate the room is served with, or null for plain HTTP.</summary>
    private X509Certificate2? _tls;

    /// <summary>True while the room is served over HTTPS/WSS.</summary>
    public bool IsSecure => _tls != null;

    public int Port { get; private set; }

    public bool IsRunning => _listener != null && _cts is { IsCancellationRequested: false };

    /// <summary>How long a client has to send its request head.</summary>
    internal TimeSpan RequestHeadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A WebSocket with no inbound frame for this long is closed.</summary>
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the server pings each WebSocket, which keeps a live client inside <see cref="IdleTimeout"/>.</summary>
    internal TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Raised when a viewer takes or drops control, with true for taken.</summary>
    public event EventHandler<bool>? ControlHolderChanged;

    /// <summary>Raised when repeated failed control attempts switched control off.</summary>
    public event EventHandler? ControlDisabledAfterFailedAttempts;

    /// <summary>
    /// Asks the host whether this viewer may take control. A correct code is not enough on its
    /// own: someone who learned or guessed it still has to be let in. With no approver set, every
    /// request is refused.
    /// </summary>
    public Func<ControlApprovalRequest, CancellationToken, Task<bool>>? ApproveControl { get; set; }

    /// <summary>How long the host has to answer before the request is refused.</summary>
    internal TimeSpan ApprovalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly List<Connection> _chatSockets = new();
    private readonly object _chatLock = new();
    private readonly List<Connection> _voiceSockets = new();
    private readonly object _voiceLock = new();

    private readonly AuthenticationRateLimiter _controlRateLimiter = new(
        maxFailures: 5,
        window: TimeSpan.FromMinutes(1),
        lockoutDuration: TimeSpan.FromMinutes(2));

    private readonly ReplayFilter _chatReplayFilter = new();
    private readonly ReplayFilter _voiceReplayFilter = new();

    private readonly ConcurrentDictionary<string, ShareParticipant> _participants = new();
    public event EventHandler? ParticipantsChanged;

    /// <summary>Raised when a participant sends a chat message in the room.</summary>
    public event EventHandler<ShareChatMessage>? ChatMessageReceived;
    public event EventHandler<byte[]>? VoicePacketReceived;

    public ShareViewerHost(LogService logService, RemoteInputService input)
    {
        _logService = logService;
        _input = input;

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            // SystemEvents is unavailable without an interactive session. Remote control then does
            // not stop on its own when the PC locks, which the host should know about.
            _logService.LogWarning("[ShareScreen] Cannot watch for the session locking; remote control will not stop automatically on lock", ex);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _logService.LogWarning("[ShareScreen] Workstation locked. Revoking remote control immediately.");
            DisableControl();
        }
    }

    public IReadOnlyList<ShareParticipant> GetActiveParticipants() =>
        _participants.Values.OrderBy(p => p.ConnectedAt).ToList();

    /// <param name="tls">
    /// A certificate valid for <paramref name="hostName"/> to serve HTTPS/WSS with (Tailscale), or
    /// null for plain HTTP. With one, plain HTTP is accepted only from this PC, for the media
    /// server's auth check.
    /// </param>
    public void Start(ShareRoom room, string hostName, int webrtcPort, int preferredPort, X509Certificate2? tls = null)
    {
        Stop();

        _tls = tls;
        _room = room;
        _authKey = ShareScreenCrypto.DeriveAuthKey(room.ViewerToken);
        _mediaAuthPath = "/mtx-auth/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _hostName = hostName;
        _webrtcPort = webrtcPort;
        _failedControlAttempts = 0;
        Port = FindFreePort(preferredPort);

        _chatReplayFilter.Reset();
        _voiceReplayFilter.Reset();
        _nonces.Reset();

        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);

        _logService.LogInfo($"[ShareScreen] Viewer page is being served on port {Port}.");
    }

    /// <summary>Where the media server asks whether a publish or read may go ahead. Loopback only.</summary>
    public string MediaAuthAddress => $"http://127.0.0.1:{Port}{_mediaAuthPath}";

    /// <summary>The link to hand a friend. View-only: it carries no control code.</summary>
    public string ViewerUrl => _room == null
        ? string.Empty
        : $"{(_tls != null ? "https" : "http")}://{_hostName}:{Port}/{_room.PathName}#v={_room.ViewerToken}";

    // -- Control gate --------------------------------------------------------

    /// <summary>
    /// Turns control on and returns the code a viewer must type to claim it.
    ///
    /// The code is deliberately not part of the viewer link, so handing someone the link to watch
    /// never hands them the keyboard as well -- taking control is always a second, separate act.
    /// </summary>
    public string EnableControl(CaptureDisplay target)
    {
        _controlCode = RandomCode();
        Interlocked.Exchange(ref _failedControlAttempts, 0);
        _input.Allow(target);
        return _controlCode;
    }

    /// <summary>Shuts control off and drops whoever holds it.</summary>
    public void DisableControl()
    {
        _controlCode = null;
        _input.RevokeAll();

        var controller = Interlocked.Exchange(ref _controller, null);
        if (controller != null)
        {
            controller.Close();
            ControlHolderChanged?.Invoke(this, false);
        }
    }

    private static string RandomCode()
    {
        // Ambiguous characters are left out so the code survives being read aloud.
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var chars = new char[8];
        for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    // -- Origin and Host validation -----------------------------------------

    private bool IsAllowedOrigin(string? origin, Dictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            // Native clients send no Origin. They still have to answer the nonce, which a web
            // page cannot do without the room's secrets.
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        if (!originUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !originUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var originHost = originUri.Host;
        var cleanRequestHost = HostName(headers.GetValueOrDefault("Host"));

        if (string.Equals(originHost, cleanRequestHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(_hostName) && string.Equals(originHost, _hostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsLoopbackHost(originHost) && (IsLoopbackHost(cleanRequestHost) || cleanRequestHost.Length == 0))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the Host header names this PC. A DNS-rebound page sends its own domain here,
    /// which defeats an Origin check alone, so only this PC's addresses, loopback and the
    /// configured viewer host name are served.
    /// </summary>
    internal bool IsAllowedHost(string? hostHeader)
    {
        var host = HostName(hostHeader);
        if (host.Length == 0) return false;
        if (IsLoopbackHost(host)) return true;
        if (!string.IsNullOrEmpty(_hostName) && string.Equals(host, _hostName, StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && NetworkProfileClassifier.IsLocalAddress(ip);
    }

    /// <summary>The host part of a Host header, without the port; IPv6 keeps its brackets.</summary>
    private static string HostName(string? hostHeader)
    {
        var value = (hostHeader ?? string.Empty).Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 0 ? value[..(close + 1)] : value;
        }
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[..colon] : value;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);

    // -- Socket plumbing -----------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptSocketAsync(ct);
                if (!_socketSlots.Wait(0))
                {
                    // Over the cap, half-open or not: refuse rather than queue.
                    _ = RefuseBusyAsync(client, ct);
                    continue;
                }

                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _logService.LogError("[ShareScreen] Viewer host accept failed", ex);
                await Task.Delay(200, ct);
            }
        }
    }

    private async Task RefuseBusyAsync(Socket raw, CancellationToken ct)
    {
        if (_tls == null)
        {
            var plain = new Connection(raw, new NetworkStream(raw, ownsSocket: false), secure: false);
            await SendSimpleAsync(plain, 503, "Service Unavailable", ct);
            plain.Close();
            return;
        }
        Connection.CloseQuietly(raw);
    }

    /// <summary>
    /// Wraps an accepted socket. With a certificate, a TLS handshake is expected (its first byte
    /// is 0x16); anything else is let through only from loopback, for the media server's check.
    /// </summary>
    private async Task<Connection?> OpenAsync(Socket raw, CancellationToken ct)
    {
        var network = new NetworkStream(raw, ownsSocket: false);
        var tls = _tls;
        if (tls == null) return new Connection(raw, network, secure: false);

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(RequestHeadTimeout);
        try
        {
            var first = new byte[1];
            if (await raw.ReceiveAsync(first, SocketFlags.Peek, handshake.Token) == 0) return null;
            if (first[0] != 0x16)
            {
                var remote = (raw.RemoteEndPoint as IPEndPoint)?.Address;
                return remote != null && IPAddress.IsLoopback(remote) ? new Connection(raw, network, secure: false) : null;
            }

            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = tls,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshake.Token);
            return new Connection(raw, ssl, secure: true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or AuthenticationException or IOException or SocketException)
        {
            return null;
        }
    }

    private async Task HandleClientAsync(Socket raw, CancellationToken ct)
    {
        Connection? socket = null;
        try
        {
            socket = await OpenAsync(raw, ct);
            if (socket == null) { Connection.CloseQuietly(raw); return; }

            var request = await ReadRequestHeadAsync(socket, ct);
            if (request == null) { socket.Close(); return; }

            var (method, target, headers) = request.Value.Head;

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                _mediaAuthPath.Length > 0 && target == _mediaAuthPath)
            {
                await HandleMediaAuthAsync(socket, headers, request.Value.Body, ct);
                socket.Close();
                return;
            }

            if (_tls != null && !socket.Secure)
            {
                // Over HTTPS rooms, plain HTTP from this PC is for the media server's check only.
                await SendSimpleAsync(socket, 400, "Bad Request", ct);
                socket.Close();
                return;
            }

            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await SendSimpleAsync(socket, 405, "Method Not Allowed", ct);
                socket.Close();
                return;
            }

            if (!IsAllowedHost(headers.GetValueOrDefault("Host")))
            {
                _logService.LogWarning("[ShareScreen] Request refused: its Host header does not name this PC.");
                await SendSimpleAsync(socket, 421, "Misdirected Request", ct);
                socket.Close();
                return;
            }

            var path = target.Split('?')[0].TrimEnd('/');
            var query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty;
            var room = _room;

            if (room == null) { await SendSimpleAsync(socket, 503, "Not sharing", ct); socket.Close(); return; }

            // The path is the session secret; anything else gets nothing, not even a hint.
            if (path == $"/{room.PathName}")
            {
                await SendPageAsync(socket, room, headers, ct);
                socket.Close();
                return;
            }

            if (path == $"/{room.PathName}/nonce")
            {
                await SendNonceAsync(socket, ct);
                socket.Close();
                return;
            }

            if (path == $"/{room.PathName}/ws")
            {
                await HandleControlSocketAsync(socket, headers, query, ct);
                return;
            }

            if (path == $"/{room.PathName}/chat")
            {
                await HandleChatSocketAsync(socket, room, headers, query, ct);
                return;
            }

            if (path == $"/{room.PathName}/voice")
            {
                await HandleVoiceSocketAsync(socket, room, headers, query, ct);
                return;
            }

            await SendSimpleAsync(socket, 404, "Not found", ct);
            socket.Close();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException or AuthenticationException)
        {
            if (socket != null) socket.Close(); else Connection.CloseQuietly(raw);
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Viewer request failed", ex);
            if (socket != null) socket.Close(); else Connection.CloseQuietly(raw);
        }
        finally
        {
            _socketSlots.Release();
        }
    }

    private async Task<((string Method, string Target, Dictionary<string, string> Headers) Head, byte[] Body)?> ReadRequestHeadAsync(
        Connection socket, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var used = 0;

        // A client that trickles its request head (or never sends one) must not hold a slot.
        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headCts.CancelAfter(RequestHeadTimeout);

        try
        {
            while (used < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(used), SocketFlags.None, headCts.Token);
                if (read == 0) return null;
                used += read;

                var text = Encoding.ASCII.GetString(buffer, 0, used);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                var head = ParseRequestHead(text);
                if (head == null) return null;
                return (head.Value, buffer[(end + 4)..used]);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    internal static (string Method, string Target, Dictionary<string, string> Headers)? ParseRequestHead(string text)
    {
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headText = end >= 0 ? text[..end] : text;

        var lines = headText.Split("\r\n");
        if (lines.Length == 0) return null;
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return (parts[0], parts[1], headers);
    }

    private async Task SendPageAsync(
        Connection socket,
        ShareRoom room,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var requestHost = headers.GetValueOrDefault("Host") ?? string.Empty;
        var secure = _tls != null;
        // Over HTTPS the certificate names the Tailscale host, so every URL must use that name.
        var mediaHost = !secure && (requestHost.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                    requestHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            ? "127.0.0.1"
            : _hostName;
        var html = ShareViewerPage.WithScripts()
            .Replace("__TITLE__", $"AxePower - room {room.FormattedCode}")
            .Replace("__NAME__", WebUtility.HtmlEncode(Environment.UserName))
            .Replace("__WHEP__", $"{(secure ? "https" : "http")}://{mediaHost}:{_webrtcPort}/{room.PathName}/whep")
            .Replace("__WS__", $"{(secure ? "wss" : "ws")}://{mediaHost}:{Port}/{room.PathName}/ws");

        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            (secure ? "Strict-Transport-Security: max-age=300\r\n" : string.Empty) +
            "Connection: close\r\n\r\n");

        await socket.SendAsync(head, SocketFlags.None, ct);
        await socket.SendAsync(body, SocketFlags.None, ct);
    }

    // -- Media server auth --------------------------------------------------

    private const int MaxMediaAuthBodyBytes = 8192;

    /// <summary>
    /// Answers MediaMTX's <c>authMethod: http</c> check: 200 to allow, 401 to refuse. Only the
    /// media server on this PC may ask, at this session's random path.
    /// </summary>
    private async Task HandleMediaAuthAsync(Connection socket, Dictionary<string, string> headers, byte[] already, CancellationToken ct)
    {
        var allowed = false;
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        if (remote != null && IPAddress.IsLoopback(remote) &&
            int.TryParse(headers.GetValueOrDefault("Content-Length"), out var length) &&
            length is > 0 and <= MaxMediaAuthBodyBytes)
        {
            var body = new byte[length];
            var have = Math.Min(already.Length, length);
            Buffer.BlockCopy(already, 0, body, 0, have);
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(RequestHeadTimeout);
            if (have == length || await ReadExactAsync(socket, body, have, length - have, readCts.Token))
            {
                allowed = AuthorizeMedia(body);
            }
        }

        await SendSimpleAsync(socket, allowed ? 200 : 401, allowed ? "OK" : "Unauthorized", ct);
    }

    /// <summary>
    /// The decision behind the media server's auth check. Publishing needs the encoder's
    /// credentials from loopback; reading over WebRTC needs a single-use proof of the viewer token
    /// in the WHEP request's query (<c>?n=&lt;nonce&gt;&amp;p=&lt;proof&gt;</c>, channel <c>whep</c>).
    /// </summary>
    internal bool AuthorizeMedia(byte[] body)
    {
        var room = _room;
        if (room == null) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string Field(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

            if (!string.Equals(Field("path"), room.PathName, StringComparison.Ordinal)) return false;

            switch (Field("action"))
            {
                case "publish":
                    return IPAddress.TryParse(Field("ip"), out var ip) && IPAddress.IsLoopback(ip) &&
                           Field("user") == "publisher" &&
                           CryptographicOperations.FixedTimeEquals(
                               Encoding.UTF8.GetBytes(Field("password")), Encoding.UTF8.GetBytes(room.PublisherToken));
                case "read":
                    return Field("protocol") == "webrtc" &&
                           TryAdmit(Field("query"), ShareScreenCrypto.WhepChannel, _authKey, out _);
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task SendNonceAsync(Connection socket, CancellationToken ct)
    {
        var client = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
        var nonce = _nonces.Issue(client);
        if (nonce == null)
        {
            await SendSimpleAsync(socket, 503, "Service Unavailable", ct);
            return;
        }

        var body = Encoding.ASCII.GetBytes($"{{\"n\":\"{nonce}\"}}");
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n");
        await socket.SendAsync(head, SocketFlags.None, ct);
        await socket.SendAsync(body, SocketFlags.None, ct);
    }

    private static async Task SendSimpleAsync(Connection socket, int status, string reason, CancellationToken ct)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try { await socket.SendAsync(head, SocketFlags.None, ct); }
        catch (Exception ex) when (Connection.IsGone(ex)) { /* Best-effort: the client may already have gone. */ }
    }

    /// <summary>
    /// Consumes the request's nonce and checks its proof. The nonce is used up either way, so a
    /// captured upgrade can never be replayed and a guess costs a fresh round trip.
    /// </summary>
    private bool TryAdmit(string query, string channel, byte[]? key, out string nonce)
    {
        var parameters = ParseQuery(query);
        nonce = parameters.GetValueOrDefault("n") ?? string.Empty;
        var proof = parameters.GetValueOrDefault("p");
        if (!_nonces.TryConsume(nonce)) return false;
        return key != null && ShareScreenCrypto.VerifyProof(key, channel, nonce, proof);
    }

    private static async Task<bool> CompleteHandshakeAsync(Connection socket, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await SendSimpleAsync(socket, 400, "Bad Request", ct);
            return false;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await socket.SendAsync(response, SocketFlags.None, ct);
        return true;
    }

    // -- Control socket ------------------------------------------------------

    private async Task HandleControlSocketAsync(
        Connection socket, Dictionary<string, string> headers, string query, CancellationToken ct)
    {
        var clientIp = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;

        // 1. Brute-force rate limiting check
        if (_controlRateLimiter.IsLockedOut(clientIp, out var retryAfter))
        {
            _logService.LogWarning($"[ShareScreen] Control attempt blocked by rate limiter for {clientIp}. Retry after {retryAfter.TotalSeconds:F0}s.");
            var retrySec = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 429 Too Many Requests\r\nRetry-After: {retrySec}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            try { await socket.SendAsync(head, SocketFlags.None, ct); }
            catch (Exception ex) when (Connection.IsGone(ex)) { /* Best-effort: the client may already have gone. */ }
            socket.Close();
            return;
        }

        // 2. WebSocket Origin validation (Anti-CSWSH)
        if (!IsAllowedOrigin(headers.GetValueOrDefault("Origin"), headers))
        {
            _logService.LogWarning("[ShareScreen] Control WebSocket rejected: cross-site origin.");
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        // 3. The proof needs both the viewer token and the host's current control code.
        var room = _room;
        var code = _controlCode;
        var controlKey = room != null && !string.IsNullOrEmpty(code)
            ? ShareScreenCrypto.DeriveControlKey(room.ViewerToken, code)
            : null;

        if (!TryAdmit(query, ShareScreenCrypto.ControlChannel, controlKey, out var nonce))
        {
            _controlRateLimiter.RecordFailedAttempt(clientIp);
            _logService.LogWarning("[ShareScreen] A viewer tried to take control with a wrong or missing code.");

            // Counted before answering, so the code is already void by the time anyone sees the refusal.
            if (controlKey != null && Interlocked.Increment(ref _failedControlAttempts) >= MaxFailedControlAttemptsPerSession)
            {
                _logService.LogWarning("[ShareScreen] Too many failed control attempts. Switching control off; the code is void.");
                DisableControl();
                ControlDisabledAfterFailedAttempts?.Invoke(this, EventArgs.Empty);
            }

            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        _controlRateLimiter.RecordSuccessfulAuth(clientIp);

        // One controller at a time, and nobody takes it from whoever holds it (or is asking for it).
        if (Interlocked.CompareExchange(ref _controller, socket, null) != null)
        {
            _logService.LogWarning("[ShareScreen] A second viewer tried to take control while it is held. Refused.");
            await SendSimpleAsync(socket, 409, "Conflict", ct);
            socket.Close();
            return;
        }

        // The host has the last word, and must still have control on when they give it.
        var approved = await AskHostAsync(socket, headers, ct);
        if (!approved || _controlCode != code || Volatile.Read(ref _controller) != socket)
        {
            Interlocked.CompareExchange(ref _controller, null, socket);
            _logService.LogWarning("[ShareScreen] The host did not allow a control request.");
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        if (!await CompleteHandshakeAsync(socket, headers, ct))
        {
            Interlocked.CompareExchange(ref _controller, null, socket);
            socket.Close();
            return;
        }

        var participantId = "ctrl-" + Guid.NewGuid().ToString("N")[..8];
        var endpoint = socket.RemoteEndPoint?.ToString() ?? "unknown";
        _participants[participantId] = new ShareParticipant(participantId, endpoint, "Controller", DateTime.UtcNow);
        ParticipantsChanged?.Invoke(this, EventArgs.Empty);

        _logService.LogWarning("[ShareScreen] A viewer has TAKEN CONTROL of this PC.");
        ControlHolderChanged?.Invoke(this, true);

        using var pinger = StartPinger(socket, ct);
        try
        {
            var frameKey = ShareScreenCrypto.DeriveControlFrameKey(controlKey!, nonce);
            await PumpControlFramesAsync(socket, frameKey, ct);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // An ordinary disconnect or idle timeout.
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Control socket failed", ex);
        }
        finally
        {
            // Whatever happened, this viewer no longer holds the keyboard. The gate itself stays
            // as the host left it, so control remains offered for a reconnect -- but nothing is
            // left pressed in the meantime.
            pinger.Cancel();
            var wasController = Interlocked.CompareExchange(ref _controller, null, socket) == socket;
            _input.ReleaseHeld();
            _participants.TryRemove(participantId, out _);
            ParticipantsChanged?.Invoke(this, EventArgs.Empty);
            _logService.LogWarning("[ShareScreen] The viewer has released control.");
            if (wasController) ControlHolderChanged?.Invoke(this, false);
            socket.Close();
        }
    }

    private async Task<bool> AskHostAsync(Connection socket, Dictionary<string, string> headers, CancellationToken ct)
    {
        var approve = ApproveControl;
        if (approve == null) return false;

        var request = new ControlApprovalRequest(
            socket.RemoteEndPoint?.ToString() ?? "unknown",
            headers.GetValueOrDefault("User-Agent") ?? string.Empty,
            DateTime.UtcNow);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApprovalTimeout);
        try
        {
            var answer = approve(request, timeout.Token);
            var finished = await Task.WhenAny(answer, Task.Delay(Timeout.Infinite, timeout.Token)) == answer;
            return finished && answer.IsCompletedSuccessfully && answer.Result;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Asking the host about a control request failed", ex);
            return false;
        }
    }

    private async Task PumpControlFramesAsync(Connection socket, byte[] frameKey, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var windowStart = DateTime.UtcNow;
        var eventCount = 0;
        long lastSequence = 0;

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadDataFrameAsync(socket, buffer, MaxControlFrameBytes, ct);
            if (frame == null) return;                       // closed, idle, or oversized

            var (opcode, payload) = frame.Value;
            if (opcode != 0x1) continue;                     // only text carries input

            // <seq>|<sealed event>: an injected, replayed, reordered or altered frame ends control.
            if (!TryOpenControlFrame(Encoding.UTF8.GetString(payload), frameKey, ref lastSequence, out var eventJson))
            {
                _logService.LogWarning("[ShareScreen] A control frame failed its integrity or ordering check. Disconnecting the controller.");
                return;
            }

            var now = DateTime.UtcNow;
            if (now - windowStart > TimeSpan.FromSeconds(1))
            {
                windowStart = now;
                eventCount = 0;
            }

            eventCount++;
            if (eventCount > ControlEventsAbusePerSecond)
            {
                _logService.LogWarning("[ShareScreen] Excessive input rate from controller detected. Disconnecting controller.");
                return;
            }

            if (eventCount > ControlEventsPerSecond)
            {
                continue;
            }

            ApplyEvent(eventJson);
        }
    }

    internal static bool TryOpenControlFrame(string frame, byte[] frameKey, ref long lastSequence, out string eventJson)
    {
        eventJson = string.Empty;
        var bar = frame.IndexOf('|');
        if (bar <= 0) return false;

        if (!long.TryParse(frame.AsSpan(0, bar), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sequence) || sequence <= lastSequence)
            return false;

        if (!ShareScreenCrypto.TryOpenControlFrame(frameKey, sequence, frame[(bar + 1)..], out var json)) return false;

        lastSequence = sequence;
        eventJson = json;
        return true;
    }

    /// <summary>Turns one JSON message into desktop input. Malformed messages are simply ignored.</summary>
    internal void ApplyEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var typeProperty)) return;

            switch (typeProperty.GetString())
            {
                case "m":
                    _input.MoveMouse(root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble());
                    break;
                case "d":
                    _input.MouseButton(root.GetProperty("b").GetInt32(), pressed: true);
                    break;
                case "u":
                    _input.MouseButton(root.GetProperty("b").GetInt32(), pressed: false);
                    break;
                case "w":
                    _input.Scroll(root.GetProperty("d").GetInt32());
                    break;
                case "k":
                    _input.Key((ushort)root.GetProperty("c").GetInt32(), root.GetProperty("p").GetInt32() == 1);
                    break;
                case "r":
                    // The viewer's window lost focus; let go of anything still held.
                    _input.ReleaseHeld();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            // Malformed event, number overflow, or type mismatch; safely ignored.
        }
    }

    // -- Frames, pings and idle time ----------------------------------------

    /// <summary>
    /// Pings the socket on <see cref="PingInterval"/>. Browsers and <c>ClientWebSocket</c> answer
    /// pings by themselves, so a live client always has an inbound frame inside the idle limit.
    /// </summary>
    private CancellationTokenSource StartPinger(Connection socket, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interval = PingInterval;
        _ = Task.Run(async () =>
        {
            var ping = EncodeFrame(Array.Empty<byte>(), 0x9);
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(interval, cts.Token);
                    SendFrame(socket, ping);
                }
            }
            catch (Exception ex) when (Connection.IsGone(ex))
            {
                // Cancelled or the socket is gone; the reader notices on its own.
            }
        });
        return cts;
    }

    /// <summary>
    /// Reads the next data or close frame, answering pings and skipping pongs. Returns null on
    /// close, oversize, end of stream, or <see cref="IdleTimeout"/> without any inbound frame.
    /// </summary>
    private async Task<(int Opcode, byte[] Payload)?> ReadDataFrameAsync(
        Connection socket, byte[] scratch, int maxPayloadBytes, CancellationToken ct)
    {
        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(IdleTimeout);

            (int Opcode, byte[] Payload)? frame;
            try
            {
                frame = await ReadFrameAsync(socket, scratch, maxPayloadBytes, idleCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logService.LogInfo("[ShareScreen] Closing an idle room connection.");
                return null;
            }

            if (frame == null || frame.Value.Opcode == 0x8) return null;
            if (frame.Value.Opcode == 0x9)
            {
                try { SendFrame(socket, EncodeFrame(frame.Value.Payload, 0xA)); } catch (Exception ex) when (Connection.IsGone(ex)) { return null; /* the peer went while we answered its ping */ }
                continue;
            }
            if (frame.Value.Opcode == 0xA) continue;
            return frame;
        }
    }

    /// <summary>Reads one WebSocket frame from a socket, unmasking it. Returns null when peer gone or oversized.</summary>
    private static async Task<(int Opcode, byte[] Payload)?> ReadFrameAsync(
        Connection socket, byte[] scratch, int maxPayloadBytes, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(socket, header, 2, ct)) return null;

        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(socket, ext, 2, ct)) return null;
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(socket, ext, 8, ct)) return null;
            length = 0;
            for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
        }

        // Input messages and media frames are bounded; anything larger is rejected.
        if (length < 0 || length > maxPayloadBytes) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(socket, mask, 4, ct)) return null;

        var payload = length <= scratch.Length ? scratch : new byte[length];
        if (!await ReadExactAsync(socket, payload, (int)length, ct)) return null;

        if (masked)
        {
            for (int i = 0; i < length; i++) payload[i] ^= mask[i % 4];
        }

        return (opcode, payload[..(int)length]);
    }

    /// <summary>Reads one WebSocket frame from an arbitrary Stream, unmasking it. Returns null when EOF or oversized.</summary>
    internal static async Task<(int Opcode, byte[] Payload)?> ReadFrameAsync(
        Stream stream, byte[] scratch, int maxPayloadBytes, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, 2, ct)) return null;

        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        long length = header[1] & 0x7F;

        if (length == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, 2, ct)) return null;
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, 8, ct)) return null;
            length = 0;
            for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
        }

        if (length < 0 || length > maxPayloadBytes) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, 4, ct)) return null;

        var payload = length <= scratch.Length ? scratch : new byte[length];
        if (!await ReadExactAsync(stream, payload, (int)length, ct)) return null;

        if (masked)
        {
            for (int i = 0; i < length; i++) payload[i] ^= mask[i % 4];
        }

        return (opcode, payload[..(int)length]);
    }

    private static async Task<bool> ReadExactAsync(Connection socket, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var got = await socket.ReceiveAsync(buffer.AsMemory(offset + read, count - read), SocketFlags.None, ct);
            if (got == 0) return false;
            read += got;
        }
        return true;
    }

    private static async Task<bool> ReadExactAsync(Connection socket, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var got = await socket.ReceiveAsync(buffer.AsMemory(read, count - read), SocketFlags.None, ct);
            if (got == 0) return false;
            read += got;
        }
        return true;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (got == 0) return false;
            read += got;
        }
        return true;
    }

    internal const int MaxQueryParameters = 200;

    internal static Dictionary<string, string> ParseQuery(string query, int maxParameters = MaxQueryParameters)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= maxParameters) break;
            var equals = pair.IndexOf('=');
            if (equals > 0) result[pair[..equals]] = WebUtility.UrlDecode(pair[(equals + 1)..]);
        }
        return result;
    }

    private static int FindFreePort(int preferred)
    {
        for (int port = preferred; port < preferred + 50; port++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Any, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // Taken; try the next.
            }
        }
        return preferred;
    }

    public void BroadcastChatMessage(ShareChatMessage message)
    {
        try
        {
            var room = _room;
            if (room == null) return;
            var payload = RoomMessageCrypto.Encrypt(message, room.ViewerToken);
            var frame = EncodeTextFrame(payload);

            List<Connection> sockets;
            lock (_chatLock) { sockets = _chatSockets.ToList(); }

            foreach (var s in sockets)
            {
                try { SendFrame(s, frame); }
                catch (Exception ex) when (Connection.IsGone(ex)) { /* A dead peer is removed by its own reader loop. */ }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not broadcast chat message", ex);
        }
    }

    private static byte[] EncodeTextFrame(byte[] payload)
        => EncodeFrame(payload, 0x1);

    private static byte[] EncodeBinaryFrame(byte[] payload)
        => EncodeFrame(payload, 0x2);

    private static void SendFrame(Connection connection, byte[] frame) => connection.Write(frame);

    private static byte[] EncodeFrame(byte[] payload, int opcode)
    {
        if (payload.Length <= 125)
        {
            var frame = new byte[payload.Length + 2];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            return frame;
        }
        else if (payload.Length <= 65535)
        {
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = 126;
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)(payload.Length & 0xFF);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }
        else
        {
            var frame = new byte[payload.Length + 10];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = 127;
            for (int i = 0; i < 8; i++)
            {
                frame[2 + i] = (byte)(((long)payload.Length >> (56 - i * 8)) & 0xFF);
            }
            Buffer.BlockCopy(payload, 0, frame, 10, payload.Length);
            return frame;
        }
    }

    // -- Chat socket ---------------------------------------------------------

    private async Task HandleChatSocketAsync(
        Connection socket, ShareRoom room, Dictionary<string, string> headers, string query, CancellationToken ct)
    {
        if (!IsAllowedOrigin(headers.GetValueOrDefault("Origin"), headers))
        {
            _logService.LogWarning("[ShareScreen] Chat WebSocket rejected: cross-site origin.");
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        if (!TryAdmit(query, ShareScreenCrypto.ChatChannel, _authKey, out _))
        {
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        lock (_chatLock)
        {
            if (_chatSockets.Count >= MaxChatSockets)
            {
                _logService.LogWarning("[ShareScreen] Chat connection rejected: maximum connection limit reached.");
                _ = SendSimpleAsync(socket, 503, "Service Unavailable", ct);
                socket.Close();
                return;
            }
            _chatSockets.Add(socket);
        }

        var participantId = "chat-" + Guid.NewGuid().ToString("N")[..8];
        using var pinger = StartPinger(socket, ct);
        try
        {
            if (!await CompleteHandshakeAsync(socket, headers, ct)) return;

            var endpoint = socket.RemoteEndPoint?.ToString() ?? "unknown";
            _participants[participantId] = new ShareParticipant(participantId, endpoint, "Chat/Viewer", DateTime.UtcNow);
            ParticipantsChanged?.Invoke(this, EventArgs.Empty);

            var buffer = new byte[8192];
            var windowStart = DateTime.UtcNow;
            var frames = 0;
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadDataFrameAsync(socket, buffer, MaxChatFrameBytes, ct);
                if (frame == null) break;

                if (DateTime.UtcNow - windowStart > TimeSpan.FromSeconds(1)) { windowStart = DateTime.UtcNow; frames = 0; }
                if (++frames > ChatFramesAbusePerSecond)
                {
                    _logService.LogWarning("[ShareScreen] Chat frame flood. Disconnecting the participant.");
                    break;
                }

                var (opcode, payload) = frame.Value;
                if (opcode != 0x1) continue;

                if (RoomMessageCrypto.TryDecrypt(payload, room.ViewerToken, _chatReplayFilter, out var msg, out _) &&
                    msg != null && IsAcceptedChatPayload(msg))
                {
                    ChatMessageReceived?.Invoke(this, msg);

                    var echoFrame = EncodeTextFrame(payload);
                    List<Connection> others;
                    lock (_chatLock) { others = _chatSockets.Where(s => s != socket).ToList(); }
                    foreach (var other in others)
                    {
                        try { SendFrame(other, echoFrame); }
                        catch (Exception ex) when (Connection.IsGone(ex)) { /* A dead peer is removed by its own reader loop. */ }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { /* The peer disconnected or the room closed: the normal end of this loop. */ }
        finally
        {
            pinger.Cancel();
            lock (_chatLock) { _chatSockets.Remove(socket); }
            if (_participants.TryRemove(participantId, out _)) ParticipantsChanged?.Invoke(this, EventArgs.Empty);
            socket.Close();
        }
    }

    private static bool IsAcceptedChatPayload(ShareChatMessage message)
    {
        if (message.Kind == ShareChatKinds.Text)
            return !string.IsNullOrWhiteSpace(message.Text) && message.Text.Length <= 4000;

        if (message.Kind is ShareChatKinds.FileOffer or ShareChatKinds.FileComplete)
            return IsHexId(message.AttachmentId) &&
                   message.AttachmentSize is > 0 and <= ShareAttachmentStore.MaxAttachmentBytes &&
                   message.ChunkCount is > 0 and <= ShareAttachmentStore.MaxWireChunks &&
                   message.AttachmentSha256.Length == 64 &&
                   message.AttachmentSha256.All(char.IsAsciiHexDigit);

        if (message.Kind == ShareChatKinds.FileChunk)
            return IsHexId(message.AttachmentId) &&
                   message.ChunkIndex >= 0 &&
                   message.ChunkIndex < message.ChunkCount &&
                   message.ChunkCount is > 0 and <= ShareAttachmentStore.MaxWireChunks &&
                   message.ChunkData.Length <= 32 * 1024;

        return false;
    }

    private static bool IsHexId(string value) =>
        value.Length == 32 && value.All(char.IsAsciiHexDigit);

    // -- Voice socket --------------------------------------------------------

    public void BroadcastVoicePacket(byte[] opusPacket)
    {
        var room = _room;
        if (room == null || opusPacket.Length is 0 or > 4096) return;
        var frame = EncodeBinaryFrame(RoomVoiceCrypto.Encrypt(opusPacket, room.ViewerToken));
        List<Connection> sockets;
        lock (_voiceLock) { sockets = _voiceSockets.ToList(); }
        foreach (var socket in sockets)
        {
            try { SendFrame(socket, frame); }
            catch (Exception ex) when (Connection.IsGone(ex)) { /* A dead peer is removed by its own reader loop. */ }
        }
    }

    private async Task HandleVoiceSocketAsync(
        Connection socket, ShareRoom room, Dictionary<string, string> headers, string query, CancellationToken ct)
    {
        if (!IsAllowedOrigin(headers.GetValueOrDefault("Origin"), headers))
        {
            _logService.LogWarning("[ShareScreen] Voice WebSocket rejected: cross-site origin.");
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        if (!TryAdmit(query, ShareScreenCrypto.VoiceChannel, _authKey, out _))
        {
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        lock (_voiceLock)
        {
            if (_voiceSockets.Count >= MaxVoiceSockets)
            {
                _logService.LogWarning("[ShareScreen] Voice connection rejected: maximum connection limit reached.");
                _ = SendSimpleAsync(socket, 503, "Service Unavailable", ct);
                socket.Close();
                return;
            }
            _voiceSockets.Add(socket);
        }

        var participantId = "voice-" + Guid.NewGuid().ToString("N")[..8];
        using var pinger = StartPinger(socket, ct);
        try
        {
            if (!await CompleteHandshakeAsync(socket, headers, ct)) return;

            var endpoint = socket.RemoteEndPoint?.ToString() ?? "unknown";
            _participants[participantId] = new ShareParticipant(participantId, endpoint, "Voice", DateTime.UtcNow);
            ParticipantsChanged?.Invoke(this, EventArgs.Empty);

            var scratch = new byte[4096];
            var windowStart = DateTime.UtcNow;
            var frames = 0;
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadDataFrameAsync(socket, scratch, MaxVoiceFrameBytes, ct);
                if (frame == null) break;

                if (DateTime.UtcNow - windowStart > TimeSpan.FromSeconds(1)) { windowStart = DateTime.UtcNow; frames = 0; }
                if (++frames > VoiceFramesAbusePerSecond)
                {
                    _logService.LogWarning("[ShareScreen] Voice frame flood. Disconnecting the participant.");
                    break;
                }

                if (frame.Value.Opcode != 0x2) continue;
                if (!RoomVoiceCrypto.TryDecrypt(frame.Value.Payload, room.ViewerToken, _voiceReplayFilter, out var opusPacket, out _) || opusPacket.Length > 4096)
                    continue;

                VoicePacketReceived?.Invoke(this, opusPacket);

                var relay = EncodeBinaryFrame(frame.Value.Payload);
                List<Connection> peers;
                lock (_voiceLock) { peers = _voiceSockets.Where(peer => peer != socket).ToList(); }
                foreach (var peer in peers)
                {
                    try { SendFrame(peer, relay); }
                    catch (Exception ex) when (Connection.IsGone(ex)) { /* A dead peer is removed by its own reader loop. */ }
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { /* The peer disconnected or the room closed: the normal end of this loop. */ }
        finally
        {
            pinger.Cancel();
            lock (_voiceLock) { _voiceSockets.Remove(socket); }
            if (_participants.TryRemove(participantId, out _)) ParticipantsChanged?.Invoke(this, EventArgs.Empty);
            socket.Close();
        }
    }

    public void Stop()
    {
        DisableControl();

        lock (_chatLock)
        {
            foreach (var s in _chatSockets) s.Close();
            _chatSockets.Clear();
        }

        lock (_voiceLock)
        {
            foreach (var s in _voiceSockets) s.Close();
            _voiceSockets.Clear();
        }

        _chatReplayFilter.Reset();
        _voiceReplayFilter.Reset();
        _nonces.Reset();
        _participants.Clear();
        ParticipantsChanged?.Invoke(this, EventArgs.Empty);

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Stop(); }
        catch (SocketException) { /* Best-effort: the listener is being discarded either way. */ }
        _listener = null;
        _room = null;
        _authKey = null;
        _mediaAuthPath = string.Empty;
        _tls = null;
    }

    public void Dispose()
    {
        try
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
        catch (Exception)
        {
            // Best-effort teardown: subscribing may have failed in the first place (see the constructor).
        }

        Stop();
    }
}

/// <summary>A viewer with a valid code is asking to take control; the host answers yes or no.</summary>
public sealed record ControlApprovalRequest(string Endpoint, string UserAgent, DateTime RequestedAt);

/// <summary>
/// One client: the socket (identity, peeking, closing) and the stream all traffic goes through,
/// plain or TLS. Writes are serialized, because a broadcast, a ping and a handshake reply can
/// all want the same connection at once.
/// </summary>
internal sealed class Connection
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public Connection(Socket socket, Stream stream, bool secure)
    {
        Socket = socket;
        Stream = stream;
        Secure = secure;
    }

    public Socket Socket { get; }
    public Stream Stream { get; }
    public bool Secure { get; }
    public EndPoint? RemoteEndPoint => Socket.RemoteEndPoint;

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, SocketFlags flags, CancellationToken ct) => Stream.ReadAsync(buffer, ct);

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, SocketFlags flags, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await Stream.WriteAsync(data, ct);
            await Stream.FlushAsync(ct);
            return data.Length;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Write(byte[] frame)
    {
        _writeLock.Wait();
        try
        {
            Stream.Write(frame, 0, frame.Length);
            Stream.Flush();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Closes the stream and socket. Safe to call more than once and on a broken connection.</summary>
    public void Close()
    {
        // A TLS stream sends close_notify on dispose, which fails on a reset connection.
        try { Stream.Dispose(); } catch (Exception ex) when (IsGone(ex)) { /* already broken */ }
        CloseQuietly(Socket);
    }

    /// <summary>Closes a socket that may already be closed or reset.</summary>
    public static void CloseQuietly(Socket socket)
    {
        try { socket.Close(); } catch (Exception ex) when (IsGone(ex)) { /* already closed */ }
    }

    /// <summary>
    /// What a closed, reset, timed-out or cancelled connection throws. Anything else is a defect and
    /// is not swallowed by the best-effort sends and closes.
    /// </summary>
    public static bool IsGone(Exception ex) =>
        ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException
            or AuthenticationException or InvalidOperationException;
}
