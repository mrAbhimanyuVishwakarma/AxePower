using System;
using System.IO;
using System.Threading;
using AxePower.Core.Models.ShareScreen;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Captures what the PC is playing (WASAPI loopback) or the microphone, and writes the raw samples
/// into the encoder's stdin.
///
/// FFmpeg has no WASAPI input on Windows, so the capture happens here and the PCM is piped across.
/// The format is whatever the endpoint natively runs at, declared to FFmpeg via
/// <see cref="FfmpegInputArguments"/>, which avoids resampling twice.
///
/// The pump also manufactures silence. WASAPI loopback delivers no packets at all while the
/// endpoint is idle, and raw PCM on a pipe carries no timestamps, so a quiet PC starves FFmpeg's
/// audio input and it never starts muxing -- the stream simply never goes out. Topping the pipe up
/// against a wall clock keeps the byte rate honest whether or not anything is playing.
/// </summary>
public sealed class ShareAudioPump : IDisposable
{
    /// <summary>How often the top-up timer checks whether the pipe is behind real time.</summary>
    private static readonly TimeSpan TopUpInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Never inject more than this much silence at once, so a stall cannot burst.</summary>
    private const double MaxSilenceSeconds = 0.25;

    private readonly LogService _logService;
    private readonly object _writeLock = new();

    private WasapiCapture? _capture;
    private Stream? _destination;
    private Timer? _topUpTimer;
    private byte[]? _silence;

    private long _bytesWritten;
    private int _bytesPerSecond;
    private int _blockAlign;
    private long _startedTicks;
    private int _disposed;

    /// <summary>Raised when the capture stops on its own, e.g. the device was unplugged.</summary>
    public event EventHandler<string>? Failed;

    public bool IsRunning { get; private set; }

    /// <summary>The input flags FFmpeg needs to interpret the piped bytes. Empty until started.</summary>
    public string FfmpegInputArguments { get; private set; } = string.Empty;

    public ShareAudioPump(LogService logService) => _logService = logService;

    /// <summary>
    /// Reads the endpoint's mix format without opening a capture, so the encoder command line can
    /// declare the PCM format before the process that will receive it exists.
    /// Returns null when there is nothing to capture.
    /// </summary>
    public string? DescribeInput(ShareAudioMode mode, string? deviceId)
    {
        if (mode == ShareAudioMode.None) return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = ResolveDevice(enumerator, mode, deviceId);
            if (device == null) return null;

            return BuildInputArguments(device.AudioClient.MixFormat);
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not read the audio endpoint format", ex);
            return null;
        }
    }

    /// <summary>
    /// Opens the requested endpoint and begins writing PCM to <paramref name="destination"/>.
    /// Returns false when there is nothing to capture, which is not fatal: the caller can still
    /// share video, it just has to leave the audio stream out of the command line.
    /// </summary>
    public bool Start(ShareAudioMode mode, string? deviceId, Stream destination)
    {
        if (mode == ShareAudioMode.None) return false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = ResolveDevice(enumerator, mode, deviceId);
            if (device == null)
            {
                _logService.LogInfo("[ShareScreen] No audio endpoint available; sharing video only.");
                return false;
            }

            _destination = destination;

            _capture = mode == ShareAudioMode.SystemAudio
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device);

            var format = _capture.WaveFormat;
            FfmpegInputArguments = BuildInputArguments(format);

            _bytesPerSecond = format.AverageBytesPerSecond;
            _blockAlign = Math.Max(1, format.BlockAlign);
            _silence = new byte[(int)(_bytesPerSecond * MaxSilenceSeconds) + _blockAlign];
            _bytesWritten = 0;
            _startedTicks = DateTime.UtcNow.Ticks;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            _topUpTimer = new Timer(_ => TopUpWithSilence(), null, TopUpInterval, TopUpInterval);

            IsRunning = true;
            _logService.LogInfo(
                $"[ShareScreen] Capturing {mode} from '{device.FriendlyName}' " +
                $"({format.SampleRate} Hz, {format.Channels} ch, {format.Encoding}).");
            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not start audio capture; sharing video only", ex);
            Cleanup();
            return false;
        }
    }

    private static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, ShareAudioMode mode, string? deviceId)
    {
        var flow = mode == ShareAudioMode.SystemAudio ? DataFlow.Render : DataFlow.Capture;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try { return enumerator.GetDevice(deviceId); }
            catch { /* the saved device is gone; fall back to the default below */ }
        }

        try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    /// <summary>Maps the endpoint's wave format onto the matching FFmpeg raw-PCM demuxer flags.</summary>
    internal static string BuildInputArguments(WaveFormat format)
    {
        var sampleFormat = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "f32le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => "s16le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 32 => "s32le",
            // Shared-mode WASAPI hands back 32-bit float in practice; it is the safe assumption.
            _ => "f32le"
        };

        // Naming the layout stops FFmpeg having to guess it from the channel count.
        var layout = format.Channels == 1 ? "mono" : "stereo";

        return $"-f {sampleFormat} -ar {format.SampleRate} -ac {format.Channels} " +
               $"-channel_layout {layout} -i pipe:0";
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0) Write(e.Buffer, e.BytesRecorded);
    }

    /// <summary>
    /// Writes enough zeroes to bring the pipe back in line with real time.
    ///
    /// The deficit is measured against a wall clock rather than a fixed cadence, so when real audio
    /// is flowing this adds nothing, and when the endpoint goes idle it adds exactly the shortfall.
    /// That keeps audio and video from drifting apart across a long session.
    /// </summary>
    private void TopUpWithSilence()
    {
        var silence = _silence;
        if (silence == null || !IsRunning) return;

        var elapsedSeconds = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _startedTicks)) / (double)TimeSpan.TicksPerSecond;
        var expected = (long)(elapsedSeconds * _bytesPerSecond);
        var deficit = expected - Interlocked.Read(ref _bytesWritten);

        // One block of slack: chasing anything smaller would just add jitter.
        if (deficit < _blockAlign) return;

        var count = (int)Math.Min(deficit, silence.Length);
        count -= count % _blockAlign;
        if (count <= 0) return;

        Write(silence, count);
    }

    private void Write(byte[] buffer, int count)
    {
        var destination = _destination;
        if (destination == null) return;

        // Capture callbacks and the top-up timer both write here; interleaving them mid-buffer
        // would tear a sample frame in half.
        lock (_writeLock)
        {
            if (_destination == null) return;

            try
            {
                destination.Write(buffer, 0, count);
                Interlocked.Add(ref _bytesWritten, count);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
            {
                // The encoder went away. Dropping the stream keeps us off a dead pipe.
                _destination = null;
                IsRunning = false;
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
        if (e.Exception != null && Volatile.Read(ref _disposed) == 0)
        {
            _logService.LogError("[ShareScreen] Audio capture stopped unexpectedly", e.Exception);
            Failed?.Invoke(this, e.Exception.Message);
        }
    }

    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not stop audio capture cleanly", ex);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        IsRunning = false;

        _topUpTimer?.Dispose();
        _topUpTimer = null;

        lock (_writeLock)
        {
            _destination = null;
        }

        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { /* Best-effort teardown of the audio capture we are discarding. */ }
            _capture = null;
        }

        _silence = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}
