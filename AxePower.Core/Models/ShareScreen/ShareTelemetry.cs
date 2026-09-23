using System;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// A snapshot of how the stream is actually behaving. The blueprint's rule is that a stream is
/// never judged by eye — every claim on the UI traces back to a number in here.
/// </summary>
public sealed record ShareTelemetry
{
    /// <summary>Frames per second FFmpeg reports leaving the encoder.</summary>
    public double Fps { get; init; }

    /// <summary>Encoded video bitrate as measured, not as requested.</summary>
    public double BitrateMbps { get; init; }

    /// <summary>Frames the pipeline dropped to avoid building latency.</summary>
    public long DroppedFrames { get; init; }

    /// <summary>Frames duplicated because capture did not deliver in time.</summary>
    public long DuplicatedFrames { get; init; }

    /// <summary>How long the session has been live.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Viewers currently pulling the stream, from the media server's metrics.</summary>
    public int ViewerCount { get; init; }

    /// <summary>Total bytes pushed so far.</summary>
    public long BytesSent { get; init; }

    /// <summary>
    /// How far encoder output is running behind real time. Above 1.0 the encoder is keeping up;
    /// a sustained dip below 1.0 means it is overloaded and the quality ladder should step down.
    /// </summary>
    public double Speed { get; init; } = 1.0;

    public static ShareTelemetry Empty { get; } = new();

    /// <summary>True when the encoder is falling behind badly enough to grow latency.</summary>
    public bool IsEncoderOverloaded => Speed > 0 && Speed < 0.92;
}
