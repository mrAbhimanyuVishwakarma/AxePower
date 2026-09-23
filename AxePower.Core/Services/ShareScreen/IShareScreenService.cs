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

    Task StopAsync();

    /// <summary>
    /// Turns remote control on for the running session and returns the code a viewer must type to
    /// claim it, or turns it off again. The code is never part of the viewing link, so the host
    /// has to pass it on deliberately.
    /// </summary>
    string? SetRemoteControl(bool enabled);
}
