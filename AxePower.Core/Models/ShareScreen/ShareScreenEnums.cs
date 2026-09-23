namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// Preset rungs of the quality ladder. The blueprint's core rule is that frame rate outranks
/// resolution, so every rung stays at 60 FPS and drops pixels instead of frames.
/// </summary>
public enum ShareQuality
{
    /// <summary>1280x720 @ 60 — the rung used when bandwidth is tight.</summary>
    Smooth720,

    /// <summary>1920x1080 @ 60, ~16 Mbps. The default.</summary>
    Balanced1080,

    /// <summary>1920x1080 @ 60, ~22 Mbps.</summary>
    High1080,

    /// <summary>2560x1440 @ 60, ~28 Mbps.</summary>
    Quad1440,

    /// <summary>3840x2160 @ 60, ~45 Mbps. Only offered when the encoder and link can carry it.</summary>
    Ultra2160
}

public enum ShareVideoCodec
{
    /// <summary>H.264 — the mandatory baseline: every browser can hardware-decode it.</summary>
    H264,

    Hevc,

    Av1
}

/// <summary>Which silicon does the encoding. Software is an emergency fallback, never a default.</summary>
public enum ShareEncoderVendor
{
    Nvidia,
    Amd,
    Intel,
    Software
}

/// <summary>What audio, if any, rides along with the video.</summary>
public enum ShareAudioMode
{
    /// <summary>No audio track at all.</summary>
    None,

    /// <summary>WASAPI loopback of a render device — what the PC is playing (game, music, calls).</summary>
    SystemAudio,

    /// <summary>A capture device, for talking over the stream.</summary>
    Microphone
}

public enum ShareSessionState
{
    Idle,

    /// <summary>Locating FFmpeg and the media server, probing encoders.</summary>
    Preparing,

    /// <summary>Media server up, encoder starting, no viewer can connect yet.</summary>
    Starting,

    /// <summary>Publishing. The viewer link works.</summary>
    Live,

    Stopping,

    /// <summary>Stopped because something failed; <c>StatusMessage</c> says what.</summary>
    Faulted
}
