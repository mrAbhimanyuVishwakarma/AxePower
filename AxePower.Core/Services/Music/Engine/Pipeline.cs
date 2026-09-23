using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Brings any decoder's output to the device format: channel mapping first (mono → stereo by
/// duplication, surround → stereo downmix), then resampling. Returns the source untouched when
/// it already matches, so matching formats stay bit-identical.
/// </summary>
internal static class FormatAdapter
{
    public static ISampleProvider Adapt(ISampleProvider source, int sampleRate, int channels)
    {
        ISampleProvider result = source;

        if (result.WaveFormat.Channels != channels)
            result = new ChannelMapper(result, channels);

        if (result.WaveFormat.SampleRate != sampleRate)
            result = new WdlResamplingSampleProvider(result, sampleRate);

        return result;
    }
}

internal sealed class ChannelMapper : ISampleProvider
{
    private const float Minus3Db = 0.707f;

    private readonly ISampleProvider _source;
    private readonly int _in;
    private readonly int _out;
    private float[] _buffer = Array.Empty<float>();

    public ChannelMapper(ISampleProvider source, int outChannels)
    {
        _source = source;
        _in = source.WaveFormat.Channels;
        _out = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _out;
        int needed = frames * _in;
        if (_buffer.Length < needed) _buffer = new float[needed];

        int readFrames = _source.Read(_buffer, 0, needed) / _in;

        for (int f = 0; f < readFrames; f++)
        {
            int i = f * _in;
            float left, right;

            switch (_in)
            {
                case 1:
                    left = right = _buffer[i];
                    break;
                case 2:
                    left = _buffer[i];
                    right = _buffer[i + 1];
                    break;
                case 3: // FL FR FC
                    left = _buffer[i] + Minus3Db * _buffer[i + 2];
                    right = _buffer[i + 1] + Minus3Db * _buffer[i + 2];
                    break;
                case 4: // FL FR BL BR
                    left = _buffer[i] + Minus3Db * _buffer[i + 2];
                    right = _buffer[i + 1] + Minus3Db * _buffer[i + 3];
                    break;
                case 5: // FL FR FC BL BR
                    left = _buffer[i] + Minus3Db * (_buffer[i + 2] + _buffer[i + 3]);
                    right = _buffer[i + 1] + Minus3Db * (_buffer[i + 2] + _buffer[i + 4]);
                    break;
                default: // 5.1 / 7.1: FL FR FC LFE BL BR (SL SR)
                    left = _buffer[i] + Minus3Db * (_buffer[i + 2] + _buffer[i + 4]);
                    right = _buffer[i + 1] + Minus3Db * (_buffer[i + 2] + _buffer[i + 5]);
                    break;
            }

            int o = offset + f * _out;
            if (_out == 1)
            {
                buffer[o] = 0.5f * (left + right);
            }
            else
            {
                buffer[o] = left;
                buffer[o + 1] = right;
                for (int c = 2; c < _out; c++) buffer[o + c] = 0f;
            }
        }

        return readFrames * _out;
    }
}

/// <summary>
/// Decodes the first part of a track ahead of time, on the engine thread, so the render thread
/// never waits for a decoder to start (Media Foundation creates its reader on first read).
/// </summary>
internal sealed class PrerollSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float[] _buffer;
    private int _filled;
    private int _position;

    public PrerollSampleProvider(ISampleProvider source, TimeSpan preroll)
    {
        _source = source;
        int samples = (int)(preroll.TotalSeconds * source.WaveFormat.SampleRate) * source.WaveFormat.Channels;
        _buffer = new float[Math.Max(source.WaveFormat.Channels, samples)];

        while (_filled < _buffer.Length)
        {
            int n = _source.Read(_buffer, _filled, _buffer.Length - _filled);
            if (n == 0)
            {
                _sourceEnded = true;
                break;
            }
            _filled += n;
        }
    }

    private readonly bool _sourceEnded;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int fromBuffer = Math.Min(count, _filled - _position);
        if (fromBuffer > 0)
        {
            Array.Copy(_buffer, _position, buffer, offset, fromBuffer);
            _position += fromBuffer;
        }

        if (fromBuffer == count || _sourceEnded) return fromBuffer;
        return fromBuffer + _source.Read(buffer, offset + fromBuffer, count - fromBuffer);
    }
}

/// <summary>One track inside the <see cref="GaplessSourceChain"/>.</summary>
internal sealed class ChainSource
{
    public ChainSource(Guid key, IDecoder decoder, ISampleProvider samples, TimeSpan startOffset)
    {
        Key = key;
        Decoder = decoder;
        Samples = samples;
        StartOffset = startOffset;
    }

