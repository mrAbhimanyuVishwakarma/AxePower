using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.SoftwareHub;

/// <summary>A portable tool that is downloaded from its GitHub releases rather than installed.</summary>
public sealed record PortableToolSpec(
    string DisplayName,
    string ReleasesApiUrl,
    string FallbackDownloadUrl,
    string FallbackVersion,
    string FolderName,
    string ExeName,
    string AssetNameMustContain);

public interface IPortableToolDownloader
{
    /// <summary>Downloads the tool's latest portable build into <paramref name="folder"/>. Returns the exe path, or null on failure.</summary>
    Task<string?> DownloadAsync(PortableToolSpec spec, string folder, IProgress<string> progress, CancellationToken cancellationToken = default);
}

public static class PortableTools
{
    public const string Dlss5SwapperFolder = "DLSS5-Swapper";
    public const string Dlss5SwapperExe = "DLSS5-Swapper-portable.exe";

    /// <summary>Release downloads may only come from GitHub and its asset CDN.</summary>
    public static readonly string[] AllowedDownloadHosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    public static readonly PortableToolSpec Dlss5Swapper = new(
        "DLSS 5 Swapper",
        "https://api.github.com/repos/rakanki911/DLSS5-Swapper/releases/latest",
        "https://github.com/rakanki911/DLSS5-Swapper/releases/download/v2.2.4/DLSS5-Swapper-2.2.4-portable.exe",
        "v2.2.4",
        Dlss5SwapperFolder,
        Dlss5SwapperExe,
        "portable");
}

/// <summary>
/// Downloads a portable tool. The release lookup is advisory: an asset address it returns is used
/// only if it is https on an allowed host and names an .exe; the file is written to a temporary
/// name, checked to be a Windows executable, then moved into place, so a failed or hostile
/// download never replaces a working copy.
/// </summary>
public sealed class GitHubPortableToolDownloader : IPortableToolDownloader
{
    private readonly HttpClient _http;
    private readonly LogService? _log;

    public GitHubPortableToolDownloader(LogService? log = null, HttpMessageHandler? handler = null)
    {
        _log = log;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AxePower-Client");
    }

    public async Task<string?> DownloadAsync(PortableToolSpec spec, string folder, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        var tag = $"[{spec.DisplayName}]";
        progress.Report($"\n{tag} Querying GitHub releases for latest portable executable...");
        var (downloadUrl, version) = await ResolveLatestAsync(spec, cancellationToken).ConfigureAwait(false);

        if (!PackageInput.TryDownloadUrl(downloadUrl, PortableTools.AllowedDownloadHosts, out var uri) || PackageInput.SafeDownloadFileName(uri, ".exe") is null)
        {
            progress.Report($"{tag} The release points somewhere unexpected; using the known release instead.");
            _log?.LogWarning($"[SoftwareHub] Ignored release asset address for {spec.DisplayName}: {downloadUrl}");
            PackageInput.TryDownloadUrl(spec.FallbackDownloadUrl, PortableTools.AllowedDownloadHosts, out uri);
            version = spec.FallbackVersion;
        }

        progress.Report($"{tag} Target release: {version}");
        progress.Report($"{tag} Downloading portable build from: {uri}");

        Directory.CreateDirectory(folder);
        var exePath = Path.Combine(folder, spec.ExeName);
        var partial = exePath + ".download";
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            long totalRead = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read, lastReport = -1;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalRead += read;
                    if (totalBytes is > 0)
                    {
                        int percent = (int)(totalRead * 100 / totalBytes.Value);
                        if (percent != lastReport && percent % 15 == 0)
                        {
                            lastReport = percent;
                            progress.Report($"[Download Progress] {percent}% ({totalRead / (1024 * 1024)} MB / {totalBytes.Value / (1024 * 1024)} MB)");
                        }
                    }
                }
            }

            if (!IsWindowsExecutable(partial))
            {
                progress.Report($"\n[ERROR] {spec.DisplayName} download is not a Windows program; discarded.");
                return null;
            }

            File.Move(partial, exePath, overwrite: true);
            progress.Report($"\n{tag} ✓ Download completed successfully ({totalRead / (1024 * 1024)} MB)!");
            return exePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress.Report($"\n[CANCELLED] {spec.DisplayName} download cancelled.");
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            progress.Report($"\n[ERROR] {spec.DisplayName} download failed: {ex.Message}");
            _log?.LogError($"[SoftwareHub] {spec.DisplayName} download failed", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception) { /* Best-effort: a leftover .download file is overwritten by the next attempt. */ }
        }
    }

    private async Task<(string Url, string Version)> ResolveLatestAsync(PortableToolSpec spec, CancellationToken cancellationToken)
    {
        string url = spec.FallbackDownloadUrl, version = spec.FallbackVersion;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var json = await _http.GetStringAsync(spec.ReleasesApiUrl, timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (url, version);
            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String)
                version = tagProp.GetString() ?? version;
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    if (!name.Contains(spec.AssetNameMustContain, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String)
                        url = u.GetString() ?? url;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log?.LogWarning($"[SoftwareHub] GitHub API lookup for {spec.DisplayName}: {ex.Message}. Using direct release fallback.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (url, version);
    }

    /// <summary>True when the file starts with the "MZ" header of a Windows executable.</summary>
    public static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length > 64 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception)
        {
            // Fails closed: an unreadable download is not treated as a program.
            return false;
        }
    }
}
