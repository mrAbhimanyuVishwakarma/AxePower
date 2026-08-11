using System.Text.Json.Serialization;

namespace AxePower.Core.Models;

public class AppRule
{
    [JsonPropertyName("executableName")]
    public string ExecutableName { get; set; } = string.Empty;

    [JsonPropertyName("targetMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PowerMode TargetMode { get; set; } = PowerMode.Beast;
}