    public Guid Key { get; }
    public IDecoder Decoder { get; }
    public ISampleProvider Samples { get; }
    public TimeSpan StartOffset { get; }
    public long FramesRead { get; set; }
    public bool EndReported { get; set; }
    public bool NearEndReported { get; set; }
}

/// <summary>Callbacks from the render thread. Implementations must only queue work, never block.</summary>
internal interface IChainListener
{
    void OnTrackStarted(Guid key);
    void OnTrackEnded(Guid key);
    void OnTrackFailed(Guid key, Exception error);
    void OnNearEnd(Guid key);
    void OnRetired(ChainSource source);
}

/// <summary>
/// Plays the current track and joins the next one into the same buffer, so consecutive tracks
/// meet with no gap. It never runs dry: when there is nothing left it returns silence, which
/// keeps the device open, and reports the end once.
/// </summary>
internal sealed class GaplessSourceChain : ISampleProvider
{
    /// <summary>How long before the end the next track is opened and pre-rolled.</summary>
    public static readonly TimeSpan PreloadWindow = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly IChainListener _listener;
    private ChainSource? _current;
    private ChainSource? _next;

    public GaplessSourceChain(int sampleRate, int channels, IChainListener listener)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _listener = listener;
    }

    public WaveFormat WaveFormat { get; }

    public Guid? CurrentKey { get { lock (_lock) return _current?.Key; } }

    public ChainSource? Current { get { lock (_lock) return _current; } }

    /// <summary>
    /// Where the current track was (re)started, and how much has been handed to the output since
    /// — part of which is still buffered in the device and not yet audible.
    /// </summary>
    public (TimeSpan Start, TimeSpan Rendered, TimeSpan Duration) Progress
    {
        get
        {
            lock (_lock)
            {
                return _current == null
                    ? (TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
                    : (_current.StartOffset, TimeSpan.FromSeconds(_current.FramesRead / (double)WaveFormat.SampleRate), _current.Decoder.Duration);
            }
        }
    }

    public bool IsNearEnd
    {
        get { lock (_lock) return _current != null && Remaining(_current) <= PreloadWindow; }
    }

    /// <summary>Replaces the current track and drops any preloaded next one. Returns what was replaced.</summary>
    public IReadOnlyList<ChainSource> SetCurrent(ChainSource? source)
    {
        lock (_lock)
        {
            var retired = new List<ChainSource>(2);
            if (_current != null) retired.Add(_current);
            if (_next != null) retired.Add(_next);
            _current = source;
            _next = null;
            return retired;
        }
    }

    /// <summary>Swaps in a re-positioned copy of the current track (after a seek); the next track is kept.</summary>
    public ChainSource? ReplaceCurrent(ChainSource source)
    {
        lock (_lock)
        {
            var old = _current;
            _current = source;
            return old;
        }
    }

    /// <summary>Sets the track to join after the current one. Returns the previously queued one, if any.</summary>
    public ChainSource? SetNext(ChainSource? source)
    {
        lock (_lock)
        {
            var old = _next;
            _next = source;
            return old;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        var started = new List<Guid>(1);
        var retired = new List<ChainSource>(1);
        Guid? ended = null, nearEnd = null;
        (Guid Key, Exception Error)? failed = null;

        lock (_lock)
        {
            int written = 0;

            while (written < count && _current != null && !_current.EndReported)
            {
                int n = SafeRead(_current, buffer, offset + written, count - written, ref failed);
                _current.FramesRead += n / channels;
                written += n;

                if (failed != null && failed.Value.Key == _current.Key)
                {
                    // A broken track is reported as failed only — the player skips it; joining the
                    // next track here as well would advance twice
                    _current.EndReported = true;
                    break;
                }

                // Some readers return short reads mid-stream; only an empty read means the end
                if (n == 0)
                {
                    // The current track ran out inside this buffer
                    if (_next != null)
                    {
                        retired.Add(_current);
                        _current = _next;
                        _next = null;
                        started.Add(_current.Key);
                    }
                    else
                    {
                        _current.EndReported = true;
                        ended = _current.Key;
                    }
                }
            }

            if (_current != null && !_current.EndReported && !_current.NearEndReported && Remaining(_current) <= PreloadWindow)
            {
                _current.NearEndReported = true;
                nearEnd = _current.Key;
            }

            if (written < count)
                Array.Clear(buffer, offset + written, count - written);
        }

        // Notify outside the lock so no listener runs while the render path holds it
        foreach (var source in retired) _listener.OnRetired(source);
        if (failed is { } f) _listener.OnTrackFailed(f.Key, f.Error);
        foreach (var key in started) _listener.OnTrackStarted(key);
        if (ended is { } e) _listener.OnTrackEnded(e);
        if (nearEnd is { } ne) _listener.OnNearEnd(ne);

        return count;
    }

    private static int SafeRead(ChainSource source, float[] buffer, int offset, int count, ref (Guid, Exception)? failed)
    {
        try
        {
            return source.Samples.Read(buffer, offset, count);
        }
        catch (Exception ex)
        {
            // A decoder failure mid-track ends that track; the player decides what comes next
            failed = (source.Key, ex);
            return 0;
        }
    }

    private TimeSpan Remaining(ChainSource source)
    {
        var position = source.StartOffset + TimeSpan.FromSeconds(source.FramesRead / (double)WaveFormat.SampleRate);
        return source.Decoder.Duration - position;
    }
}

/// <summary>
/// Software volume and fades. Never touches the Windows master volume. Three independent gains
/// multiply: the volume setting, a fade for ducking and the sleep timer, and a short transport
/// fade that keeps pause, seek and skip click-free (so pausing never undoes a duck). At unity
/// gain with no ramp in progress, samples pass through untouched.
/// </summary>
internal sealed class VolumeStage : ISampleProvider
{
    private static readonly TimeSpan VolumeRamp = TimeSpan.FromMilliseconds(20);

    private readonly ISampleProvider _source;
    private readonly object _lock = new();
    private readonly GainRamp _volume = new();
    private readonly GainRamp _fade = new();
    private readonly GainRamp _transport = new();

    public VolumeStage(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    /// Maps the 0–2 volume setting to amplitude. Up to 100 % a cubic curve makes the slider feel
    /// linear; above that (boost, only when allowed) gain rises linearly to 2×.
    /// </summary>
    public static double ToAmplitude(double volume, bool muted, bool allowBoost)
    {
        if (muted || double.IsNaN(volume) || volume <= 0) return 0;
        if (volume <= 1.0) return volume * volume * volume;
        return allowBoost ? Math.Min(volume, 2.0) : 1.0;
    }

    public void SetAmplitude(double amplitude)
    {
        lock (_lock) _volume.Start(amplitude, Frames(VolumeRamp), immediate: false);
    }

    /// <summary>Ducking and sleep-timer fades. Completes when reached, or at once when <paramref name="immediate"/>.</summary>
    public Task FadeToAsync(double gain, TimeSpan duration, bool immediate)
    {
        lock (_lock) return _fade.Start(gain, Frames(duration), immediate);
    }

    /// <summary>Short click-avoidance fade around pause, seek and track changes.</summary>
    public Task TransportFadeAsync(double gain, TimeSpan duration, bool immediate)
    {
        lock (_lock) return _transport.Start(gain, Frames(duration), immediate);
    }

    private long Frames(TimeSpan duration) => (long)(duration.TotalSeconds * WaveFormat.SampleRate);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = WaveFormat.Channels;
        var completed = new List<TaskCompletionSource>(1);

        lock (_lock)
        {
            if (_volume.IsUnity && _fade.IsUnity && _transport.IsUnity) return read;

            for (int i = 0; i < read; i += channels)
            {
                _volume.Advance(completed);
                _fade.Advance(completed);
                _transport.Advance(completed);

                float gain = (float)(_volume.Value * _fade.Value * _transport.Value);
                for (int c = 0; c < channels && i + c < read; c++)
                    buffer[offset + i + c] *= gain;
            }
        }

        foreach (var done in completed) done.TrySetResult();
        return read;
    }

    /// <summary>A linear gain ramp advanced once per frame on the render thread.</summary>
    private sealed class GainRamp
    {
        private double _target = 1.0, _step;
        private long _framesLeft;
        private TaskCompletionSource? _done;

        public double Value { get; private set; } = 1.0;

        public bool IsUnity => Value == 1.0 && _framesLeft == 0;

        public Task Start(double target, long frames, bool immediate)
        {
            _done?.TrySetResult();
            _done = null;
            _target = target;

            if (immediate || frames <= 0)
            {
                Value = target;
                _framesLeft = 0;
                return Task.CompletedTask;
            }

            _framesLeft = frames;
            _step = (_target - Value) / frames;
            _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _done.Task;
        }

        public void Advance(List<TaskCompletionSource> completed)
        {
            if (_framesLeft == 0) return;

            Value += _step;
            if (--_framesLeft == 0)
            {
                Value = _target;
                if (_done != null) completed.Add(_done);
                _done = null;
            }
        }
    }
}

/// <summary>
/// Keeps boosted audio from clipping. Active only while total gain exceeds 0 dB; otherwise it
/// passes samples through untouched. Threshold −1 dBFS, 1 ms attack, 100 ms release, and a
/// final clamp guarantees no sample leaves beyond ±1.0.
/// </summary>
internal sealed class SoftLimiter : ISampleProvider
{
    private const float Threshold = 0.8912509f;   // −1 dBFS

    private readonly ISampleProvider _source;
    private readonly float _attack;
    private readonly float _release;
    private float _gain = 1f;

    public SoftLimiter(ISampleProvider source)
    {
        _source = source;
        double rate = source.WaveFormat.SampleRate;
        _attack = (float)(1 - Math.Exp(-1.0 / (0.001 * rate)));
        _release = (float)(1 - Math.Exp(-1.0 / (0.100 * rate)));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public volatile bool Active;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!Active && _gain >= 0.99999f) return read;

        int channels = WaveFormat.Channels;
        for (int i = 0; i < read; i += channels)
        {
            float peak = 0f;
            for (int c = 0; c < channels && i + c < read; c++)
                peak = Math.Max(peak, Math.Abs(buffer[offset + i + c]));

            float desired = peak > Threshold ? Threshold / peak : 1f;
            _gain += (desired - _gain) * (desired < _gain ? _attack : _release);

            for (int c = 0; c < channels && i + c < read; c++)
            {
                float s = buffer[offset + i + c] * _gain;
                buffer[offset + i + c] = s > 1f ? 1f : s < -1f ? -1f : s;
            }
        }

        if (!Active && _gain > 0.99999f) _gain = 1f;
        return read;
    }
}

