using System;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Friends;

/// <summary>
/// Local user gamer profile representation for the Friends hub.
/// </summary>
public sealed record FriendsProfile
{
    [JsonPropertyName("gamerTag")]
    public string GamerTag { get; init; } = Environment.UserName;

    [JsonPropertyName("statusMessage")]
    public string StatusMessage { get; init; } = "Ready to play & stream";

    [JsonPropertyName("status")]
    public FriendStatus Status { get; init; } = FriendStatus.Online;

    [JsonPropertyName("avatarColor")]
    public string AvatarColor { get; init; } = "#5865F2"; // Discord blurple accent

    [JsonIgnore]
    public string Initials => !string.IsNullOrWhiteSpace(GamerTag)
        ? GamerTag[..1].ToUpperInvariant()
        : "U";

}
