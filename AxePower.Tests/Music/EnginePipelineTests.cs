using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Services.Music;
using NAudio.Wave;

namespace AxePower.Tests.Music;

/// <summary>
/// Sample-level tests of the playback pipeline. No audio device is opened: the tests pull
/// samples from the stages directly, the way the output would.
/// </summary>
public class EnginePipelineTests
{
    /// <summary>An in-memory decoder over a fixed sample array.</summary>
    private sealed class ArrayDecoder : IDecoder
    {
        private readonly float[] _samples;
        private int _position;

        public ArrayDecoder(float[] samples, int sampleRate = 48000, int channels = 2)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }
        public TimeSpan Duration => TimeSpan.FromSeconds(_samples.Length / (double)(WaveFormat.SampleRate * WaveFormat.Channels));
        public TimeSpan Position => TimeSpan.FromSeconds(_position / (double)(WaveFormat.SampleRate * WaveFormat.Channels));
        public bool Disposed { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public void Seek(TimeSpan position) =>
            _position = (int)(position.TotalSeconds * WaveFormat.SampleRate) * WaveFormat.Channels;

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingListener : IChainListener
    {
        public List<string> Events { get; } = new();
        public void OnTrackStarted(Guid key) => Events.Add($"started:{key}");
        public void OnTrackEnded(Guid key) => Events.Add($"ended:{key}");
        public void OnTrackFailed(Guid key, Exception error) => Events.Add($"failed:{key}");
        public void OnNearEnd(Guid key) => Events.Add($"near:{key}");
        public void OnRetired(ChainSource source) => Events.Add($"retired:{source.Key}");
    }

    private sealed class FloatSource : ISampleProvider
    {
        private readonly Func<int, float> _sample;
        private long _index;
        public FloatSource(Func<int, float> sample, int sampleRate = 48000, int channels = 2)
        {
            _sample = sample;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = _sample((int)_index++);
            return count;
        }
    }

    private static float[] Ramp(int count, float start) => Enumerable.Range(0, count).Select(i => start + i * 1e-5f).ToArray();

    private static ChainSource Source(Guid key, IDecoder decoder) => new(key, decoder, decoder, TimeSpan.Zero);

    // ── Gapless ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gapless_JoinsTracksInOneBuffer_WithNoMissingOrExtraSamples()
    {
        var a = Ramp(10_001 * 2, 0.1f);
        var b = Ramp(7_003 * 2, 0.5f);
        var keyA = Guid.NewGuid();
        var keyB = Guid.NewGuid();
        var listener = new RecordingListener();

        var chain = new GaplessSourceChain(48000, 2, listener);
        chain.SetCurrent(Source(keyA, new ArrayDecoder(a)));
        chain.SetNext(Source(keyB, new ArrayDecoder(b)));

        var output = new List<float>();
        var buffer = new float[1024];
        while (!listener.Events.Contains($"ended:{keyB}"))
        {
            Assert.Equal(buffer.Length, chain.Read(buffer, 0, buffer.Length)); // never runs dry
            output.AddRange(buffer);
        }

        var expected = a.Concat(b).ToArray();
        Assert.Equal(expected, output.Take(expected.Length).ToArray());      // sample-exact join
        Assert.All(output.Skip(expected.Length), s => Assert.Equal(0f, s)); // silence after the end
        Assert.Equal(1, listener.Events.Count(e => e == $"started:{keyB}"));
        Assert.Equal(1, listener.Events.Count(e => e == $"ended:{keyB}"));
        Assert.DoesNotContain($"ended:{keyA}", listener.Events);            // A joined into B, it never "ended"
        Assert.Contains($"retired:{keyA}", listener.Events);
    }

    [Fact]
    public void NoNextTrack_EndIsReportedOnce_ThenSilence()
    {
        var key = Guid.NewGuid();
        var listener = new RecordingListener();
        var chain = new GaplessSourceChain(48000, 2, listener);
        chain.SetCurrent(Source(key, new ArrayDecoder(Ramp(1000, 0.2f))));

        var buffer = new float[4096];
        for (int i = 0; i < 5; i++) chain.Read(buffer, 0, buffer.Length);

        Assert.Equal(1, listener.Events.Count(e => e == $"ended:{key}"));
    }

    [Fact]
    public void Progress_CountsRenderedFramesFromTheStartOffset()
    {
        var chain = new GaplessSourceChain(48000, 2, new RecordingListener());
        var decoder = new ArrayDecoder(new float[48000 * 2 * 3]);
        chain.SetCurrent(new ChainSource(Guid.NewGuid(), decoder, decoder, TimeSpan.FromSeconds(1)));

        var buffer = new float[48000]; // 0.5 s of stereo
        chain.Read(buffer, 0, buffer.Length);

        var (start, rendered, _) = chain.Progress;
        Assert.Equal(TimeSpan.FromSeconds(1), start);
        Assert.Equal(0.5, rendered.TotalSeconds, 3);
    }

    // ── Format adaptation ───────────────────────────────────────────────────

    [Fact]
    public void Adapt_SameFormat_ReturnsTheSourceUntouched()
    {
        var source = new FloatSource(i => 0.25f);
        Assert.Same(source, FormatAdapter.Adapt(source, 48000, 2));
    }

    [Fact]
    public void Resample_44100To48000_ProducesTheExpectedLength()
    {
        var decoder = new ArrayDecoder(Enumerable.Range(0, 44100 * 2).Select(i => (float)Math.Sin(i * 0.01) * 0.5f).ToArray(), 44100, 2);
        var adapted = FormatAdapter.Adapt(decoder, 48000, 2);

        long total = 0;
        var buffer = new float[4096];
        int n;
        while ((n = adapted.Read(buffer, 0, buffer.Length)) > 0) total += n;

        // One second in, one second out (within the resampler's small filter latency)
        Assert.InRange(total / 2.0, 48000 * 0.99, 48000 * 1.01);
    }

    [Fact]
    public void MonoToStereo_DuplicatesTheChannel()
    {
        var adapted = FormatAdapter.Adapt(new FloatSource(i => i * 0.001f, 48000, 1), 48000, 2);
        var buffer = new float[8];
        adapted.Read(buffer, 0, 8);
        Assert.Equal(new[] { 0f, 0f, 0.001f, 0.001f, 0.002f, 0.002f, 0.003f, 0.003f }, buffer);
    }

    [Fact]
    public void Surround51_DownmixesToStereo()
    {
        // FL=0.1 FR=0.2 FC=0.3 LFE=0.9 BL=0.4 BR=0.5
        float[] frame = { 0.1f, 0.2f, 0.3f, 0.9f, 0.4f, 0.5f };
        var adapted = FormatAdapter.Adapt(new FloatSource(i => frame[i % 6], 48000, 6), 48000, 2);
        var buffer = new float[2];
        adapted.Read(buffer, 0, 2);

        Assert.Equal(0.1 + 0.707 * (0.3 + 0.4), buffer[0], 4);
        Assert.Equal(0.2 + 0.707 * (0.3 + 0.5), buffer[1], 4);
    }

    // ── Volume and limiter ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, false, false, 0.0)]
    [InlineData(0.5, false, false, 0.125)]   // cubic curve
    [InlineData(1.0, false, false, 1.0)]
    [InlineData(1.5, false, false, 1.0)]     // boost not allowed
    [InlineData(1.5, false, true, 1.5)]      // boost is linear above 100 %
    [InlineData(3.0, false, true, 2.0)]      // capped at 200 %
    [InlineData(0.8, true, false, 0.0)]      // muted
    public void VolumeCurve(double volume, bool muted, bool boost, double expected)
    {
        Assert.Equal(expected, VolumeStage.ToAmplitude(volume, muted, boost), 6);
    }

    [Fact]
    public void NeutralStages_AreBitIdenticalPassThrough()
    {
        var source = Enumerable.Range(0, 4096).Select(i => (float)Math.Sin(i * 0.37) * 0.93f).ToArray();
        var decoder = new ArrayDecoder(source);
        var limiter = new SoftLimiter(new VolumeStage(decoder));

        var output = new float[source.Length];
        limiter.Read(output, 0, output.Length);

        Assert.Equal(source, output);
    }

    [Fact]
    public void BoostTo200Percent_WithLimiter_NeverExceedsFullScale()
    {
        var loud = new FloatSource(i => 0.9f * (float)Math.Sin(i * 0.05));
        var volume = new VolumeStage(loud);
        var limiter = new SoftLimiter(volume) { Active = true };
        volume.SetAmplitude(2.0);

        var buffer = new float[48000 * 2];
        for (int pass = 0; pass < 3; pass++)
        {
            limiter.Read(buffer, 0, buffer.Length);
            Assert.All(buffer, s => Assert.InRange(s, -1.0f, 1.0f));
        }
    }

    [Fact]
    public void Fade_ReachesTargetAndCompletes()
    {
        var volume = new VolumeStage(new FloatSource(i => 1f));
        var fade = volume.FadeToAsync(0.0, TimeSpan.FromMilliseconds(10), immediate: false);

        var buffer = new float[48000 * 2 / 10]; // 100 ms
        volume.Read(buffer, 0, buffer.Length);

        Assert.True(fade.IsCompleted);
        Assert.Equal(0f, buffer[^1]);
        Assert.True(buffer[0] > 0.9f); // it ramps, it does not jump
    }

    // ── Output conversion ───────────────────────────────────────────────────

    [Fact]
    public void FloatDevice_GetsBitIdenticalBytes()
    {
        var source = Enumerable.Range(0, 1000).Select(i => (float)Math.Cos(i * 0.1) * 0.7f).ToArray();
        var converter = new PcmOutputConverter(new ArrayDecoder(source), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));

        var bytes = new byte[source.Length * 4];
        Assert.Equal(bytes.Length, converter.Read(bytes, 0, bytes.Length));

        var back = new float[source.Length];
        Buffer.BlockCopy(bytes, 0, back, 0, bytes.Length);
        Assert.Equal(source, back);
    }

    [Fact]
    public void Pcm16Device_ClampsOutOfRangeSamples()
    {
        var converter = new PcmOutputConverter(new ArrayDecoder(new[] { 1.5f, -1.5f }), new WaveFormat(48000, 16, 2));
        var bytes = new byte[4];
        converter.Read(bytes, 0, 4);

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(bytes, 2));
    }
}
