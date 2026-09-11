using System;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models;

/// <summary>
/// A phone or tablet that has completed pairing with the AxePower mobile bridge.
/// Persisted via <see cref="AppSettings"/>. The bearer token itself is never stored —
/// only its SHA-256 hash, so a leaked settings file cannot be replayed against the bridge.
/// </summary>
public class PairedDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "Unknown device";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "android";

    /// <summary>Base64 SHA-256 of the issued bearer token.</summary>
    [JsonPropertyName("tokenHash")]
    public string TokenHash { get; set; } = string.Empty;

    [JsonPropertyName("pairedAt")]
    public DateTime PairedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("lastSeenAt")]
    public DateTime LastSeenAt { get; set; } = DateTime.Now;

    /// <summary>Permits power-mode, refresh-rate and battery-tuning commands.</summary>
    [JsonPropertyName("allowPowerControl")]
    public bool AllowPowerControl { get; set; } = true;

    /// <summary>
    /// Permits lock / sleep / restart / shutdown. Off by default — a misplaced phone
    /// should not be able to power down the PC without the user opting in per device.
    /// </summary>
    [JsonPropertyName("allowSystemPower")]
    public bool AllowSystemPower { get; set; } = false;

    public PairedDevice Clone() => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Platform = Platform,
        TokenHash = TokenHash,
        PairedAt = PairedAt,
        LastSeenAt = LastSeenAt,
        AllowPowerControl = AllowPowerControl,
        AllowSystemPower = AllowSystemPower
    };
}

/// <summary>Runtime state of the mobile bridge, surfaced to the Phone Control view.</summary>
public class BridgeServerState
{
    public bool IsRunning { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public int ConnectedClients { get; set; }
    public int PairedDeviceCount { get; set; }

    /// <summary>Active pairing code, or empty when pairing mode is not armed.</summary>
    public string PairingCode { get; set; } = string.Empty;

    public DateTime? PairingCodeExpiresAt { get; set; }

    public string ServerUrl => IsRunning && !string.IsNullOrEmpty(IpAddress)
        ? $"http://{IpAddress}:{Port}/"
        : string.Empty;
}
