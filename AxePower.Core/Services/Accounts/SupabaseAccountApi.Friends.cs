using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Friends;

namespace AxePower.Core.Services.Accounts;

/// <summary>
/// The Friends calls (F1's tables and functions). Every one runs as the signed-in user, so Row
/// Level Security decides what comes back; nothing here can see beyond what the rules allow.
/// </summary>
public interface IFriendsApi
{
    Task<IReadOnlyList<Friend>> ListFriendsAsync(string accessToken, CancellationToken ct);
    Task<FriendRequestResult> SendFriendRequestAsync(string accessToken, string username, CancellationToken ct);
    Task RespondToRequestAsync(string accessToken, string friendshipId, bool accept, CancellationToken ct);
    Task RemoveFriendAsync(string accessToken, string userId, CancellationToken ct);
    Task BlockAsync(string accessToken, string userId, CancellationToken ct);
    Task UnblockAsync(string accessToken, string userId, CancellationToken ct);
    Task<IReadOnlyList<BlockedUser>> ListBlockedAsync(string accessToken, CancellationToken ct);

    Task<IReadOnlyDictionary<string, FriendPresence>> GetPresenceAsync(string accessToken, IReadOnlyCollection<string> userIds, CancellationToken ct);
    Task PublishPresenceAsync(string accessToken, string userId, MyPresence presence, CancellationToken ct);

    Task<FriendInvite> SendInviteAsync(string accessToken, string toUserId, InviteKind kind, string roomLink, CancellationToken ct);
    Task<IReadOnlyList<FriendInvite>> ListIncomingInvitesAsync(string accessToken, string userId, CancellationToken ct);
    Task DismissInviteAsync(string accessToken, string inviteId, CancellationToken ct);
}

public sealed partial class SupabaseAccountApi : IFriendsApi
{
    private const string PresenceColumns = "user_id,status,activity_kind,activity,updated_at";

    public async Task<IReadOnlyList<Friend>> ListFriendsAsync(string accessToken, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "rest/v1/rpc/list_friends", new JsonObject(), accessToken, ct);
        if (json is not JsonArray rows) return Array.Empty<Friend>();

