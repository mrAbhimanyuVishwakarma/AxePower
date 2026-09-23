using AxePower.Core.Services.ShareScreen;
using NAudio.Wave;

namespace AxePower.Tests.ShareScreen;

/// <summary>
/// The audio input flags handed to FFmpeg.
///
/// Raw PCM on a pipe carries no header, so every one of these values is load-bearing: get the
/// sample format or the rate wrong and FFmpeg reinterprets the bytes rather than failing, which
/// comes out as noise or as a stream that never starts.
/// </summary>
public class ShareAudioPumpTests
{
    [Fact]
    public void FloatEndpoint_IsDescribedAsF32le()
    {
        // Shared-mode WASAPI hands back 32-bit float, which is the case that matters in practice.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        var args = ShareAudioPump.BuildInputArguments(format);

        Assert.Contains("-f f32le", args);
        Assert.Contains("-ar 48000", args);
        Assert.Contains("-ac 2", args);
        Assert.EndsWith("-i pipe:0", args);
    }

    [Fact]
    public void SixteenBitEndpoint_IsDescribedAsS16le()
    {
        var args = ShareAudioPump.BuildInputArguments(new WaveFormat(44100, 16, 2));

        Assert.Contains("-f s16le", args);
        Assert.Contains("-ar 44100", args);
    }

    [Fact]
    public void ChannelLayoutIsStated_SoFfmpegNeedNotGuess()
    {
        Assert.Contains("-channel_layout stereo",
            ShareAudioPump.BuildInputArguments(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));

        Assert.Contains("-channel_layout mono",
            ShareAudioPump.BuildInputArguments(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));
    }

    [Fact]
    public void RateFollowsTheEndpoint_RatherThanBeingForcedTo48k()
    {
        // Declaring a rate the endpoint does not run at would play the audio back at the wrong
        // speed; the resample to Opus's 48 kHz happens later, inside FFmpeg.
        var args = ShareAudioPump.BuildInputArguments(WaveFormat.CreateIeeeFloatWaveFormat(96000, 2));

        Assert.Contains("-ar 96000", args);
    }
}
