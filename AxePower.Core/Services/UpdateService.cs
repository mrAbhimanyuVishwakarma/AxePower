using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services;

public class UpdateService
{
    private readonly LogService _logService;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadHttpClient;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public bool IsDownloadInProgress { get; private set; }
    public double CurrentDownloadProgress { get; private set; }
    public event EventHandler<UpdateDownloadProgressEventArgs>? DownloadProgressChanged;

    private static readonly string[] ReleaseEndpoints = new[]
    {
        "https://api.github.com/repos/mrAbhimanyuVishwakarma/AxePower_Download/releases/latest",
        "https://api.github.com/repos/mrAbhimanyuVishwakarma/AxePower/releases/latest"
    };

    public UpdateService(LogService logService)
    {
        _logService = logService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AxePower-App/" + GetCurrentVersionString());
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

        // Release assets can take longer than the API request timeout to begin or finish,
        // especially when GitHub redirects to its object store. Per-stage cancellation is
        // applied in DownloadUpdateAsync instead of one short timeout for the whole transfer.
        _downloadHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _downloadHttpClient.DefaultRequestHeaders.Add("User-Agent", "AxePower-App/" + GetCurrentVersionString());
        _downloadHttpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");
    }

    /// <summary>Single fallback when no assembly version is available; the assembly version is the source of truth.</summary>
    public const string FallbackVersion = "2.0.3";

