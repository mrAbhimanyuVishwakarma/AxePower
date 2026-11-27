using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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
    string AssetNameMustContain,
    PortableZipRelease? ZipRelease = null);

public sealed record PortableZipRelease(string Repository, string FileNameSuffix, string FallbackSha256);

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

    public const string VeyraReleases = "https://github.com/Likely7/Veyra-NRVideo/releases";

    public static PortableToolSpec? Find(string? id) => id switch
    {
        "dlss5swapper" => Dlss5Swapper,
        "veyra-nvidia" => VeyraNvidia,
        "veyra-amd" => VeyraAmd,
        _ => null
    };

    private static readonly PortableToolSpec VeyraNvidia = Veyra("NVIDIA", "e09150d1594aa9416fa27457a42b4d50b5d814e05471216bef95b9303544386b");
    private static readonly PortableToolSpec VeyraAmd = Veyra("AMD", "b6440744dee2ae9422838f486873c84ed8d6b1bc36b6ad29222e8eda40077bf8");

    private static PortableToolSpec Veyra(string gpu, string fallbackSha256)
    {
        const string version = "2.2.1";
        var suffix = $"-{gpu}-win64-portable.zip";
        return new PortableToolSpec(
            $"Veyra ({gpu})",
            "https://api.github.com/repos/Likely7/Veyra-NRVideo/releases/latest",
            $"{VeyraReleases}/download/v{version}/Veyra-{version}{suffix}",
            $"v{version}",
            $"Veyra-{gpu}",
            "Veyra.exe",
            suffix,
            new PortableZipRelease("Likely7/Veyra-NRVideo", suffix, fallbackSha256));
    }
}

