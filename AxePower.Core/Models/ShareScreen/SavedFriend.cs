using System;
using System.Text.Json.Serialization;
using AxePower.Core.Models.Friends;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// A remembered friend with their saved room address or Tailscale IP, plus gamer status.
/// </summary>
public sealed record SavedFriend
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("lastConnected")]
    public DateTime? LastConnected { get; init; }

    [JsonPropertyName("status")]
    public FriendStatus Status { get; init; } = FriendStatus.Offline;

    [JsonPropertyName("activity")]
    public string Activity { get; init; } = string.Empty;

    [JsonPropertyName("isStreaming")]
    public bool IsStreaming { get; init; }

    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; init; } = string.Empty;

    [JsonIgnore]
    public string Initials => !string.IsNullOrWhiteSpace(Name)
        ? Name[..1].ToUpperInvariant()
        : "F";

    [JsonIgnore]
    public string StatusBadgeColor => Status switch
    {
        FriendStatus.Online => "#23A55A",    // Discord green
        FriendStatus.InGame => "#5865F2",    // Blurple
        FriendStatus.Streaming => "#A855F7", // Purple
        FriendStatus.InCall => "#F27835",    // Orange
        FriendStatus.Away => "#FEE75C",      // Yellow
        _ => "#80848E"                       // Grey
    };

    [JsonIgnore]
    public string StatusDisplay => !string.IsNullOrWhiteSpace(Activity) ? Activity : Status.ToString();
}
