using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Owns the FFmpeg process that captures, encodes and publishes, and turns its progress output
/// into <see cref="ShareTelemetry"/>.
///
/// Every number the UI shows about the stream originates here, so that nothing is claimed about
/// the stream that FFmpeg did not actually report.
/// </summary>
public sealed class ShareEncodePipeline : IDisposable
{
    private readonly LogService _logService;
    private readonly ChildProcessJob _job = new();

    private Process? _process;
    private ShareAudioPump? _audioPump;
    private DateTime _startedUtc;
    private int _disposed;

    /// <summary>Fires roughly twice a second while the stream is live.</summary>
    public event EventHandler<ShareTelemetry>? TelemetryUpdated;

    /// <summary>Fires when FFmpeg exits without being asked to. The string is a short reason.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>The last few FFmpeg warnings, for the error message when something goes wrong.</summary>
    private readonly Queue<string> _recentErrors = new();

    public bool IsRunning => _process is { HasExited: false };

    public ShareEncodePipeline(LogService logService) => _logService = logService;

    public void Start(
        string ffmpegPath,
        CaptureDisplay display,
        QualityPreset preset,
        ShareEncoder encoder,
        ShareScreenSettings settings,
        ShareRoom room,
        ShareAudioPump audioPump)
    {
        Stop();

        // The command line must declare the PCM format before FFmpeg exists to receive it, so the
        // endpoint is inspected up front and only opened for real once the pipe is there.
        var deviceId = ResolveAudioDeviceId(settings);
        var audioArguments = audioPump.DescribeInput(settings.AudioMode, deviceId);
        var wantsAudio = !string.IsNullOrWhiteSpace(audioArguments);

        var arguments = FfmpegArgumentBuilder.Build(display, preset, encoder, settings, room, audioArguments);
        _logService.LogInfo($"[ShareScreen] Starting encoder: ffmpeg {arguments}");

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                RedirectStandardInput = wantsAudio,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += OnProgressLine;
        _process.ErrorDataReceived += OnErrorLine;
        _process.Exited += OnExited;

        _process.Start();
        // Assign before anything else can throw, so FFmpeg cannot outlive AxePower and keep
        // streaming the screen with no window left to stop it.
        _job.Assign(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _startedUtc = DateTime.UtcNow;

        if (wantsAudio)
        {
            audioPump.Start(settings.AudioMode, deviceId, _process.StandardInput.BaseStream);
        }

        _audioPump = audioPump;
    }

    private static string? ResolveAudioDeviceId(ShareScreenSettings settings) =>
        settings.AudioMode == ShareAudioMode.Microphone
            ? settings.MicrophoneDeviceId
            : settings.SystemAudioDeviceId;

    // -- Telemetry -----------------------------------------------------------

    private readonly Dictionary<string, string> _progressFields = new(StringComparer.Ordinal);

    /// <summary>
    /// Parses the <c>-progress</c> stream. FFmpeg emits a block of key=value lines terminated by
    /// <c>progress=</c>, so the block is accumulated and published as one consistent snapshot.
    /// </summary>
    private void OnProgressLine(object? sender, DataReceivedEventArgs e)
    {
        if (e.Data == null) return;

        var separator = e.Data.IndexOf('=');
        if (separator <= 0) return;

        var key = e.Data[..separator].Trim();
        var value = e.Data[(separator + 1)..].Trim();
        _progressFields[key] = value;

        if (key != "progress") return;

        var telemetry = new ShareTelemetry
        {
            Fps = ParseDouble(_progressFields.GetValueOrDefault("fps")),
            BitrateMbps = ParseBitrateMbps(_progressFields.GetValueOrDefault("bitrate")),
            DroppedFrames = (long)ParseDouble(_progressFields.GetValueOrDefault("drop_frames")),
            DuplicatedFrames = (long)ParseDouble(_progressFields.GetValueOrDefault("dup_frames")),
            BytesSent = (long)ParseDouble(_progressFields.GetValueOrDefault("total_size")),
            Speed = ParseSpeed(_progressFields.GetValueOrDefault("speed")),
            Elapsed = DateTime.UtcNow - _startedUtc
        };

        TelemetryUpdated?.Invoke(this, telemetry);
    }

    internal static double ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>"16000.4kbits/s" becomes 16.0004. "N/A" becomes 0.</summary>
    internal static double ParseBitrateMbps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var trimmed = text.Trim();
        var digitsEnd = 0;
        while (digitsEnd < trimmed.Length && (char.IsDigit(trimmed[digitsEnd]) || trimmed[digitsEnd] is '.' or '-'))
        {
            digitsEnd++;
        }

        if (digitsEnd == 0) return 0;

        var number = ParseDouble(trimmed[..digitsEnd]);
        var unit = trimmed[digitsEnd..];

        if (unit.StartsWith("kbits", StringComparison.OrdinalIgnoreCase)) return number / 1000.0;
        if (unit.StartsWith("mbits", StringComparison.OrdinalIgnoreCase)) return number;
        if (unit.StartsWith("bits", StringComparison.OrdinalIgnoreCase)) return number / 1_000_000.0;
        return number / 1000.0;
    }

    /// <summary>"1.01x" becomes 1.01. Anything unparseable reads as 1.0, i.e. "no complaint".</summary>
    internal static double ParseSpeed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1.0;

        var trimmed = text.Trim().TrimEnd('x', 'X');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 1.0;
    }

    private void OnErrorLine(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        _logService.LogError($"[ShareScreen/ffmpeg] {e.Data}");

        lock (_recentErrors)
        {
            _recentErrors.Enqueue(e.Data);
            while (_recentErrors.Count > 5) _recentErrors.Dequeue();
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var process = _process;
        if (process == null) return;   // a deliberate Stop() already cleared it

        string reason;
        lock (_recentErrors)
        {
            reason = _recentErrors.Count > 0
                ? string.Join(" ", _recentErrors)
                : $"The encoder stopped unexpectedly (exit code {process.ExitCode}).";
        }

        Failed?.Invoke(this, reason);
    }

    public void Stop()
    {
        var process = _process;
        _process = null;   // cleared first so OnExited treats this as deliberate

        _audioPump?.Stop();
        _audioPump = null;

        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(4000);
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not stop the encoder cleanly", ex);
        }
        finally
        {
            process.Dispose();
        }

        lock (_recentErrors) _recentErrors.Clear();
        _progressFields.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _job.Dispose();
    }
}
