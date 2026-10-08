using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// The joining side of a friend's room: the chat and voice sockets, their admission (the viewer
/// token is proved, never sent), decryption with replay windows, and encrypted sending. Events
/// arrive on background threads; the caller marshals to the UI if it needs to.
/// </summary>
public sealed class ShareRoomSession : IDisposable
{
    public delegate Task<Uri> Admission(Uri roomUri, string viewerToken, string channel, CancellationToken ct);

    private const int MaxChatMessageBytes = 64 * 1024;
    private const int MaxVoicePacketBytes = 8192;

    private readonly LogService _log;
    private readonly Admission _admit;
    private readonly SemaphoreSlim _voiceSendGate = new(1, 1);

    // A joined room's chat and voice are checked for replays here too, not only on the host.
    private readonly ReplayFilter _chatReplay = new();
    private readonly ReplayFilter _voiceReplay = new();

    private ClientWebSocket? _chatSocket;
    private CancellationTokenSource? _chatCts;
    private ClientWebSocket? _voiceSocket;
    private CancellationTokenSource? _voiceCts;

    public ShareRoomSession(LogService log, Admission? admit = null)
    {
        _log = log;
        _admit = admit ?? ((room, token, channel, ct) => ShareRoomClient.BuildAdmittedSocketUriAsync(room, token, channel, ct));
    }

    /// <summary>The room's viewer token, from the joined link. Empty when not joined or the link had none.</summary>
    public string Secret { get; private set; } = string.Empty;

    public bool HasSecret => !string.IsNullOrWhiteSpace(Secret);
    public bool IsChatOpen => _chatSocket?.State == WebSocketState.Open;
    public bool IsVoiceOpen => _voiceSocket?.State == WebSocketState.Open;

    /// <summary>A decrypted chat or file message from the room; never marked as ours.</summary>
    public event EventHandler<ShareChatMessage>? ChatReceived;

    /// <summary>A decrypted Opus packet from the room.</summary>
    public event EventHandler<byte[]>? VoiceReceived;

    /// <summary>True when the voice socket opens, false when it ends.</summary>
    public event EventHandler<bool>? VoiceConnectionChanged;

    /// <summary>
    /// The room's chat connection ended for a reason other than leaving: the argument says why in
    /// words for the user (refused admission, a full room, a lost connection).
    /// </summary>
    public event EventHandler<string>? ChatEnded;

    /// <summary>Takes the room's secret from <paramref name="roomUri"/> and opens the chat socket.</summary>
    public void JoinChat(Uri roomUri)
    {
        CloseChat();
        Secret = ShareRoomLink.ParseViewerSecret(roomUri);
        _chatReplay.Reset();
        if (!HasSecret) return;

        var cts = new CancellationTokenSource();
        _chatCts = cts;
        var secret = Secret;
        _ = Task.Run(() => RunChatAsync(roomUri, secret, cts.Token));
    }

    /// <summary>Opens the voice socket for the joined room. False when the link carried no secret.</summary>
    public bool JoinVoice(Uri roomUri)
    {
        CloseVoice();
        if (!HasSecret) return false;

        _voiceReplay.Reset();
        var cts = new CancellationTokenSource();
        _voiceCts = cts;
        var secret = Secret;
        _ = Task.Run(() => RunVoiceAsync(roomUri, secret, cts.Token));
        return true;
    }

    private async Task RunChatAsync(Uri roomUri, string secret, CancellationToken ct)
    {
        try
        {
            var chatUri = await _admit(roomUri, secret, ShareScreenCrypto.ChatChannel, ct);
            var ws = new ClientWebSocket();
            _chatSocket = ws;
            await ws.ConnectAsync(chatUri, ct);

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var (type, data) = await ReceiveMessageAsync(ws, MaxChatMessageBytes, ct);
                if (type == WebSocketMessageType.Close) break;
                if (type != WebSocketMessageType.Text || data == null) continue;

                try
                {
                    if (RoomMessageCrypto.TryDecrypt(data, secret, _chatReplay, out var msg, out _) && msg != null)
                        ChatReceived?.Invoke(this, msg with { IsSelf = false });
                }
                catch (JsonException)
                {
                    // Authenticated but not a chat message: dropped. The content is never logged.
                    _log.LogWarning("[Friends] Dropped a room message that was not valid chat data.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user left the room.
        }
        catch (Exception ex)
        {
            _log.LogWarning("[Friends] Room chat connection ended", ex);
            ChatEnded?.Invoke(this, DescribeChatFailure(ex));
        }
    }

    /// <summary>What to tell the user when the chat connection fails.</summary>
    public static string DescribeChatFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound } =>
            "The room refused this link. It may have closed, or the link is out of date: ask your friend for a new one.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests } =>
            "The room is busy right now. Try joining again in a moment.",
        HttpRequestException or WebSocketException or System.IO.IOException or OperationCanceledException =>
            "Lost the connection to the room. Check that you are on the same network (or Tailscale) and join again.",
        _ => "Chat stopped unexpectedly. The details are in Logs.",
    };

