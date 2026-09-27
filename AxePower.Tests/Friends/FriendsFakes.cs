using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Friends;

namespace AxePower.Tests.Friends;

// Shared by the Friends engine and Friends page tests.

internal sealed class FakeFeed : IRealtimeFeed
{
    public event EventHandler<RealtimeChange>? Changed;
    public event EventHandler<bool>? ConnectionChanged;
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public string? UserId { get; private set; }

    public void Start(string userId, Func<CancellationToken, Task<string>> accessToken) { Started = true; UserId = userId; }
    public void Push(RealtimeChange change) => Changed?.Invoke(this, change);
    public void Connect(bool on) => ConnectionChanged?.Invoke(this, on);
    public void Dispose() => Disposed = true;
}

internal sealed class FakeFriendsApi : IFriendsApi
{
    public static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
    public const string Me = "u-1";

    public List<string> Calls { get; } = new();
    public IReadOnlyList<Friend> FriendsList { get; set; } = Array.Empty<Friend>();
    public Dictionary<string, FriendPresence> Presence { get; } = new();
    public List<MyPresence> Published { get; } = new();
    public List<string> PresenceAskedFor { get; } = new();
    public string? LastRequestedUsername { get; private set; }
    public Action OnSendRequest { get; set; } = () => { };
    public List<ProfileSuggestion> SearchResults { get; } = new();
    public List<string> Searches { get; } = new();

    public Task<IReadOnlyList<ProfileSuggestion>> SearchProfilesAsync(string accessToken, string query, CancellationToken ct)
    { lock (Calls) Searches.Add(query); return Task.FromResult<IReadOnlyList<ProfileSuggestion>>(SearchResults.ToList()); }
    public FriendRequestResult RequestResult { get; set; } = FriendRequestResult.Requested;

    public Task<IReadOnlyList<Friend>> ListFriendsAsync(string accessToken, CancellationToken ct)
    { lock (Calls) Calls.Add("ListFriends"); return Task.FromResult(FriendsList); }

    public Task<FriendRequestResult> SendFriendRequestAsync(string accessToken, string username, CancellationToken ct)
    { lock (Calls) Calls.Add("SendRequest"); OnSendRequest(); LastRequestedUsername = username; return Task.FromResult(RequestResult); }

    public Task RespondToRequestAsync(string accessToken, string friendshipId, bool accept, CancellationToken ct)
    { lock (Calls) Calls.Add($"Respond:{friendshipId}:{accept}"); return Task.CompletedTask; }
    public Task RemoveFriendAsync(string accessToken, string userId, CancellationToken ct)
    { lock (Calls) Calls.Add($"Remove:{userId}"); return Task.CompletedTask; }
    public Task BlockAsync(string accessToken, string userId, CancellationToken ct)
    { lock (Calls) Calls.Add($"Block:{userId}"); return Task.CompletedTask; }
    public Task UnblockAsync(string accessToken, string userId, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<BlockedUser>> ListBlockedAsync(string accessToken, CancellationToken ct) => Task.FromResult<IReadOnlyList<BlockedUser>>(Array.Empty<BlockedUser>());

    public Task<IReadOnlyDictionary<string, FriendPresence>> GetPresenceAsync(string accessToken, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        lock (Calls) { PresenceAskedFor.Clear(); PresenceAskedFor.AddRange(userIds); }
        return Task.FromResult<IReadOnlyDictionary<string, FriendPresence>>(Presence.Where(p => userIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }

    public Task PublishPresenceAsync(string accessToken, string userId, MyPresence presence, CancellationToken ct)
    { lock (Calls) Published.Add(presence); return Task.CompletedTask; }

    public Task<FriendInvite> SendInviteAsync(string accessToken, string toUserId, InviteKind kind, string roomLink, CancellationToken ct)
    {
        lock (Calls) Calls.Add($"Invite:{toUserId}:{kind}:{roomLink}");
        return Task.FromResult(new FriendInvite("i", FakeFriendsApi.Me, toUserId, kind, roomLink, FakeFriendsApi.Now, FakeFriendsApi.Now.AddMinutes(10)));
    }

    public Task<IReadOnlyList<FriendInvite>> ListIncomingInvitesAsync(string accessToken, string userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FriendInvite>>(Array.Empty<FriendInvite>());

    public Task DismissInviteAsync(string accessToken, string inviteId, CancellationToken ct)
    { lock (Calls) Calls.Add($"Dismiss:{inviteId}"); return Task.CompletedTask; }
}
