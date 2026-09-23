using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Asks FFmpeg which encoders it was built with, then makes each candidate encode a couple of
/// synthetic frames to discard the ones the driver will not actually open.
/// </summary>
public sealed class ShareEncoderProbe : IShareEncoderProbe
{
    private readonly LogService _logService;

    /// <summary>Every encoder we know how to drive, in the blueprint's priority order.</summary>
    private static readonly ShareEncoder[] KnownEncoders =
    {
        new("h264_nvenc", ShareEncoderVendor.Nvidia,   ShareVideoCodec.H264, "NVIDIA NVENC - H.264"),
        new("h264_amf",   ShareEncoderVendor.Amd,      ShareVideoCodec.H264, "AMD AMF - H.264"),
        new("h264_qsv",   ShareEncoderVendor.Intel,    ShareVideoCodec.H264, "Intel Quick Sync - H.264"),
        new("libx264",    ShareEncoderVendor.Software, ShareVideoCodec.H264, "Software x264 - H.264"),

        new("hevc_nvenc", ShareEncoderVendor.Nvidia,   ShareVideoCodec.Hevc, "NVIDIA NVENC - HEVC"),
        new("hevc_amf",   ShareEncoderVendor.Amd,      ShareVideoCodec.Hevc, "AMD AMF - HEVC"),
        new("hevc_qsv",   ShareEncoderVendor.Intel,    ShareVideoCodec.Hevc, "Intel Quick Sync - HEVC"),

        new("av1_nvenc",  ShareEncoderVendor.Nvidia,   ShareVideoCodec.Av1,  "NVIDIA NVENC - AV1"),
        new("av1_amf",    ShareEncoderVendor.Amd,      ShareVideoCodec.Av1,  "AMD AMF - AV1"),
        new("av1_qsv",    ShareEncoderVendor.Intel,    ShareVideoCodec.Av1,  "Intel Quick Sync - AV1")
    };

    public ShareEncoderProbe(LogService logService) => _logService = logService;

    public async Task<IReadOnlyList<ShareEncoder>> ProbeAsync(string ffmpegPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return Array.Empty<ShareEncoder>();

        var listed = await ListBuiltInEncodersAsync(ffmpegPath, ct);
        var verified = new List<ShareEncoder>();

        foreach (var encoder in KnownEncoders)
        {
            if (!listed.Contains(encoder.FfmpegName)) continue;

            // Software x264 is always there if it is listed; no point spending a second on it.
            if (!encoder.IsHardware || await CanInitialiseAsync(ffmpegPath, encoder.FfmpegName, ct))
            {
                verified.Add(encoder);
            }
            else
            {
                _logService.LogInfo($"[ShareScreen] {encoder.FfmpegName} is built in but the driver refused it; not offering it.");
            }
        }

        return verified;
    }

    public ShareEncoder? SelectBest(IReadOnlyList<ShareEncoder> available, ShareVideoCodec codec, string? preferredFfmpegName)
    {
        if (available.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredFfmpegName))
        {
            var forced = available.FirstOrDefault(e => e.FfmpegName == preferredFfmpegName);
            if (forced != null) return forced;
        }

        var forCodec = available.Where(e => e.Codec == codec).ToList();

        // H.264 is the compatibility baseline: if the requested codec has no encoder, fall back to
        // it rather than failing, because every browser can decode it.
        if (forCodec.Count == 0 && codec != ShareVideoCodec.H264)
        {
            forCodec = available.Where(e => e.Codec == ShareVideoCodec.H264).ToList();
        }

        return forCodec
            .OrderBy(e => e.Vendor switch
            {
                ShareEncoderVendor.Nvidia => 0,
                ShareEncoderVendor.Amd => 1,
                ShareEncoderVendor.Intel => 2,
                _ => 3   // software last: never the default for gaming
            })
            .FirstOrDefault();
    }

    private async Task<HashSet<string>> ListBuiltInEncodersAsync(string ffmpegPath, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var output = await RunAsync(ffmpegPath, "-hide_banner -encoders", TimeSpan.FromSeconds(15), ct);

            // Lines look like " V....D h264_nvenc  NVIDIA NVENC H.264 encoder (codec h264)".
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Length >= 6 && parts[0].StartsWith("V", StringComparison.Ordinal))
                {
                    names.Add(parts[1]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not list FFmpeg encoders", ex);
        }

        return names;
    }

    /// <summary>Encodes two frames of colour bars and reports whether FFmpeg exited cleanly.</summary>
    private async Task<bool> CanInitialiseAsync(string ffmpegPath, string encoderName, CancellationToken ct)
    {
        try
        {
            var args = $"-hide_banner -f lavfi -i testsrc=size=640x360:rate=30 -frames:v 2 " +
                       $"-c:v {encoderName} -f null -";
            await RunAsync(ffmpegPath, args, TimeSpan.FromSeconds(20), ct, requireSuccess: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs FFmpeg and returns stdout+stderr. Throws when it fails and success was required.</summary>
    private static async Task<string> RunAsync(
        string ffmpegPath, string arguments, TimeSpan timeout, CancellationToken ct, bool requireSuccess = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"FFmpeg did not finish within {timeout.TotalSeconds:0}s.");
        }

        if (requireSuccess && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}.");
        }

        return await stdout + "\n" + await stderr;
    }
}
