using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Serves the viewer page and carries remote input back from it.
///
/// It speaks HTTP and WebSocket over a raw socket rather than using <c>HttpListener</c>, because
/// binding anything but loopback with HttpListener needs either administrator rights or a
/// pre-registered URL reservation, and AxePower is meant to run as a standard user.
///
/// Control is gated here as well as in <see cref="RemoteInputService"/>: the socket is refused
/// unless it presents the session's control code, and only one viewer may hold control at a time.
/// </summary>
public sealed class ShareViewerHost : IDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly LogService _logService;
    private readonly RemoteInputService _input;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private ShareRoom? _room;
    private int _webrtcPort;
    private string _hostName = string.Empty;

    /// <summary>The code a viewer must present to take control. Null while control is off.</summary>
    private string? _controlCode;

    /// <summary>The one socket currently holding control, if any.</summary>
    private Socket? _controller;

    public int Port { get; private set; }

    public bool IsRunning => _listener != null && _cts is { IsCancellationRequested: false };

    /// <summary>Raised when a viewer takes or drops control, with true for taken.</summary>
    public event EventHandler<bool>? ControlHolderChanged;

    private readonly List<Socket> _chatSockets = new();
    private readonly object _chatLock = new();
    private readonly List<Socket> _voiceSockets = new();
    private readonly object _voiceLock = new();

    /// <summary>Raised when a participant sends a chat message in the room.</summary>
    public event EventHandler<ShareChatMessage>? ChatMessageReceived;
    public event EventHandler<byte[]>? VoicePacketReceived;

    public ShareViewerHost(LogService logService, RemoteInputService input)
    {
        _logService = logService;
        _input = input;
    }

    public void Start(ShareRoom room, string hostName, int webrtcPort, int preferredPort)
    {
        Stop();

        _room = room;
        _hostName = hostName;
        _webrtcPort = webrtcPort;
        Port = FindFreePort(preferredPort);

        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);

        _logService.LogInfo($"[ShareScreen] Viewer page is being served on port {Port}.");
    }

    /// <summary>The link to hand a friend. View-only: it carries no control code.</summary>
    public string ViewerUrl => _room == null
        ? string.Empty
        : $"http://{_hostName}:{Port}/{_room.PathName}#v={_room.ViewerToken}";

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
            try { controller.Close(); } catch { }
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

    // -- Socket plumbing -----------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptSocketAsync(ct);
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

    private async Task HandleClientAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            var request = await ReadRequestHeadAsync(socket, ct);
            if (request == null) { socket.Close(); return; }

            var (method, target, headers) = request.Value;
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await SendSimpleAsync(socket, 405, "Method Not Allowed", ct);
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

            if (path == $"/{room.PathName}/ws")
            {
                await HandleControlSocketAsync(socket, headers, query, ct);
                return;
            }

            if (path == $"/{room.PathName}/chat")
            {
                await HandleChatSocketAsync(socket, headers, query, ct);
                return;
            }

            if (path == $"/{room.PathName}/voice")
            {
                await HandleVoiceSocketAsync(socket, headers, query, ct);
                return;
            }

            await SendSimpleAsync(socket, 404, "Not found", ct);
            socket.Close();
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Viewer request failed", ex);
            try { socket.Close(); } catch { }
        }
    }

    private async Task<(string Method, string Target, Dictionary<string, string> Headers)?> ReadRequestHeadAsync(
        Socket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var used = 0;

        while (used < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(used), SocketFlags.None, ct);
            if (read == 0) return null;
            used += read;

            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var lines = text[..end].Split("\r\n");
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

        return null;
    }

    private async Task SendPageAsync(
        Socket socket,
        ShareRoom room,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var requestHost = headers.GetValueOrDefault("Host") ?? string.Empty;
        var mediaHost = requestHost.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        requestHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : _hostName;
        var html = ShareViewerPage.Html
            .Replace("__TITLE__", $"AxePower - room {room.FormattedCode}")
            .Replace("__NAME__", WebUtility.HtmlEncode(Environment.UserName))
            .Replace("__WHEP__", $"http://{mediaHost}:{_webrtcPort}/{room.PathName}/whep")
            .Replace("__WS__", $"ws://{mediaHost}:{Port}/{room.PathName}/ws");

        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        await socket.SendAsync(head, SocketFlags.None, ct);
        await socket.SendAsync(body, SocketFlags.None, ct);
    }

    private static async Task SendSimpleAsync(Socket socket, int status, string reason, CancellationToken ct)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try { await socket.SendAsync(head, SocketFlags.None, ct); } catch { }
    }

    // -- Control socket ------------------------------------------------------

    private async Task HandleControlSocketAsync(
        Socket socket, Dictionary<string, string> headers, string query, CancellationToken ct)
    {
        var code = _controlCode;
        var supplied = ParseQuery(query).GetValueOrDefault("k");

        // Refuse before upgrading, so a wrong code never reaches the input path at all.
        if (string.IsNullOrEmpty(code) ||
            string.IsNullOrEmpty(supplied) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(code)))
        {
            _logService.LogWarning("[ShareScreen] A viewer tried to take control with a wrong or missing code.");
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await SendSimpleAsync(socket, 400, "Bad Request", ct);
            socket.Close();
            return;
        }

        // One controller at a time: two people fighting over one mouse helps nobody.
        var previous = Interlocked.Exchange(ref _controller, socket);
        if (previous != null) { try { previous.Close(); } catch { } }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await socket.SendAsync(response, SocketFlags.None, ct);

        _logService.LogWarning("[ShareScreen] A viewer has TAKEN CONTROL of this PC.");
        ControlHolderChanged?.Invoke(this, true);

        try
        {
            await PumpControlFramesAsync(socket, ct);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // An ordinary disconnect.
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
            Interlocked.CompareExchange(ref _controller, null, socket);
            _input.ReleaseHeld();
            _logService.LogWarning("[ShareScreen] The viewer has released control.");
            ControlHolderChanged?.Invoke(this, false);
            try { socket.Close(); } catch { }
        }
    }

    private async Task PumpControlFramesAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(socket, buffer, ct);
            if (frame == null) return;                       // closed

            var (opcode, payload) = frame.Value;
            if (opcode == 0x8) return;                       // close
            if (opcode != 0x1) continue;                     // only text carries input

            ApplyEvent(Encoding.UTF8.GetString(payload));
        }
    }

    /// <summary>Turns one JSON message into desktop input. Malformed messages are simply ignored.</summary>
    private void ApplyEvent(string json)
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
        catch (JsonException)
        {
            // Not worth logging: a viewer can send anything, and we simply do not act on it.
        }
        catch (KeyNotFoundException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Reads one WebSocket frame, unmasking it. Returns null when the peer has gone.</summary>
    private static async Task<(int Opcode, byte[] Payload)?> ReadFrameAsync(Socket socket, byte[] scratch, CancellationToken ct)
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

        // Input messages are tiny; anything large is malformed or hostile.
        if (length < 0 || length > 64 * 1024) return null;

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

    private static async Task<bool> ReadExactAsync(Socket socket, byte[] buffer, int count, CancellationToken ct)
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

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
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

            List<Socket> sockets;
            lock (_chatLock) { sockets = _chatSockets.ToList(); }

            foreach (var s in sockets)
            {
                try { SendFrame(s, frame); } catch { }
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

    private static void SendFrame(Socket socket, byte[] frame)
    {
        lock (socket)
        {
            var sent = 0;
            while (sent < frame.Length)
            {
                var count = socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                if (count <= 0) throw new IOException("The WebSocket connection closed during send.");
                sent += count;
            }
        }
    }

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
                frame[2 + i] = (byte)((payload.Length >> (56 - i * 8)) & 0xFF);
            }
            Buffer.BlockCopy(payload, 0, frame, 10, payload.Length);
            return frame;
        }
    }

    private async Task HandleChatSocketAsync(Socket socket, Dictionary<string, string> headers, string query, CancellationToken ct)
    {
        var room = _room;
        var suppliedToken = ParseQuery(query).GetValueOrDefault("v");
        if (room == null || string.IsNullOrWhiteSpace(suppliedToken) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(suppliedToken), Encoding.UTF8.GetBytes(room.ViewerToken)))
        {
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await SendSimpleAsync(socket, 400, "Bad Request", ct);
            socket.Close();
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await socket.SendAsync(response, SocketFlags.None, ct);

        lock (_chatLock) { _chatSockets.Add(socket); }

        try
        {
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(socket, buffer, ct);
                if (frame == null) break;

                var (opcode, payload) = frame.Value;
                if (opcode == 0x8) break;
                if (opcode != 0x1) continue;

                try
                {
                    if (RoomMessageCrypto.TryDecrypt(payload, room.ViewerToken, out var msg) &&
                        msg != null && IsAcceptedChatPayload(msg))
                    {
                        ChatMessageReceived?.Invoke(this, msg);

                        var echoFrame = EncodeTextFrame(payload);
                        List<Socket> others;
                        lock (_chatLock) { others = _chatSockets.Where(s => s != socket).ToList(); }
                        foreach (var other in others)
                        {
                            try { SendFrame(other, echoFrame); } catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            lock (_chatLock) { _chatSockets.Remove(socket); }
            try { socket.Close(); } catch { }
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

    public void BroadcastVoicePacket(byte[] opusPacket)
    {
        var room = _room;
        if (room == null || opusPacket.Length is 0 or > 4096) return;
        var frame = EncodeBinaryFrame(RoomVoiceCrypto.Encrypt(opusPacket, room.ViewerToken));
        List<Socket> sockets;
        lock (_voiceLock) { sockets = _voiceSockets.ToList(); }
        foreach (var socket in sockets)
        {
            try { SendFrame(socket, frame); } catch { }
        }
    }

    private async Task HandleVoiceSocketAsync(Socket socket, Dictionary<string, string> headers, string query, CancellationToken ct)
    {
        var room = _room;
        var token = ParseQuery(query).GetValueOrDefault("v");
        if (room == null || string.IsNullOrWhiteSpace(token) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(room.ViewerToken)))
        {
            await SendSimpleAsync(socket, 403, "Forbidden", ct);
            socket.Close();
            return;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            await SendSimpleAsync(socket, 400, "Bad Request", ct);
            socket.Close();
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await socket.SendAsync(response, SocketFlags.None, ct);
        lock (_voiceLock) { _voiceSockets.Add(socket); }

        try
        {
            var scratch = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(socket, scratch, ct);
                if (frame == null || frame.Value.Opcode == 0x8) break;
                if (frame.Value.Opcode != 0x2) continue;
                if (!RoomVoiceCrypto.TryDecrypt(frame.Value.Payload, room.ViewerToken, out var opusPacket) || opusPacket.Length > 4096)
                    continue;

                VoicePacketReceived?.Invoke(this, opusPacket);

                var relay = EncodeBinaryFrame(frame.Value.Payload);
                List<Socket> peers;
                lock (_voiceLock) { peers = _voiceSockets.Where(peer => peer != socket).ToList(); }
                foreach (var peer in peers)
                {
                    try { SendFrame(peer, relay); } catch { }
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { }
        finally
        {
            lock (_voiceLock) { _voiceSockets.Remove(socket); }
            try { socket.Close(); } catch { }
        }
    }

    public void Stop()
    {
        DisableControl();

        lock (_chatLock)
        {
            foreach (var s in _chatSockets) { try { s.Close(); } catch { } }
            _chatSockets.Clear();
        }

        lock (_voiceLock)
        {
            foreach (var s in _voiceSockets) { try { s.Close(); } catch { } }
            _voiceSockets.Clear();
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Stop(); } catch { }
        _listener = null;
        _room = null;
    }

    public void Dispose() => Stop();
}
