using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// Persistent Share Screen preferences, stored under the <c>shareScreen</c> key of settings.json.
///
/// Tokens and room codes are deliberately absent: they are minted per session and must not
/// outlive it. Every property needs a default and a line in <see cref="Clone"/>, or its value is
/// lost on the next save.
/// </summary>
public class ShareScreenSettings
{
    // ── Capture ─────────────────────────────────────────────────────────────

    /// <summary>DXGI output ordinal of the monitor to share. Falls back to the primary if gone.</summary>
    [JsonPropertyName("displayIndex")]
    public int DisplayIndex { get; set; }

    [JsonPropertyName("quality")]
    public ShareQuality Quality { get; set; } = ShareQuality.Balanced1080;

    [JsonPropertyName("codec")]
    public ShareVideoCodec Codec { get; set; } = ShareVideoCodec.H264;

    /// <summary>
    /// FFmpeg encoder name to force, or null to pick the best available automatically
    /// (NVENC, then AMF, then QSV, then software).
    /// </summary>
    [JsonPropertyName("preferredEncoder")]
    public string? PreferredEncoder { get; set; }

    /// <summary>Hide the mouse pointer from the shared picture.</summary>
    [JsonPropertyName("hideCursor")]
    public bool HideCursor { get; set; }

    // ── Audio ───────────────────────────────────────────────────────────────

    [JsonPropertyName("audioMode")]
    public ShareAudioMode AudioMode { get; set; } = ShareAudioMode.SystemAudio;

    /// <summary>WASAPI render endpoint to loop back, or null to follow the system default.</summary>
    [JsonPropertyName("systemAudioDeviceId")]
    public string? SystemAudioDeviceId { get; set; }

    /// <summary>WASAPI capture endpoint for the microphone, or null for the system default.</summary>
    [JsonPropertyName("microphoneDeviceId")]
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Microphone gain applied before mixing, 0.0–2.0.</summary>
    [JsonPropertyName("microphoneGain")]
    public double MicrophoneGain { get; set; } = 1.0;

    // ── Delivery ────────────────────────────────────────────────────────────

    /// <summary>Port the bundled media server listens on for the host's own RTSP publish.</summary>
    [JsonPropertyName("rtspPort")]
    public int RtspPort { get; set; } = 8554;

    /// <summary>Port viewers connect to over WebRTC.</summary>
    [JsonPropertyName("webrtcPort")]
    public int WebrtcPort { get; set; } = 8889;

    /// <summary>
    /// Hostname or IP viewers should use. Empty means "work it out from the active network
    /// adapter", which is right on a LAN and on Tailscale but not behind NAT.
    /// </summary>
    [JsonPropertyName("viewerHostOverride")]
    public string? ViewerHostOverride { get; set; }

    /// <summary>Step the quality ladder down automatically when the encoder or link struggles.</summary>
    [JsonPropertyName("adaptiveQuality")]
    public bool AdaptiveQuality { get; set; } = true;

    /// <summary>Keep the screen awake while sharing.</summary>
    [JsonPropertyName("keepAwakeWhileSharing")]
    public bool KeepAwakeWhileSharing { get; set; } = true;

    // -- Remote control ------------------------------------------------------

    /// <summary>
    /// Whether the Share Screen page offers remote control at all.
    ///
    /// Off by default, and deliberately not enough on its own: turning it on only makes the
    /// control button available. Each session still has to be granted explicitly, and the viewer
    /// still has to be given a code that the viewing link does not contain.
    /// </summary>
    [JsonPropertyName("allowRemoteControl")]
    public bool AllowRemoteControl { get; set; }

    /// <summary>Port the viewer page and its control channel are served on.</summary>
    [JsonPropertyName("viewerPort")]
    public int ViewerPort { get; set; } = 8890;

    /// <summary>List of saved/remembered friends and their addresses.</summary>
    [JsonPropertyName("savedFriends")]
    public List<SavedFriend> SavedFriends { get; set; } = new();

    public ShareScreenSettings Clone() => new()
    {
        DisplayIndex = DisplayIndex,
        Quality = Quality,
        Codec = Codec,
        PreferredEncoder = PreferredEncoder,
        HideCursor = HideCursor,
        AudioMode = AudioMode,
        SystemAudioDeviceId = SystemAudioDeviceId,
        MicrophoneDeviceId = MicrophoneDeviceId,
        MicrophoneGain = MicrophoneGain,
        RtspPort = RtspPort,
        WebrtcPort = WebrtcPort,
        ViewerHostOverride = ViewerHostOverride,
        AdaptiveQuality = AdaptiveQuality,
        KeepAwakeWhileSharing = KeepAwakeWhileSharing,
        AllowRemoteControl = AllowRemoteControl,
        ViewerPort = ViewerPort,
        SavedFriends = SavedFriends.Select(f => f with { }).ToList()
    };
}
