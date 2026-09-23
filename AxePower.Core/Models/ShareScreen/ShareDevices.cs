using System;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// One monitor that can be captured. <see cref="OutputIndex"/> is the DXGI output ordinal that
/// Desktop Duplication (and FFmpeg's <c>ddagrab</c>) addresses the display by.
/// </summary>
public sealed record CaptureDisplay(
    int OutputIndex,
    string DeviceName,
    string FriendlyName,
    int Width,
    int Height,
    int RefreshHz,
    bool IsPrimary,
    int Left = 0,
    int Top = 0)
{
    public string DisplayLabel =>
        $"{FriendlyName} — {Width}x{Height} @ {RefreshHz}Hz{(IsPrimary ? " (primary)" : string.Empty)}";

    /// <summary>
    /// Turns a point expressed as a fraction of the shared picture into a desktop pixel.
    ///
    /// Remote control needs this because the viewer only knows where they clicked inside a video
    /// element; the monitor being shared may sit anywhere in the virtual desktop, including at
    /// negative coordinates when it is left of or above the primary one.
    /// </summary>
    public (int X, int Y) FractionToDesktop(double fractionX, double fractionY)
    {
        var clampedX = Math.Clamp(fractionX, 0.0, 1.0);
        var clampedY = Math.Clamp(fractionY, 0.0, 1.0);

        return (Left + (int)Math.Round(clampedX * (Width - 1)),
                Top + (int)Math.Round(clampedY * (Height - 1)));
    }
}

/// <summary>An audio endpoint offered as a stream source.</summary>
/// <param name="Id">The WASAPI endpoint id, or empty for "system default".</param>
/// <param name="IsCapture">True for microphones, false for render devices captured via loopback.</param>
public sealed record ShareAudioDevice(string Id, string Name, bool IsDefault, bool IsCapture)
{
    public string DisplayLabel => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>
/// A hardware encoder that FFmpeg reported as built in and that a probe confirmed actually runs on
/// this machine. Listing an encoder the driver refuses is worse than not offering it.
/// </summary>
public sealed record ShareEncoder(
    string FfmpegName,
    ShareEncoderVendor Vendor,
    ShareVideoCodec Codec,
    string DisplayName)
{
    /// <summary>True for everything except the libx264 fallback.</summary>
    public bool IsHardware => Vendor != ShareEncoderVendor.Software;
}

/// <summary>
/// The credentials and addresses for one sharing session. The room code is a convenience for
/// reading out loud; the tokens are what actually authorise publishing and viewing.
/// </summary>
public sealed record ShareRoom(
    string Code,
    string PathName,
    string PublisherToken,
    string ViewerToken)
{
    /// <summary>"482-731" — the code as a human reads it aloud.</summary>
    public string FormattedCode => Code.Length == 6 ? $"{Code[..3]}-{Code[3..]}" : Code;

    /// <summary>The link a friend opens. Host is the address the viewer can actually reach.</summary>
    public string ViewerUrl(string host, int webrtcPort) =>
        $"http://{host}:{webrtcPort}/{PathName}/?token={Uri.EscapeDataString(ViewerToken)}";

    /// <summary>Where FFmpeg pushes the encoded stream.</summary>
    public string PublishUrl(int rtspPort) =>
        $"rtsp://publisher:{Uri.EscapeDataString(PublisherToken)}@127.0.0.1:{rtspPort}/{PathName}";
}
