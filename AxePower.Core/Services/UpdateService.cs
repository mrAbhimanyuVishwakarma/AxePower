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
    }

    public string GetCurrentVersionString()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version?.ToString(3) ?? "1.3.0";
    }

    public Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? new Version(1, 2, 0);
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
                    ReleaseNotes = release.Body ?? "Performance improvements and bug fixes.",
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

        _logService.LogInfo($"Downloading update from {downloadUrl} to {destinationPath}...");

        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        var totalBytesRead = 0L;
        int bytesRead;

        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await destStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalBytesRead += bytesRead;

            if (totalBytes > 0)
            {
                var percentage = (double)totalBytesRead / totalBytes * 100.0;
                progress.Report(percentage);
            }
        }

        progress.Report(100.0);
        _logService.LogInfo($"Update downloaded successfully: {destinationPath}");
        return destinationPath;
    }

    public bool ApplyUpdate(string installerPath)
    {
        try
        {
            _logService.LogInfo($"Launching installer: {installerPath}");
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to launch installer", ex);
            return false;
        }
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