    public string GetCurrentVersionString()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version?.ToString(3) ?? FallbackVersion;
    }

    public Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? Version.Parse(FallbackVersion);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();

        foreach (var endpoint in ReleaseEndpoints)
        {
            try
            {
                _logService.LogInfo($"Checking for updates via {endpoint}...");
                using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var release = await response.Content.ReadFromJsonAsync<GitHubReleaseResponse>(cancellationToken: cancellationToken);
                if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                {
                    continue;
                }

                var parsedVersion = ParseSemanticVersion(release.TagName);
                if (parsedVersion == null)
                {
                    continue;
                }

                bool hasUpdate = parsedVersion > currentVersion;
                var (bestAssetUrl, assetName, sizeBytes) = FindBestAsset(release.Assets);

                return new UpdateCheckResult
                {
                    HasUpdate = hasUpdate,
                    LatestVersion = parsedVersion.ToString(),
                    RawTag = release.TagName,
                    ReleaseTitle = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                    ReleaseNotes = FormatReleaseNotesForDisplay(release.Body ?? "Performance improvements and bug fixes."),
                    DownloadUrl = bestAssetUrl ?? release.HtmlUrl,
                    AssetName = assetName,
                    FileSizeBytes = sizeBytes,
                    PublishedAt = release.PublishedAt,
                    ReleaseUrl = release.HtmlUrl ?? "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases"
                };
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Update check failed for {endpoint}: {ex.Message}");
            }
        }

        return new UpdateCheckResult
        {
            HasUpdate = false,
            LatestVersion = currentVersion.ToString(),
            RawTag = "v" + currentVersion.ToString(),
            ReleaseTitle = "Up to date",
            ReleaseNotes = string.Empty,
            ReleaseUrl = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases"
        };
    }

    public async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_Update");
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
        {
            fileName = "AxePower_Setup.exe";
        }

        var destinationPath = Path.Combine(tempDir, fileName);
        var partialPath = destinationPath + ".part";

        // Auto-download and the Update Now button can arrive together. Only one transfer may
        // write the versioned installer; later callers reuse the verified completed file.
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsValidInstaller(destinationPath))
            {
                ReportDownloadProgress(progress, 100, "Update is ready to install.");
                _logService.LogInfo($"Using already downloaded update: {destinationPath}");
                return destinationPath;
            }

            IsDownloadInProgress = true;
            CurrentDownloadProgress = 0;
            ReportDownloadProgress(progress, 0, $"Connecting to download {fileName}...");
            _logService.LogInfo($"Downloading update from {downloadUrl} to {destinationPath}...");

            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerCts.CancelAfter(TimeSpan.FromMinutes(2));
            using var response = await _downloadHttpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                headerCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stallCts.CancelAfter(TimeSpan.FromMinutes(2));
            await using var sourceStream = await response.Content.ReadAsStreamAsync(stallCts.Token).ConfigureAwait(false);
            await using var destStream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[64 * 1024];
            var totalBytesRead = 0L;
            int bytesRead;

            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token).ConfigureAwait(false)) > 0)
            {
                await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), stallCts.Token).ConfigureAwait(false);
                totalBytesRead += bytesRead;
                stallCts.CancelAfter(TimeSpan.FromMinutes(2));

                if (totalBytes > 0)
                {
                    var percentage = Math.Clamp((double)totalBytesRead / totalBytes * 100.0, 0, 100);
                    ReportDownloadProgress(progress, percentage, $"Downloading update... {percentage:0}%");
                }
                else
                {
                    ReportDownloadProgress(
                        progress,
                        CurrentDownloadProgress,
                        $"Downloading update... {totalBytesRead / 1024d / 1024d:0.0} MB");
                }
            }

            await destStream.FlushAsync(stallCts.Token).ConfigureAwait(false);
            await destStream.DisposeAsync().ConfigureAwait(false);

            if (totalBytes > 0 && totalBytesRead != totalBytes)
            {
                throw new InvalidDataException($"The update download was incomplete ({totalBytesRead} of {totalBytes} bytes).");
            }

            if (!IsValidInstaller(partialPath, totalBytes > 0 ? totalBytes : null))
            {
                throw new InvalidDataException("The downloaded update is not a valid Windows installer.");
            }

            File.Move(partialPath, destinationPath, overwrite: true);
            ReportDownloadProgress(progress, 100, "Update downloaded. Launching installer...");
            _logService.LogInfo($"Update downloaded successfully: {destinationPath} ({totalBytesRead} bytes).");
            return destinationPath;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
            catch
            {
                // Keep the original download error.
            }

            _logService.LogError("Update download failed", ex);
            throw;
        }
        finally
        {
            IsDownloadInProgress = false;
            _downloadGate.Release();
        }
    }

    public bool ApplyUpdate(string installerPath)
    {
        try
        {
            if (!IsValidInstaller(installerPath))
            {
                _logService.LogError($"Refusing to launch an invalid or incomplete installer: {installerPath}");
                return false;
            }

            _logService.LogInfo($"Launching installer: {installerPath}");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
            };

            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to launch installer", ex);
            return false;
        }
    }

    private void ReportDownloadProgress(IProgress<double> progress, double percentage, string statusText)
    {
        CurrentDownloadProgress = percentage;
        progress.Report(percentage);
        DownloadProgressChanged?.Invoke(this, new UpdateDownloadProgressEventArgs(percentage, statusText));
    }

    private static bool IsValidInstaller(string path, long? expectedLength = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64 * 1024 || (expectedLength.HasValue && info.Length != expectedLength.Value))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    public static string FormatReleaseNotesForDisplay(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "Performance improvements and bug fixes.";
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>();

        foreach (var original in lines)
        {
            var line = original.Trim();
            if (line.Length == 0)
            {
                if (output.Count > 0 && output[^1].Length > 0)
                {
                    output.Add(string.Empty);
                }
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal) &&
                line.Contains("AxePower", StringComparison.OrdinalIgnoreCase))
            {
                continue; // The dialog already shows the release title above the notes.
            }

            line = Regex.Replace(line, @"^#{1,6}\s+", string.Empty);
            line = Regex.Replace(line, @"^[-*+]\s+", "• ");
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"__(.+?)__", "$1");
            line = Regex.Replace(line, @"`([^`]+)`", "$1");
            line = Regex.Replace(line, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");
            output.Add(line);
        }

        while (output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        return string.Join(Environment.NewLine, output);
    }

    private static (string? Url, string? Name, long Size) FindBestAsset(List<GitHubAsset>? assets)
    {
        if (assets == null || assets.Count == 0)
        {
            return (null, null, 0);
        }

        // 1. Look for .exe
        foreach (var asset in assets)
        {
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.BrowserDownloadUrl, asset.Name, asset.Size);
            }
        }

        // 2. Look for .msi
        foreach (var asset in assets)
        {
            if (asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.BrowserDownloadUrl, asset.Name, asset.Size);
            }
        }

        // 3. Look for .zip
        foreach (var asset in assets)
        {
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.BrowserDownloadUrl, asset.Name, asset.Size);
            }
        }

        // Fallback to first asset
        return (assets[0].BrowserDownloadUrl, assets[0].Name, assets[0].Size);
    }

    private static Version? ParseSemanticVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var clean = Regex.Replace(tag.Trim(), @"^[^\d]+", "");
        var match = Regex.Match(clean, @"^(\d+(\.\d+){1,3})");
        if (match.Success && Version.TryParse(match.Value, out var v))
        {
            return v;
        }

        return null;
    }

    public async Task<CommunityStatsResult> GetCommunityStatsAsync(CancellationToken cancellationToken = default)
    {
        int totalStars = 0;
        int totalDiscussions = 0;

        try
        {
            var repoEndpoints = new[]
            {
                "https://api.github.com/repos/mrAbhimanyuVishwakarma/AxePower",
                "https://api.github.com/repos/mrAbhimanyuVishwakarma/AxePower_Download"
            };

            foreach (var endpoint in repoEndpoints)
            {
                try
                {
                    using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                        if (doc.RootElement.TryGetProperty("stargazers_count", out var starsProp))
                        {
                            totalStars += starsProp.GetInt32();
                        }
                    }
                }
                catch { }
            }

            try
            {
                var searchEndpoint = "https://api.github.com/search/issues?q=repo:mrAbhimanyuVishwakarma/AxePower+repo:mrAbhimanyuVishwakarma/AxePower_Download+is:issue+is:open";
                using var response = await _httpClient.GetAsync(searchEndpoint, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("total_count", out var countProp))
                    {
                        totalDiscussions = countProp.GetInt32();
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"Community stats sync failed: {ex.Message}");
        }

        return new CommunityStatsResult
        {
            StarsCount = totalStars,
            DiscussionsCount = totalDiscussions
        };
    }
}

public class CommunityStatsResult
{
    public int StarsCount { get; set; }
    public int DiscussionsCount { get; set; }
}

public sealed class UpdateDownloadProgressEventArgs : EventArgs
{
    public UpdateDownloadProgressEventArgs(double percentage, string statusText)
    {
        Percentage = percentage;
        StatusText = statusText;
    }

    public double Percentage { get; }
    public string StatusText { get; }
}

public class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string LatestVersion { get; set; } = string.Empty;
    public string RawTag { get; set; } = string.Empty;
    public string ReleaseTitle { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public string? AssetName { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string ReleaseUrl { get; set; } = string.Empty;
}

public class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
