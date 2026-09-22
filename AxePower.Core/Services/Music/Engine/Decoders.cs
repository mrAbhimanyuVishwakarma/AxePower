using System;
using System.IO;
using Concentus;
using Concentus.Oggfile;
using NAudio.Wave;

namespace AxePower.Core.Services.Music;

/// <summary>A seekable float sample source for one file.</summary>
internal interface IDecoder : ISampleProvider, IDisposable
{
    TimeSpan Duration { get; }

    /// <summary>Position of the next sample <see cref="ISampleProvider.Read"/> will return.</summary>
    TimeSpan Position { get; }

    void Seek(TimeSpan position);
}

/// <summary>WAV, AIFF, Media Foundation and Vorbis readers, which are all NAudio WaveStreams.</summary>
internal sealed class WaveStreamDecoder : IDecoder
{
    private readonly WaveStream _stream;
    private readonly ISampleProvider _samples;

    public WaveStreamDecoder(WaveStream stream)
    {
        _stream = stream;
        _samples = stream as ISampleProvider
                   ?? (stream.WaveFormat.Encoding == WaveFormatEncoding.Extensible
                       ? new ExtensiblePcmSampleProvider(stream)
                       : stream.ToSampleProvider());
    }

    public WaveFormat WaveFormat => _samples.WaveFormat;
    public TimeSpan Duration => _stream.TotalTime;
    public TimeSpan Position => _stream.CurrentTime;

    public int Read(float[] buffer, int offset, int count) => _samples.Read(buffer, offset, count);

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > Duration) position = Duration;
        _stream.CurrentTime = position;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Converts WAVE_FORMAT_EXTENSIBLE streams (typical for 24-bit and multichannel WAV) to float.
/// The sub-format decides between integer PCM and IEEE float.
/// </summary>
internal sealed class ExtensiblePcmSampleProvider : ISampleProvider
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly IWaveProvider _source;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private byte[] _bytes = Array.Empty<byte>();

    public ExtensiblePcmSampleProvider(IWaveProvider source)
    {
        _source = source;
        _bytesPerSample = source.WaveFormat.BitsPerSample / 8;
        _isFloat = source.WaveFormat is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat;

        if (_isFloat ? _bytesPerSample != 4 : _bytesPerSample is < 1 or > 4)
            throw new UnsupportedFormatException($"{source.WaveFormat.BitsPerSample}-bit");

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _bytesPerSample;
        if (_bytes.Length < needed) _bytes = new byte[needed];

        int read = _source.Read(_bytes, 0, needed);
        int samples = read / _bytesPerSample;

        for (int i = 0; i < samples; i++)
        {
            int p = i * _bytesPerSample;
            buffer[offset + i] = _isFloat
                ? BitConverter.ToSingle(_bytes, p)
                : _bytesPerSample switch
                {
                    1 => (_bytes[p] - 128) / 128f,
                    2 => (short)(_bytes[p] | _bytes[p + 1] << 8) / 32768f,
                    3 => ((_bytes[p] << 8 | _bytes[p + 1] << 16 | _bytes[p + 2] << 24) >> 8) / 8388608f,
                    _ => BitConverter.ToInt32(_bytes, p) / 2147483648f
                };
        }

        return samples;
    }
}

/// <summary>Ogg Opus through Concentus. Opus always decodes at 48 kHz; mono files are rendered as stereo.</summary>
internal sealed class OpusDecoder : IDecoder
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    private readonly FileStream _file;
    private readonly OpusOggReadStream _reader;
    private short[]? _pending;
    private int _pendingOffset;
    private long _samplesReturned;      // interleaved samples since the last seek
    private TimeSpan _seekBase = TimeSpan.Zero;

    public OpusDecoder(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels, null);
        _reader = new OpusOggReadStream(decoder, _file);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    }

    public WaveFormat WaveFormat { get; }
    public TimeSpan Duration => _reader.TotalTime;
    public TimeSpan Position => _seekBase + TimeSpan.FromSeconds(_samplesReturned / (double)(SampleRate * Channels));

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        int emptyPackets = 0;
        while (written < count)
        {
            if (_pending == null || _pendingOffset >= _pending.Length)
            {
                if (!_reader.HasNextPacket) break;
                _pending = _reader.DecodeNextPacket();
                _pendingOffset = 0;
                if (_pending == null || _pending.Length == 0)
                {
                    // Header packets decode to nothing; a run of them means a broken stream
                    if (++emptyPackets > 32) break;
                    continue;
                }
                emptyPackets = 0;
            }

            int take = Math.Min(count - written, _pending.Length - _pendingOffset);
            for (int i = 0; i < take; i++)
                buffer[offset + written + i] = _pending[_pendingOffset + i] / 32768f;
            _pendingOffset += take;
            written += take;
        }

        _samplesReturned += written;
        return written;
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        _reader.SeekTo(position);
        _pending = null;
        _pendingOffset = 0;
        _samplesReturned = 0;
        _seekBase = position;
    }

    public void Dispose()
    {
        try { _reader.Close(); } catch { }
        _file.Dispose();
    }
}
