using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>What the UI needs to know about a session, without owning any of the machinery.</summary>
public sealed record ShareSessionStatus(
    ShareSessionState State,
    string Message,
    ShareRoom? Room,
    string ViewerUrl,
    QualityPreset Quality,
    ShareEncoder? Encoder,
    ShareTelemetry Telemetry,
    RemoteControlState Control = RemoteControlState.Disabled,
    string ControlCode = "")
{
    public static ShareSessionStatus Idle { get; } = new(
        ShareSessionState.Idle, string.Empty, null, string.Empty,
        QualityPreset.Balanced1080, null, ShareTelemetry.Empty);
}

/// <summary>Starts, stops and reports on a screen-sharing session.</summary>
public interface IShareScreenService
{
    ShareSessionStatus Status { get; }

    event EventHandler<ShareSessionStatus>? StatusChanged;

    /// <summary>
    /// Brings up the media server and the encoder. Returns once the stream is publishing, so the
    /// viewer link handed back is one that already works.
    /// </summary>
    Task<ShareSessionStatus> StartAsync(ShareScreenSettings settings, IProgress<string>? step, CancellationToken ct);

    /// <summary>Opens the authenticated room channels without starting display capture.</summary>
    Task<ShareSessionStatus> StartRoomAsync(ShareScreenSettings settings, CancellationToken ct);

    /// <summary>Stops only display capture while keeping voice, chat, and files connected.</summary>
    Task StopScreenShareAsync();

    Task StopAsync();

    /// <summary>
    /// Turns remote control on for the running session and returns the code a viewer must type to
    /// claim it, or turns it off again. The code is never part of the viewing link, so the host
    /// has to pass it on deliberately.
    /// </summary>
    string? SetRemoteControl(bool enabled);

    /// <summary>Raised when a participant sends a chat message in the room.</summary>
    event EventHandler<ShareChatMessage>? ChatMessageReceived;

    /// <summary>Broadcasts a chat message to all connected participants in the room.</summary>
    void SendChatMessage(ShareChatMessage message);

    /// <summary>Raised when an authenticated participant sends one encrypted Opus voice packet.</summary>
    event EventHandler<byte[]>? VoicePacketReceived;

    /// <summary>Broadcasts one Opus voice packet to authenticated participants.</summary>
    void SendVoicePacket(byte[] opusPacket);
}