        return rows.OfType<JsonObject>().Select(row => new Friend(
            FriendshipId: Str(row, "friendship_id") ?? string.Empty,
            UserId: Str(row, "user_id") ?? string.Empty,
            Username: Str(row, "username") ?? string.Empty,
            DisplayName: Str(row, "display_name") ?? string.Empty,
            AvatarUrl: Str(row, "avatar_url"),
            State: Str(row, "state") switch
            {
                "incoming" => FriendState.Incoming,
                "outgoing" => FriendState.Outgoing,
                _ => FriendState.Friend
            },
            SinceUtc: Date(row, "since") ?? DateTime.MinValue,
            Presence: FriendPresence.Offline)).ToList();
    }

    public async Task<FriendRequestResult> SendFriendRequestAsync(string accessToken, string username, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "rest/v1/rpc/send_friend_request",
            new JsonObject { ["target_username"] = username }, accessToken, ct);
        return (json is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null) switch
        {
            "ACCEPTED" => FriendRequestResult.Accepted,
            "ALREADY_FRIENDS" => FriendRequestResult.AlreadyFriends,
            "ALREADY_REQUESTED" => FriendRequestResult.AlreadyRequested,
            _ => FriendRequestResult.Requested
        };
    }

    public Task RespondToRequestAsync(string accessToken, string friendshipId, bool accept, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "rest/v1/rpc/respond_friend_request",
            new JsonObject { ["request_id"] = friendshipId, ["accept"] = accept }, accessToken, ct);

    public Task RemoveFriendAsync(string accessToken, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "rest/v1/rpc/remove_friend", new JsonObject { ["other"] = userId }, accessToken, ct);

    public Task BlockAsync(string accessToken, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "rest/v1/rpc/block_user", new JsonObject { ["other"] = userId }, accessToken, ct);

    public Task UnblockAsync(string accessToken, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "rest/v1/rpc/unblock_user", new JsonObject { ["other"] = userId }, accessToken, ct);

    public async Task<IReadOnlyList<BlockedUser>> ListBlockedAsync(string accessToken, CancellationToken ct)
    {
        var blocks = await SendAsync(HttpMethod.Get, "rest/v1/blocks?select=blocked", null, accessToken, ct);
        var ids = (blocks as JsonArray)?.OfType<JsonObject>().Select(b => Str(b, "blocked")).OfType<string>().ToList() ?? new();
        if (ids.Count == 0) return Array.Empty<BlockedUser>();

        var profiles = await SendAsync(HttpMethod.Get,
            $"rest/v1/profiles?select=id,username,display_name&id=in.({string.Join(",", ids.Select(Uri.EscapeDataString))})",
            null, accessToken, ct);
        return (profiles as JsonArray)?.OfType<JsonObject>()
            .Select(p => new BlockedUser(Str(p, "id") ?? string.Empty, Str(p, "username") ?? string.Empty, Str(p, "display_name") ?? string.Empty))
            .ToList() ?? new List<BlockedUser>();
    }

    public async Task<IReadOnlyDictionary<string, FriendPresence>> GetPresenceAsync(
        string accessToken, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var result = new Dictionary<string, FriendPresence>(StringComparer.Ordinal);
        if (userIds.Count == 0) return result;

        var json = await SendAsync(HttpMethod.Get,
            $"rest/v1/presence?select={PresenceColumns}&user_id=in.({string.Join(",", userIds.Select(Uri.EscapeDataString))})",
            null, accessToken, ct);
        foreach (var row in (json as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (Str(row, "user_id") is { } id) result[id] = ParsePresence(row);
        }
        return result;
    }

    public Task PublishPresenceAsync(string accessToken, string userId, MyPresence presence, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "rest/v1/presence?on_conflict=user_id",
            new JsonObject
            {
                ["user_id"] = userId,
                ["status"] = presence.Status.ToString().ToLowerInvariant(),
                ["activity_kind"] = presence.Kind?.ToString().ToLowerInvariant(),
                ["activity"] = presence.Activity
            },
            accessToken, ct, prefer: "resolution=merge-duplicates");

    public async Task<FriendInvite> SendInviteAsync(string accessToken, string toUserId, InviteKind kind, string roomLink, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "rest/v1/friend_invites?select=*",
            new JsonObject { ["to_user"] = toUserId, ["kind"] = kind.ToString().ToLowerInvariant(), ["room_link"] = roomLink },
            accessToken, ct, returnRepresentation: true);
        return json is JsonArray { Count: > 0 } rows && rows[0] is JsonObject row
            ? ParseInvite(row)
            : throw new AccountException(AccountError.Unknown, "The invite could not be sent.");
    }

    public async Task<IReadOnlyList<FriendInvite>> ListIncomingInvitesAsync(string accessToken, string userId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"rest/v1/friend_invites?select=*&to_user=eq.{Uri.EscapeDataString(userId)}&order=created_at.desc", null, accessToken, ct);
        return (json as JsonArray)?.OfType<JsonObject>().Select(ParseInvite).ToList() ?? new List<FriendInvite>();
    }

    public Task DismissInviteAsync(string accessToken, string inviteId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"rest/v1/friend_invites?id=eq.{Uri.EscapeDataString(inviteId)}", null, accessToken, ct);

    // -- Parsing (shared with the Realtime client, which receives the same rows) -----------------

    internal static FriendPresence ParsePresence(JsonObject row) => new(
        Status: Str(row, "status") switch { "online" => PresenceStatus.Online, "away" => PresenceStatus.Away, _ => PresenceStatus.Offline },
        Kind: Str(row, "activity_kind") switch { "game" => ActivityKind.Game, "call" => ActivityKind.Call, "sharing" => ActivityKind.Sharing, _ => null },
        Activity: Str(row, "activity"),
        UpdatedAtUtc: Date(row, "updated_at") ?? DateTime.MinValue);

    internal static FriendInvite ParseInvite(JsonObject row) => new(
        Id: Str(row, "id") ?? string.Empty,
        FromUserId: Str(row, "from_user") ?? string.Empty,
        ToUserId: Str(row, "to_user") ?? string.Empty,
        Kind: Str(row, "kind") == "watch" ? InviteKind.Watch : InviteKind.Call,
        RoomLink: Str(row, "room_link") ?? string.Empty,
        CreatedAtUtc: Date(row, "created_at") ?? DateTime.MinValue,
        ExpiresAtUtc: Date(row, "expires_at") ?? DateTime.MinValue);
}