/// <summary>
/// Downloads GitHub release assets to a temporary path and checks them before publishing them.
/// Portable ZIPs also require the publisher's release checksum and extract into a staging folder.
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
        if (spec.ZipRelease is { } zip)
            return await DownloadZipAsync(spec, zip, folder, progress, cancellationToken).ConfigureAwait(false);

        var tag = $"[{spec.DisplayName}]";
        progress.Report($"\n{tag} Querying GitHub releases for latest portable executable...");
        var (downloadUrl, version) = await ResolveLatestAsync(spec, cancellationToken).ConfigureAwait(false);

        if (!PackageInput.TryDownloadUrl(downloadUrl, PortableTools.AllowedDownloadHosts, out var uri) || PackageInput.SafeDownloadFileName(uri, ".exe") is null)
        {
            progress.Report($"{tag} The release points somewhere unexpected; using the known release instead.");
            _log?.LogWarning($"[SoftwareHub] Ignored release asset address for {spec.DisplayName}: {downloadUrl}");
            if (!PackageInput.TryDownloadUrl(spec.FallbackDownloadUrl, PortableTools.AllowedDownloadHosts, out uri) || PackageInput.SafeDownloadFileName(uri, ".exe") is null)
            {
                // HttpClient accepts a null address, so this has to be refused here rather than
                // left to fail somewhere less clear.
                progress.Report($"\n[ERROR] {spec.DisplayName} has no trusted download address; nothing was downloaded.");
                _log?.LogWarning($"[SoftwareHub] {spec.DisplayName}: the known release address is not on the allowed hosts either.");
                return null;
            }
            version = spec.FallbackVersion;
        }

        progress.Report($"{tag} Target release: {version}");
        progress.Report($"{tag} Downloading portable build from: {uri}");

        Directory.CreateDirectory(folder);
        var exePath = Path.Combine(folder, spec.ExeName);
        var partial = exePath + ".download";
        try
        {
            long totalRead = await DownloadAssetAsync(uri, partial, progress, cancellationToken).ConfigureAwait(false);

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

    private async Task<long> DownloadAssetAsync(Uri uri, string path, IProgress<string> progress, CancellationToken cancellationToken, long sizeLimit = long.MaxValue)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureReleaseDownloadHost(response, uri);

        long? totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes > sizeLimit) throw new IOException("The release exceeds the download size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long totalRead = 0;
        int lastReport = -1;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > sizeLimit) throw new IOException("The release exceeds the download size limit.");
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (totalBytes is > 0)
            {
                int percent = (int)(totalRead * 100 / totalBytes.Value);
                if (percent != lastReport && (percent % 15 == 0 || percent == 100))
                {
                    lastReport = percent;
                    progress.Report($"[Download Progress] {percent}% ({totalRead / (1024 * 1024)} MB / {totalBytes.Value / (1024 * 1024)} MB)");
                }
            }
        }
        return totalRead;
    }

    private static void EnsureReleaseDownloadHost(HttpResponseMessage response, Uri requestUri)
    {
        var finalUri = response.RequestMessage?.RequestUri ?? requestUri;
        if (!PackageInput.TryDownloadUrl(finalUri.AbsoluteUri, PortableTools.AllowedDownloadHosts, out _))
            throw new HttpRequestException("The release download redirected to an unexpected address.");
    }

    private async Task<string?> DownloadZipAsync(PortableToolSpec tool, PortableZipRelease zip, string folder, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var exePath = Path.Combine(folder, tool.ExeName);
        var parent = Path.GetDirectoryName(folder)!;
        string? staging = null;
        try
        {
            if (Directory.Exists(folder))
            {
                if (IsWindowsExecutable(exePath)) return exePath;
                throw new IOException("The application folder already exists. Its contents were left untouched.");
            }

            progress.Report($"[{tool.DisplayName}] Checking GitHub for the latest release...");
            var (url, tag) = await ResolveLatestAsync(tool, cancellationToken).ConfigureAwait(false);
            if (!TryGetZipRelease(url, tag, zip, out var uri, out var releaseUrl))
            {
                tag = tool.FallbackVersion;
                if (!TryGetZipRelease(tool.FallbackDownloadUrl, tag, zip, out uri, out releaseUrl))
                    throw new IOException("There is no valid official release address.");
            }

            var checksum = uri.AbsoluteUri == tool.FallbackDownloadUrl
                ? zip.FallbackSha256
                : await ReadZipChecksumAsync(new Uri(releaseUrl + "SHA256SUMS.txt"), Path.GetFileName(uri.LocalPath), cancellationToken).ConfigureAwait(false);
            progress.Report($"[{tool.DisplayName}] Downloading {tag}...");
            staging = Path.Combine(parent, $".axepower-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var download = Path.Combine(staging, "release.zip");
            var extracted = Path.Combine(staging, "program");
            await DownloadAssetAsync(uri, download, progress, cancellationToken, sizeLimit: 2L * 1024 * 1024 * 1024).ConfigureAwait(false);

            progress.Report($"[{tool.DisplayName}] Verifying SHA-256...");
            await using (var stream = File.OpenRead(download))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download does not match the official SHA-256 checksum.");
            }

            progress.Report($"[{tool.DisplayName}] Extracting the verified release...");
            using (var archive = ZipFile.OpenRead(download))
            {
                if (archive.Entries.Sum(entry => entry.Length) > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The release exceeds the extraction size limit.");
                await Task.Run(() => archive.ExtractToDirectory(extracted), cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var programRoot = IsWindowsExecutable(Path.Combine(extracted, tool.ExeName))
                ? extracted
                : Path.Combine(extracted, Path.GetFileNameWithoutExtension(uri.LocalPath));
            if (!IsWindowsExecutable(Path.Combine(programRoot, tool.ExeName)))
                throw new InvalidDataException("The verified archive did not contain the expected program.");

            Directory.Move(programRoot, folder);
            progress.Report($"[{tool.DisplayName}] Download verified and ready to run.");
            return exePath;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is (HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException))
        {
            progress.Report($"[ERROR] {tool.DisplayName} could not be prepared: {ex.Message}");
            _log?.LogError($"[SoftwareHub] {tool.DisplayName} download failed", ex);
            return null;
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log?.LogWarning($"[SoftwareHub] Could not remove the temporary download folder: {ex.Message}");
                }
            }
        }
    }

    private static bool TryGetZipRelease(string url, string tag, PortableZipRelease zip,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri, out string releaseUrl)
    {
        uri = null;
        releaseUrl = $"https://github.com/{zip.Repository}/releases/download/{tag}/";
        return tag.StartsWith('v') && Version.TryParse(tag.AsSpan(1), out _)
            && PackageInput.TryDownloadUrl(url, PortableTools.AllowedDownloadHosts, out uri)
            && url.StartsWith(releaseUrl, StringComparison.Ordinal)
            && PackageInput.SafeDownloadFileName(uri, ".zip") is { } fileName
            && fileName.EndsWith(zip.FileNameSuffix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(url, releaseUrl + fileName, StringComparison.Ordinal);
    }

    private async Task<string> ReadZipChecksumAsync(Uri uri, string fileName, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureReleaseDownloadHost(response, uri);
        await response.Content.LoadIntoBufferAsync(16384, cancellationToken).ConfigureAwait(false);
        var checksums = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in checksums.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0].Length == 64 && fields[1].TrimStart('*') == fileName)
                return fields[0];
        }
        throw new InvalidDataException("The official release did not publish a checksum for this archive.");
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
            var releaseTag = doc.RootElement.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
                ? tagProp.GetString() : null;
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    if (spec.ZipRelease is { } zip
                        ? !name.EndsWith(zip.FileNameSuffix, StringComparison.OrdinalIgnoreCase)
                        : !name.Contains(spec.AssetNameMustContain, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String)
                    {
                        url = u.GetString() ?? url;
                        version = releaseTag ?? version;
                    }
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
