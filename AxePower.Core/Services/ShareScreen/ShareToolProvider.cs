using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Finds FFmpeg and MediaMTX, downloading them into <c>%LOCALAPPDATA%\AxePower\tools</c> when they
/// are missing. An FFmpeg the user already has (on PATH, or installed by winget) is preferred over
/// downloading a second copy.
/// </summary>
public sealed class ShareToolProvider : IShareToolProvider
{
    // gyan.dev publishes the canonical Windows builds; the "essentials" package is the smallest
    // one that still carries the NVENC, AMF and QSV encoders this feature needs.
    private const string FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string MediaMtxReleaseApi = "https://api.github.com/repos/bluenviron/mediamtx/releases/latest";

    private readonly LogService _logService;
    private readonly HttpClient _httpClient;

    public static string ToolsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxePower", "tools");

    private static string FfmpegDir => Path.Combine(ToolsRoot, "ffmpeg");
    private static string MediaMtxDir => Path.Combine(ToolsRoot, "mediamtx");

    public ShareToolProvider(LogService logService)
    {
        _logService = logService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AxePower");
    }

    // -- FFmpeg --------------------------------------------------------------

    public ShareToolStatus FindFfmpeg()
    {
        foreach (var candidate in FfmpegCandidates())
        {
            if (!File.Exists(candidate)) continue;

            var version = ReadFfmpegVersion(candidate);
            if (version.Length > 0) return new ShareToolStatus("FFmpeg", candidate, version, true);
        }

        return ShareToolStatus.Missing("FFmpeg");
    }

    private static IEnumerable<string> FfmpegCandidates()
    {
        // Our own copy first, so a broken PATH entry cannot shadow a good download.
        if (Directory.Exists(FfmpegDir))
        {
            foreach (var found in SafeEnumerate(FfmpegDir, "ffmpeg.exe")) yield return found;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;

            string combined;
            try { combined = Path.Combine(trimmed, "ffmpeg.exe"); }
            catch (ArgumentException) { continue; }   // a malformed PATH entry
            yield return combined;
        }

        // winget installs FFmpeg under a versioned folder in the user's package store.
        var wingetPackages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(wingetPackages)) yield break;

        IEnumerable<string> ffmpegPackages;
        try
        {
            ffmpegPackages = Directory.EnumerateDirectories(wingetPackages, "*FFmpeg*");
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var package in ffmpegPackages)
        {
            foreach (var found in SafeEnumerate(package, "ffmpeg.exe")) yield return found;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Take(4).ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private string ReadFfmpegVersion(string exePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-hide_banner -version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return string.Empty;

            var firstLine = process.StandardOutput.ReadLine() ?? string.Empty;
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception caught) when (ExpectedFailures.Process(caught)) { /* It exited as the timeout fired. */ }
                return string.Empty;
            }

            // "ffmpeg version 9.0.1-full_build-www.gyan.dev Copyright ..." -> "9.0.1-full_build..."
            const string prefix = "ffmpeg version ";
            if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return firstLine[prefix.Length..].Split(' ')[0];
            }

            return firstLine.Length > 0 ? firstLine : string.Empty;
        }
        catch (Exception ex)
        {
            _logService.LogError($"[ShareScreen] Could not read the FFmpeg version at {exePath}", ex);
            return string.Empty;
        }
    }

    public async Task<ShareToolStatus> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var existing = FindFfmpeg();
        if (existing.IsAvailable) return existing;

        _logService.LogInfo("[ShareScreen] FFmpeg not found. Downloading the Windows build...");
        try
        {
            Directory.CreateDirectory(FfmpegDir);
            await DownloadAndExtractAsync(FfmpegDownloadUrl, FfmpegDir, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] FFmpeg download failed", ex);
            return ShareToolStatus.Missing("FFmpeg");
        }

        return FindFfmpeg();
    }

    // -- MediaMTX ------------------------------------------------------------

    public ShareToolStatus FindMediaMtx()
    {
        var exe = Path.Combine(MediaMtxDir, "mediamtx.exe");
        if (!File.Exists(exe)) return ShareToolStatus.Missing("MediaMTX");

        var versionFile = Path.Combine(MediaMtxDir, "version.txt");
        var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "installed";
        return new ShareToolStatus("MediaMTX", exe, version, true);
    }

    public async Task<ShareToolStatus> EnsureMediaMtxAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var existing = FindMediaMtx();
        if (existing.IsAvailable) return existing;

        _logService.LogInfo("[ShareScreen] MediaMTX not found. Downloading the latest release...");
        try
        {
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(MediaMtxReleaseApi, ct);
            var asset = release?.Assets?.FirstOrDefault(a =>
                a.Name != null &&
                a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("amd64", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (asset?.BrowserDownloadUrl == null)
            {
                _logService.LogError("[ShareScreen] No Windows amd64 asset in the latest MediaMTX release.");
                return ShareToolStatus.Missing("MediaMTX");
            }

            Directory.CreateDirectory(MediaMtxDir);
            await DownloadAndExtractAsync(asset.BrowserDownloadUrl, MediaMtxDir, progress, ct);

            if (!string.IsNullOrWhiteSpace(release?.TagName))
            {
                try { File.WriteAllText(Path.Combine(MediaMtxDir, "version.txt"), release!.TagName); }
                catch (IOException) { /* the version label is a nicety, not a requirement */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] MediaMTX download failed", ex);
            return ShareToolStatus.Missing("MediaMTX");
        }

        return FindMediaMtx();
    }

    // -- Shared download path ------------------------------------------------

    private async Task DownloadAndExtractAsync(string url, string targetDir, IProgress<double>? progress, CancellationToken ct)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"axepower_share_{Guid.NewGuid():N}.zip");
        try
        {
            using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0L;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    // Without a Content-Length there is nothing honest to report, so stay quiet.
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress?.Report(1.0);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: a partial download in the temp folder is harmless and never run. */ }
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
