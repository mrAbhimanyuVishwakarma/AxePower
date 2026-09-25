using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Friends;

/// <summary>
/// Status of a friend in the Friends hub (Discord/Steam inspired).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FriendStatus
{
    Online,
    InGame,
    Streaming,
    InCall,
    Away,
    Offline
}
