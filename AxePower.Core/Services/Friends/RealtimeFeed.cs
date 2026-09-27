using System;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Accounts;

namespace AxePower.Core.Services.Friends;

/// <summary>A row change pushed by Supabase Realtime: table, INSERT/UPDATE/DELETE, and the row.</summary>
public sealed record RealtimeChange(string Table, string Type, JsonObject? Record, JsonObject? OldRecord);

/// <summary>Live changes to friendships, presence and invites. Swappable so FriendsService tests need no socket.</summary>
public interface IRealtimeFeed : IDisposable
{
    event EventHandler<RealtimeChange>? Changed;

    /// <summary>True while subscribed. After a reconnect, callers should reload what they may have missed.</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Connects and keeps reconnecting until disposed.</summary>
    void Start(string userId, Func<CancellationToken, Task<string>> accessToken);
}

/// <summary>
/// The Phoenix-channel messages Supabase Realtime speaks (protocol vsn 1.0.0: one JSON object per
/// frame). Pure functions, so the wire format is testable without a socket.
/// </summary>
public static class RealtimeProtocol
{
    public const string Topic = "realtime:axepower-friends";

    /// <summary>
    /// Joins one channel listening to the three Friends tables. Row Level Security still applies to
    /// every change: a subscriber only receives rows its access token may read, so presence arrives
    /// only from friends and invites only for the invitee.
    /// </summary>
    public static string Join(string userId, string accessToken, int reference)
    {
        var changes = new JsonArray
        {
            new JsonObject { ["event"] = "*", ["schema"] = "public", ["table"] = "presence" },
            new JsonObject { ["event"] = "*", ["schema"] = "public", ["table"] = "friendships" },
            new JsonObject { ["event"] = "INSERT", ["schema"] = "public", ["table"] = "friend_invites", ["filter"] = $"to_user=eq.{userId}" },
        };

        return Message(Topic, "phx_join", new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["broadcast"] = new JsonObject { ["ack"] = false, ["self"] = false },
                ["presence"] = new JsonObject { ["key"] = string.Empty },
                ["postgres_changes"] = changes,
                ["private"] = false
            },
            ["access_token"] = accessToken
        }, reference, joinReference: reference);
    }

    public static string Heartbeat(int reference) => Message("phoenix", "heartbeat", new JsonObject(), reference);

    /// <summary>Hands the channel a refreshed token; without it the server closes the channel when the old one expires.</summary>
    public static string AccessToken(string accessToken, int reference) =>
        Message(Topic, "access_token", new JsonObject { ["access_token"] = accessToken }, reference);

    private static string Message(string topic, string @event, JsonObject payload, int reference, int? joinReference = null)
    {
        var message = new JsonObject
        {
            ["topic"] = topic,
            ["event"] = @event,
            ["payload"] = payload,
            ["ref"] = reference.ToString(CultureInfo.InvariantCulture)
        };
        if (joinReference is { } j) message["join_ref"] = j.ToString(CultureInfo.InvariantCulture);
        return message.ToJsonString();
    }

    /// <summary>
    /// <see cref="Kind.SubscriptionRefused"/>: the channel is up but the server refused one of its
    /// table subscriptions. The rest keep working, so this is logged, not a reason to reconnect.
    /// </summary>
    public enum Kind { Other, Joined, JoinFailed, SubscriptionRefused, Change, Closed }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>Classifies one incoming frame; for a change, also returns it.</summary>
    public static (Kind Kind, RealtimeChange? Change, string? Detail) Parse(string frame)
    {
        JsonObject? message;
        try { message = JsonNode.Parse(frame) as JsonObject; }
        catch (JsonException) { return (Kind.Other, null, null); }
        if (message is null) return (Kind.Other, null, null);

        var @event = Text(message["event"]);
        var topic = Text(message["topic"]);
        var payload = message["payload"] as JsonObject;

        switch (@event)
        {
            case "phx_reply" when topic == Topic:
                var status = Text(payload?["status"]);
                if (status == "ok") return (Kind.Joined, null, null);
                return (Kind.JoinFailed, null, payload?["response"]?.ToJsonString());

            case "phx_error" or "phx_close" when topic == Topic:
                return (Kind.Closed, null, @event);

            case "system" when Text(payload?["status"]) == "error":
                return (Kind.SubscriptionRefused, null, Text(payload?["message"]));

            case "postgres_changes" when (payload?["data"]) is JsonObject data:
                var table = Text(data["table"]) ?? string.Empty;
                var type = Text(data["type"]) ?? string.Empty;
                return (Kind.Change, new RealtimeChange(table, type, data["record"] as JsonObject, data["old_record"] as JsonObject), null);

            default:
                return (Kind.Other, null, null);
        }
    }
}