    private async Task RunVoiceAsync(Uri roomUri, string secret, CancellationToken ct)
    {
        try
        {
            var voiceUri = await _admit(roomUri, secret, ShareScreenCrypto.VoiceChannel, ct);
            var ws = new ClientWebSocket();
            _voiceSocket = ws;
            await ws.ConnectAsync(voiceUri, ct);
            VoiceConnectionChanged?.Invoke(this, true);

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var (type, data) = await ReceiveMessageAsync(ws, MaxVoicePacketBytes, ct);
                if (type == WebSocketMessageType.Close) break;
                if (type == WebSocketMessageType.Binary && data != null &&
                    RoomVoiceCrypto.TryDecrypt(data, secret, _voiceReplay, out var opusPacket, out _))
                {
                    VoiceReceived?.Invoke(this, opusPacket);
                }
            }
        }
        catch (OperationCanceledException) { /* The user left the room. */ }
        catch (Exception ex)
        {
            _log.LogWarning($"[FriendsVoice] Voice connection ended: {ex.Message}");
        }
        finally
        {
            VoiceConnectionChanged?.Invoke(this, false);
        }
    }

    /// <summary>One whole WebSocket message, or null data when it grew past <paramref name="maxBytes"/>.</summary>
    private static async Task<(WebSocketMessageType Type, byte[]? Data)> ReceiveMessageAsync(ClientWebSocket ws, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, null);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage && message.Length <= maxBytes);

        return message.Length > maxBytes ? (result.MessageType, null) : (result.MessageType, message.ToArray());
    }

    /// <summary>Encrypts and sends one chat or file message. False when the chat socket is not open.</summary>
    public async Task<bool> SendChatAsync(ShareChatMessage message)
    {
        var socket = _chatSocket;
        if (socket?.State != WebSocketState.Open || !HasSecret) return false;
        try
        {
            var bytes = RoomMessageCrypto.Encrypt(message, Secret);
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("[Friends] Could not send room data", ex);
            return false;
        }
    }

    /// <summary>Encrypts and sends one Opus packet. False when the voice socket is not open.</summary>
    public async Task<bool> SendVoiceAsync(byte[] opusPacket)
    {
        var socket = _voiceSocket;
        if (socket?.State != WebSocketState.Open || !HasSecret) return false;

        await _voiceSendGate.WaitAsync();
        try
        {
            var encrypted = RoomVoiceCrypto.Encrypt(opusPacket, Secret);
            await socket.SendAsync(encrypted.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[FriendsVoice] Could not send voice packet: {ex.Message}");
            return false;
        }
        finally
        {
            _voiceSendGate.Release();
        }
    }

    public void CloseVoice()
    {
        _voiceCts?.Cancel();
        _voiceCts?.Dispose();
        _voiceCts = null;
        var socket = Interlocked.Exchange(ref _voiceSocket, null);
        socket?.Dispose(); // ClientWebSocket.Dispose does not throw
    }

    private void CloseChat()
    {
        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _chatCts = null;
        var socket = Interlocked.Exchange(ref _chatSocket, null);
        socket?.Dispose(); // ClientWebSocket.Dispose does not throw
    }

    /// <summary>Leaves the room: closes both sockets and forgets the secret.</summary>
    public void Leave()
    {
        CloseChat();
        CloseVoice();
        Secret = string.Empty;
    }

    public void Dispose()
    {
        Leave();
        _voiceSendGate.Dispose();
    }
}
