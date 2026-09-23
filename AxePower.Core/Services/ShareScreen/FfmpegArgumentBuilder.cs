using System;
using System.Collections.Generic;
using System.Globalization;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Turns a chosen display, quality rung and encoder into an FFmpeg command line.
///
/// Kept free of processes and files so the command line can be asserted in tests: a wrong flag
/// here is the difference between a smooth stream and a stuttering one, and that is much cheaper
/// to catch in a test than by watching a video.
/// </summary>
public static class FfmpegArgumentBuilder
{
    /// <summary>
    /// Builds the full argument string.
    /// </summary>
    /// <param name="audioInputArguments">
    /// The raw-PCM input flags from <see cref="ShareAudioPump"/>, or null/empty for a silent stream.
    /// </param>
    public static string Build(
        CaptureDisplay display,
        QualityPreset preset,
        ShareEncoder encoder,
        ShareScreenSettings settings,
        ShareRoom room,
        string? audioInputArguments)
    {
        var hasAudio = !string.IsNullOrWhiteSpace(audioInputArguments);
        var parts = new List<string>
        {
            "-hide_banner",
            "-loglevel warning",
            // Machine-readable telemetry on stdout; the human-readable stats line would fight it.
            "-progress pipe:1",
            "-nostats"
        };

        parts.Add($"-f lavfi -i \"{BuildCaptureFilter(display, preset, settings)}\"");

        if (hasAudio) parts.Add(audioInputArguments!);

        parts.Add("-map 0:v:0");
        if (hasAudio) parts.Add("-map 1:a:0");

        parts.Add(BuildVideoEncoderArguments(preset, encoder));

        if (hasAudio)
        {
            // WebRTC needs Opus, and Opus is defined at 48 kHz; resampling here keeps the browser
            // from having to do anything clever.
            parts.Add($"-c:a libopus -b:a {preset.AudioBitrateKbps}k -ar 48000 -ac 2 -application lowdelay");
        }

        // Publish with as little buffering as the muxer allows.
        parts.Add("-muxdelay 0 -muxpreload 0 -fflags +genpts");
        parts.Add($"-f rtsp -rtsp_transport tcp \"{room.PublishUrl(settings.RtspPort)}\"");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// The capture chain: Desktop Duplication, then whatever conversion the encoder needs.
    ///
    /// Frames come off the GPU because the encoder is not guaranteed to live on the same adapter
    /// that owns the display -- on a laptop the panel is usually wired to the iGPU while NVENC
    /// sits on the discrete card, and handing those textures straight to NVENC fails outright.
    /// </summary>
    internal static string BuildCaptureFilter(CaptureDisplay display, QualityPreset preset, ShareScreenSettings settings)
    {
        var chain = new List<string>
        {
            $"ddagrab=output_idx={display.OutputIndex}" +
            $":framerate={preset.Fps}" +
            $":draw_mouse={(settings.HideCursor ? 0 : 1)}",
            "hwdownload",
            "format=bgra"
        };

        // Scaling costs CPU, so only pay for it when the panel is not already the target size.
        if (display.Width != preset.Width || display.Height != preset.Height)
        {
            // fast_bilinear because holding 60 FPS matters more than resampling sharpness.
            chain.Add($"scale={preset.Width}:{preset.Height}:flags=fast_bilinear");
        }

        return string.Join(",", chain);
    }

    /// <summary>
    /// Encoder flags tuned for realtime: no B-frames, no look-ahead, a small VBV, and a keyframe
    /// every second so a viewer who joins late starts playing quickly.
    /// </summary>
    internal static string BuildVideoEncoderArguments(QualityPreset preset, ShareEncoder encoder)
    {
        var bitrate = preset.TargetBitrateKbps.ToString(CultureInfo.InvariantCulture);
        var maxrate = preset.MaxBitrateKbps.ToString(CultureInfo.InvariantCulture);

        // Half a second of buffer: enough to absorb a scene change, short enough not to be felt.
        var bufsize = (preset.TargetBitrateKbps / 2).ToString(CultureInfo.InvariantCulture);
        var gop = preset.Fps.ToString(CultureInfo.InvariantCulture);

        var common = $"-b:v {bitrate}k -maxrate {maxrate}k -bufsize {bufsize}k -g {gop} -bf 0{BrowserSafeProfile(encoder)}";

        return encoder.Vendor switch
        {
            ShareEncoderVendor.Nvidia =>
                $"-c:v {encoder.FfmpegName} -preset p1 -tune ll -rc cbr -zerolatency 1 -no-scenecut 1 " +
                $"{common} -pix_fmt yuv420p",

            ShareEncoderVendor.Amd =>
                $"-c:v {encoder.FfmpegName} -usage lowlatency -quality speed -rc cbr " +
                $"{common} -pix_fmt yuv420p",

            ShareEncoderVendor.Intel =>
                $"-c:v {encoder.FfmpegName} -preset veryfast -async_depth 1 " +
                $"{common} -pix_fmt nv12",

            // Software is the emergency fallback only; zerolatency keeps it from queueing frames.
            _ => $"-c:v {encoder.FfmpegName} -preset veryfast -tune zerolatency " +
                 $"-x264-params nal-hrd=cbr:force-cfr=1 -minrate {bitrate}k {common} -pix_fmt yuv420p"
        };
    }

    /// <summary>
    /// Pins H.264 to Main profile.
    ///
    /// Left alone, the hardware encoders emit High, which Chrome decodes happily but Firefox does
    /// not: it decodes WebRTC H.264 through OpenH264, which handles only parts of High profile, so
    /// a friend on Firefox gets a black player rather than an error. Main keeps CABAC -- and so
    /// nearly all of High's efficiency -- while staying inside what browsers actually negotiate;
    /// it is one of the two profiles production WebRTC servers advertise.
    ///
    /// Only H.264 is pinned: HEVC and AV1 name their profiles differently, and the level is left
    /// to the encoder because 1080p60 needs 4.2 and forcing anything lower would refuse the mode.
    /// </summary>
    private static string BrowserSafeProfile(ShareEncoder encoder) =>
        encoder.Codec == ShareVideoCodec.H264 ? " -profile:v main" : string.Empty;
}
