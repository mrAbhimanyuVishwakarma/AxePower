using System;

namespace AxePower.Core.Services.Friends;

public enum FriendState
{
    Friend,

    /// <summary>They asked you; you can accept or decline.</summary>
    Incoming,

    /// <summary>You asked them; waiting.</summary>
    Outgoing
}

public enum PresenceStatus
{
    Offline,
    Away,
    Online
}

public enum ActivityKind
{
    Game,
    Call,
    Sharing
}

public enum InviteKind
{
    Call,
    Watch
}

public enum FriendRequestResult
{
    /// <summary>Sent. (Also what someone who blocked you makes it look like.)</summary>
    Requested,

    /// <summary>They had already asked you, so you are now friends.</summary>
    Accepted,

    AlreadyFriends,
    AlreadyRequested
}

/// <summary>What a friend lets you see. <see cref="Effective"/> treats a stale heartbeat as offline.</summary>
public sealed record FriendPresence(PresenceStatus Status, ActivityKind? Kind, string? Activity, DateTime UpdatedAtUtc)
{
    /// <summary>A heartbeat is published every minute; after this long without one, the friend is offline.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(150);

    public static FriendPresence Offline { get; } = new(PresenceStatus.Offline, null, null, DateTime.MinValue);

    public FriendPresence Effective(DateTime nowUtc) =>
        Status != PresenceStatus.Offline && nowUtc - UpdatedAtUtc > StaleAfter ? Offline : this;
}

public sealed record Friend(
    string FriendshipId,
    string UserId,
    string Username,
    string DisplayName,
    string? AvatarUrl,
    FriendState State,
    DateTime SinceUtc,
    FriendPresence Presence);

public sealed record FriendInvite(
    string Id,
    string FromUserId,
    string ToUserId,
    InviteKind Kind,
    string RoomLink,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public sealed record BlockedUser(string UserId, string Username, string DisplayName);

/// <summary>How a person found by name relates to you.</summary>
public enum SuggestionState
{
    None,
    Friend,

    /// <summary>They asked you.</summary>
    Incoming,

    /// <summary>You asked them.</summary>
    Outgoing,

    /// <summary>You blocked them. (People who blocked you never appear.)</summary>
    Blocked
}

/// <summary>A person matching what was typed in "Add a friend".</summary>
public sealed record ProfileSuggestion(string UserId, string Username, string DisplayName, string? AvatarUrl, SuggestionState State);

/// <summary>My own presence as published: already filtered by the privacy switches.</summary>
public sealed record MyPresence(PresenceStatus Status, ActivityKind? Kind, string? Activity);