/// <summary>
/// Converts float samples to the device's format. Float output is copied bit for bit; integer
/// output is clamped, with TPDF dither only when reducing bit depth while DSP is active.
/// </summary>
internal sealed class PcmOutputConverter : IWaveProvider
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly ISampleProvider _source;
    private readonly bool _isFloat;
    private readonly int _bytesPerSample;
    private readonly Random _random = new();
    private float[] _samples = Array.Empty<float>();

    public PcmOutputConverter(ISampleProvider source, WaveFormat target)
    {
        if (source.WaveFormat.SampleRate != target.SampleRate || source.WaveFormat.Channels != target.Channels)
            throw new ArgumentException("The pipeline format must match the device format.");

        _source = source;
        WaveFormat = target;
        _isFloat = target.Encoding == WaveFormatEncoding.IeeeFloat
                   || (target is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        _bytesPerSample = target.BitsPerSample / 8;

        if (_isFloat ? _bytesPerSample != 4 : _bytesPerSample is < 2 or > 4)
            throw new NotSupportedException($"Unsupported device format: {target}");
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Set while any stage changes samples; enables dither for integer output.</summary>
    public volatile bool DspActive;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / _bytesPerSample;
        if (_samples.Length < samples) _samples = new float[samples];

        int read = _source.Read(_samples, 0, samples);

        if (_isFloat)
        {
            Buffer.BlockCopy(_samples, 0, buffer, offset, read * 4);
            return read * 4;
        }

        bool dither = DspActive;
        for (int i = 0; i < read; i++)
        {
            double s = _samples[i];
            int p = offset + i * _bytesPerSample;

            switch (_bytesPerSample)
            {
                case 2:
                {
                    double v = s * 32767.0 + (dither ? Tpdf() : 0);
                    short q = (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);
                    buffer[p] = (byte)q;
                    buffer[p + 1] = (byte)(q >> 8);
                    break;
                }
                case 3:
                {
                    double v = s * 8388607.0 + (dither ? Tpdf() : 0);
                    int q = (int)Math.Clamp(Math.Round(v), -8388608, 8388607);
                    buffer[p] = (byte)q;
                    buffer[p + 1] = (byte)(q >> 8);
                    buffer[p + 2] = (byte)(q >> 16);
                    break;
                }
                default:
                {
                    long q = (long)Math.Clamp(Math.Round(s * 2147483647.0), int.MinValue, int.MaxValue);
                    BitConverter.TryWriteBytes(new Span<byte>(buffer, p, 4), (int)q);
                    break;
                }
            }
        }

        return read * _bytesPerSample;
    }

    // Triangular dither of ±1 LSB
    private double Tpdf() => _random.NextDouble() - _random.NextDouble();
}
