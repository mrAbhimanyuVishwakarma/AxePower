using System.Collections.Generic;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>Enumerates what the user can pick as a source: which screen, and which audio endpoints.</summary>
public interface IShareDeviceProvider
{
    /// <summary>
    /// Attached monitors in DXGI output order, so the index can be handed straight to the capture
    /// filter. Never empty on a machine with a desktop.
    /// </summary>
    IReadOnlyList<CaptureDisplay> GetDisplays();

    /// <summary>Render endpoints that can be captured via WASAPI loopback (what the PC is playing).</summary>
    IReadOnlyList<ShareAudioDevice> GetRenderDevices();

    /// <summary>Capture endpoints usable as a microphone.</summary>
    IReadOnlyList<ShareAudioDevice> GetCaptureDevices();
}
