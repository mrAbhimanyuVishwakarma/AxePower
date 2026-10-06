using System;
using System.Collections.Generic;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AxePower.Core.Services.ShareScreen;

public interface IRoomVoiceService : IDisposable
{
    bool IsRunning { get; }
    bool IsMuted { get; }
    bool IsDeafened { get; }
    event EventHandler<byte[]>? EncodedPacketReady;
    bool Start();
    void Stop();
    void SetMuted(bool muted);
    void SetDeafened(bool deafened);
    void ReceivePacket(byte[] opusPacket);
}

/// <summary>
/// Captures the default Windows communications microphone, encodes 20 ms mono Opus frames,
/// and renders verified remote Opus frames through the default output device.
/// </summary>
public sealed class RoomVoiceService : IRoomVoiceService
{
    private const int SampleRate = 48_000;
    private const int FrameSamples = 960;
    private const int MaxOpusPacketBytes = 4000;

    private readonly LogService _log;
    private readonly object _captureLock = new();
    private readonly object _decodeLock = new();
    private readonly List<short> _pendingSamples = new(FrameSamples * 2);

    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;

    public bool IsRunning { get; private set; }
    public bool IsMuted { get; private set; }
    public bool IsDeafened { get; private set; }

    public event EventHandler<byte[]>? EncodedPacketReady;

    public RoomVoiceService(LogService log) => _log = log;

    public bool Start()
    {
        if (IsRunning) return true;
        try
        {
            _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 32_000;
            _encoder.UseVBR = true;
            _encoder.UseInbandFEC = true;
            _decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);

            _playbackBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1), TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _output = new WasapiOut(AudioClientShareMode.Shared, true, 50);
            _output.Init(_playbackBuffer);
            _output.Play();

            _capture = new WasapiCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("[FriendsVoice] Could not start the Windows voice devices", ex);
            Stop();
            return false;
        }
    }

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        if (muted)
        {
            lock (_captureLock) _pendingSamples.Clear();
        }
    }

    public void SetDeafened(bool deafened)
    {
        IsDeafened = deafened;
        if (deafened) _playbackBuffer?.ClearBuffer();
    }

    public void ReceivePacket(byte[] opusPacket)
    {
        if (!IsRunning || IsDeafened || opusPacket.Length is 0 or > MaxOpusPacketBytes || _decoder == null || _playbackBuffer == null)
            return;

        try
        {
            lock (_decodeLock)
            {
                var pcm = new short[FrameSamples];
                var decoded = _decoder.Decode(opusPacket, pcm, FrameSamples, false);
                if (decoded <= 0) return;
                var bytes = new byte[decoded * sizeof(short)];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                _playbackBuffer.AddSamples(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[FriendsVoice] Dropped an invalid audio packet: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning || IsMuted || _capture == null || _encoder == null) return;
        var samples = ConvertToMono48k(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
        if (samples.Length == 0) return;

        try
        {
            lock (_captureLock)
            {
                _pendingSamples.AddRange(samples);
                while (_pendingSamples.Count >= FrameSamples)
                {
                    var frame = _pendingSamples.GetRange(0, FrameSamples).ToArray();
                    _pendingSamples.RemoveRange(0, FrameSamples);
                    var encoded = new byte[MaxOpusPacketBytes];
                    var length = _encoder.Encode(frame, FrameSamples, encoded, encoded.Length);
                    if (length <= 0) continue;
                    Array.Resize(ref encoded, length);
                    EncodedPacketReady?.Invoke(this, encoded);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[FriendsVoice] Dropped a microphone frame: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) _log.LogError("[FriendsVoice] Microphone capture stopped", e.Exception);
    }

    internal static short[] ConvertToMono48k(byte[] buffer, int byteCount, WaveFormat format)
    {
        if (byteCount <= 0 || format.Channels <= 0 || format.SampleRate <= 0) return Array.Empty<short>();
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameBytes = bytesPerSample * format.Channels;
        var inputFrames = byteCount / frameBytes;
        if (inputFrames <= 0) return Array.Empty<short>();

        var mono = new float[inputFrames];
        for (var frame = 0; frame < inputFrames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * frameBytes + channel * bytesPerSample;
                sum += ReadSample(buffer, offset, format.Encoding, format.BitsPerSample);
            }
            mono[frame] = (float)(sum / format.Channels);
        }

        var outputFrames = Math.Max(1, (int)Math.Round(inputFrames * (SampleRate / (double)format.SampleRate)));
        var output = new short[outputFrames];
        for (var i = 0; i < outputFrames; i++)
        {
            var position = i * (format.SampleRate / (double)SampleRate);
            var left = Math.Min((int)position, inputFrames - 1);
            var right = Math.Min(left + 1, inputFrames - 1);
            var fraction = position - left;
            var value = mono[left] + (mono[right] - mono[left]) * fraction;
            output[i] = (short)Math.Clamp((int)Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
        }
        return output;
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormatEncoding encoding, int bits)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat && bits == 32)
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1f, 1f);
        if (encoding != WaveFormatEncoding.Pcm) return 0;
        return bits switch
        {
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            24 => ReadInt24(buffer, offset) / 8388608f,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0
        };
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    public void Stop()
    {
        IsRunning = false;
        try { _capture?.StopRecording(); } catch { /* Best-effort teardown of the microphone; the device is released on dispose. */ }
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
        }
        try { _output?.Stop(); } catch { /* Best-effort teardown of the speakers; the device is released on dispose. */ }
        _output?.Dispose();
        _capture = null;
        _output = null;
        _playbackBuffer = null;
        _encoder = null;
        _decoder = null;
        lock (_captureLock) _pendingSamples.Clear();
    }

    public void Dispose() => Stop();
}