/// <summary>
/// The Realtime connection: one WebSocket to <c>/realtime/v1/websocket</c>, a heartbeat every 25 s
/// (the server drops silent sockets at 60 s), the access token refreshed onto the channel before it
/// expires, and reconnection with back-off (1 s doubling to 30 s) after any drop.
/// </summary>
public sealed class SupabaseRealtimeFeed : IRealtimeFeed
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan TokenCheckEvery = TimeSpan.FromMinutes(5);

    private readonly SupabaseProject _project;
    private readonly LogService _log;
    private readonly CancellationTokenSource _stop = new();
    private int _reference;

    public SupabaseRealtimeFeed(SupabaseProject project, LogService log)
    {
        _project = project;
        _log = log;
    }

    public event EventHandler<RealtimeChange>? Changed;
    public event EventHandler<bool>? ConnectionChanged;

    public void Start(string userId, Func<CancellationToken, Task<string>> accessToken) =>
        _ = RunAsync(userId, accessToken, _stop.Token);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(string userId, Func<CancellationToken, Task<string>> accessToken, CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            bool joined = false;
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.Zero; // Phoenix heartbeats do this job
                var host = _project.Url.Host;
                await socket.ConnectAsync(
                    new Uri($"wss://{host}/realtime/v1/websocket?apikey={Uri.EscapeDataString(_project.PublishableKey)}&vsn=1.0.0"), stop);

                var token = await accessToken(stop);
                await SendAsync(socket, RealtimeProtocol.Join(userId, token, Next()), stop);

                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var keepAlive = KeepAliveAsync(socket, accessToken, token, session.Token);

                await foreach (var frame in ReceiveAsync(socket, session.Token))
                {
                    var (kind, change, detail) = RealtimeProtocol.Parse(frame);
                    switch (kind)
                    {
                        case RealtimeProtocol.Kind.Joined when !joined:
                            joined = true;
                            backoff = TimeSpan.FromSeconds(1);
                            ConnectionChanged?.Invoke(this, true);
                            break;
                        case RealtimeProtocol.Kind.JoinFailed:
                            _log.LogWarning($"[Friends] Live updates refused: {detail}");
                            session.Cancel();
                            break;
                        case RealtimeProtocol.Kind.SubscriptionRefused:
                            _log.LogWarning($"[Friends] A live subscription was refused (the 5-minute reload covers it): {detail}");
                            break;
                        case RealtimeProtocol.Kind.Closed:
                            session.Cancel();
                            break;
                        case RealtimeProtocol.Kind.Change when change is not null:
                            Changed?.Invoke(this, change);
                            break;
                    }
                }

                session.Cancel();
                try { await keepAlive; } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogInfo($"[Friends] Live updates disconnected ({ex.GetType().Name}); reconnecting in {backoff.TotalSeconds:0} s.");
            }

            if (joined) ConnectionChanged?.Invoke(this, false);

            try { await Task.Delay(backoff, stop); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    private async Task KeepAliveAsync(
        ClientWebSocket socket, Func<CancellationToken, Task<string>> accessToken, string lastToken, CancellationToken ct)
    {
        var sinceTokenCheck = TimeSpan.Zero;
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(HeartbeatEvery, ct);
            await SendAsync(socket, RealtimeProtocol.Heartbeat(Next()), ct);

            sinceTokenCheck += HeartbeatEvery;
            if (sinceTokenCheck >= TokenCheckEvery)
            {
                sinceTokenCheck = TimeSpan.Zero;
                var token = await accessToken(ct); // refreshes when close to expiry
                if (token != lastToken)
                {
                    lastToken = token;
                    await SendAsync(socket, RealtimeProtocol.AccessToken(token, Next()), ct);
                }
            }
        }
    }

    private int Next() => Interlocked.Increment(ref _reference);

    // A WebSocket allows one send at a time; the heartbeat and the join/token sends share it.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private async Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async System.Collections.Generic.IAsyncEnumerable<string> ReceiveAsync(
        ClientWebSocket socket, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var frame = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer, ct); }
            catch (OperationCanceledException) { yield break; }

            if (result.MessageType == WebSocketMessageType.Close) yield break;
            frame.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
            frame.SetLength(0);
            yield return text;
        }
    }
}
