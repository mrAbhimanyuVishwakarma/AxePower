using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Update;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services;

public class UpdateService
{
    private readonly LogService _logService;
    private readonly UpdateSecurityOptions _securityOptions;
    private readonly IAuthenticodeVerifier _authenticodeVerifier;
    private readonly IManifestVerifier _manifestVerifier;
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

    /// <summary>Single fallback when no assembly version is available; the assembly version is the source of truth.</summary>
    public const string FallbackVersion = "2.1.0";

    public UpdateService(
        LogService logService,
        UpdateSecurityOptions? securityOptions = null,
        IAuthenticodeVerifier? authenticodeVerifier = null,
        IManifestVerifier? manifestVerifier = null,
        HttpClient? httpClient = null,
        HttpClient? downloadHttpClient = null)
    {
        _logService = logService;
        _securityOptions = securityOptions ?? new UpdateSecurityOptions();
        _authenticodeVerifier = authenticodeVerifier ?? new WindowsAuthenticodeVerifier(_securityOptions.RequireTrustedRoot);
        _manifestVerifier = manifestVerifier ?? new EcdsaManifestVerifier(_securityOptions.PinnedPublicKeyPem);

        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        if (httpClient == null)
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AxePower-App/" + GetCurrentVersionString());
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        }

        // We use manual redirect handling on the download client to enforce strict allow-list validation.
        if (downloadHttpClient != null)
        {
            _downloadHttpClient = downloadHttpClient;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            _downloadHttpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _downloadHttpClient.DefaultRequestHeaders.Add("User-Agent", "AxePower-App/" + GetCurrentVersionString());
            _downloadHttpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");
        }
    }

    public string GetCurrentVersionString()
    {
        var asm = ResolveProductAssembly();
        return asm.GetName().Version?.ToString(3) ?? FallbackVersion;
    }

    public Version GetCurrentVersion()
    {
        var asm = ResolveProductAssembly();
        return asm.GetName().Version ?? Version.Parse(FallbackVersion);
    }

    private static Assembly ResolveProductAssembly()
    {
        var asm = Assembly.GetEntryAssembly();
        if (asm == null || asm.GetName().Name?.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) == true)
        {
            return typeof(UpdateService).Assembly;
        }

        return asm;
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

                // Look for update-manifest.json or release-manifest.json asset
                UpdateManifest? manifest = null;
                string? manifestUrl = null;
                var manifestAsset = release.Assets.FirstOrDefault(a =>
                    a.Name.Equals("update-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase));

                if (manifestAsset != null && !string.IsNullOrWhiteSpace(manifestAsset.BrowserDownloadUrl))
                {
                    manifestUrl = manifestAsset.BrowserDownloadUrl;
                    try
                    {
                        manifest = await DownloadManifestAsync(manifestUrl, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"Failed to download or parse update manifest from {manifestUrl}: {ex.Message}");
                    }
                }

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
                    ReleaseUrl = release.HtmlUrl ?? "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases",
                    Manifest = manifest,
                    ManifestUrl = manifestUrl
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

    public async Task<UpdateManifest> DownloadManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        ValidateUpdateUri(new Uri(manifestUrl));
        using var resp = await _httpClient.GetAsync(manifestUrl, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new UpdateVerificationException("Failed to deserialize release manifest JSON.");

        // Null fields become their empty defaults, which the signature check then refuses.
        var repair = JsonNullRepair.Repair(manifest);
        if (!repair.IsComplete)
            throw new UpdateVerificationException($"The release manifest is incomplete ({repair.Describe()}).");
        return manifest;
    }

    public async Task<string> DownloadUpdateAsync(
        string downloadUrl,
        IProgress<double> progress,
        CancellationToken cancellationToken = default,
        UpdateManifest? manifest = null)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new ArgumentException("Download URL cannot be null or empty.", nameof(downloadUrl));
        }

        var downloadUri = new Uri(downloadUrl);
        ValidateUpdateUri(downloadUri);

        // If manifest was not pre-fetched, fail closed if none is available
        if (manifest == null)
        {
            throw new UpdateVerificationException(
                "Update rejected: No cryptographically signed release manifest was provided. " +
                "AxePower requires a verified manifest before downloading or applying updates.");
        }

        // 1. Verify manifest signature using pinned public key
        if (!_manifestVerifier.VerifyManifest(manifest, manifest.Signature ?? string.Empty, out var manifestError))
        {
            throw new UpdateVerificationException(
                $"Update manifest signature verification failed: {manifestError}");
        }

        // 2. Reject downgrade / replay attempts
        if (_securityOptions.EnforceDowngradeProtection)
        {
            if (!Version.TryParse(manifest.Version, out var newVersion))
            {
                throw new UpdateVerificationException($"Invalid manifest version '{manifest.Version}'.");
            }

            var currentVersion = GetCurrentVersion();
            if (newVersion <= currentVersion)
            {
                throw new UpdateVerificationException(
                    $"Update rejected: Proposed version {newVersion} is not newer than current version {currentVersion}. " +
                    "Downgrade and replay attacks are prohibited.");
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_Update");
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        var assetName = !string.IsNullOrWhiteSpace(manifest.AssetName)
            ? manifest.AssetName
            : Path.GetFileName(downloadUri.LocalPath);

        if (string.IsNullOrWhiteSpace(assetName) || !assetName.Contains('.'))
        {
            assetName = "AxePower_Setup.exe";
        }

        var destinationPath = Path.Combine(tempDir, assetName);
        var tempStagingPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{assetName}.tmp");

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // If the destination file already exists and passes full Authenticode & hash checks, reuse it.
            if (File.Exists(destinationPath) && VerifyCompletedFile(destinationPath, manifest))
            {
                ReportDownloadProgress(progress, 100, "Update is ready to install.");
                _logService.LogInfo($"Reusing existing verified update: {destinationPath}");
                return destinationPath;
            }

            IsDownloadInProgress = true;
            CurrentDownloadProgress = 0;
            ReportDownloadProgress(progress, 0, $"Connecting to download {assetName}...");
            _logService.LogInfo($"Downloading update from {downloadUrl} to temporary staging file...");

            // Stream download with safe redirect checking and incremental SHA-256 calculation
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytesRead = 0;

            await using (var destStream = new FileStream(
                tempStagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var response = await SendRequestWithSafeRedirectsAsync(downloadUri, cancellationToken);
                response.EnsureSuccessStatusCode();

                var reportedContentLength = response.Content.Headers.ContentLength;
                if (reportedContentLength.HasValue && manifest.ByteLength > 0 && reportedContentLength.Value != manifest.ByteLength)
                {
                    throw new UpdateVerificationException(
                        $"HTTP Content-Length ({reportedContentLength.Value}) does not match manifest byte length ({manifest.ByteLength}).");
                }

                await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[64 * 1024];
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    sha256.AppendData(buffer.AsSpan(0, bytesRead));
                    totalBytesRead += bytesRead;

                    if (manifest.ByteLength > 0)
                    {
                        var pct = Math.Clamp((double)totalBytesRead / manifest.ByteLength * 100.0, 0, 100);
                        ReportDownloadProgress(progress, pct, $"Downloading update... {pct:0}%");
                    }
                    else
                    {
                        ReportDownloadProgress(progress, CurrentDownloadProgress, $"Downloading update... {totalBytesRead / 1024d / 1024d:0.0} MB");
                    }
                }

                await destStream.FlushAsync(cancellationToken);
            }

            // 3. Verify total length
            if (manifest.ByteLength > 0 && totalBytesRead != manifest.ByteLength)
            {
                throw new UpdateVerificationException(
                    $"Update payload truncated: expected {manifest.ByteLength} bytes but received {totalBytesRead} bytes.");
            }

            // 4. Verify SHA-256 hash against manifest
            var actualHash = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            var expectedHash = manifest.Sha256.Trim().ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateVerificationException(
                    $"SHA-256 hash mismatch! The downloaded update does not match the signed manifest.\nExpected: {expectedHash}\nActual:   {actualHash}");
            }

            // 5. Verify Authenticode signature and publisher identity
            if (_securityOptions.RequireAuthenticode)
            {
                var authResult = _authenticodeVerifier.Verify(tempStagingPath, _securityOptions.ExpectedPublisher);
                if (!authResult.IsValid)
                {
                    throw new UpdateVerificationException(
                        $"Authenticode signature verification failed for downloaded installer: {authResult.FailureReason}");
                }
            }

            // 6. Atomic promotion: move from temp staging to final destination
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(tempStagingPath, destinationPath);

            ReportDownloadProgress(progress, 100, "Update downloaded and cryptographically verified. Ready to install.");
            _logService.LogInfo($"Update downloaded and verified successfully: {destinationPath}");
            return destinationPath;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempStagingPath))
                {
                    File.Delete(tempStagingPath);
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { /* Best-effort: a partial download in the temp folder is harmless and never run. */ }

            _logService.LogError("Update download or verification failed", ex);
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
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                _logService.LogError($"Refusing to launch update: file does not exist '{installerPath}'");
                return false;
            }

            // Before execution, re-verify Authenticode signature and publisher identity
            if (_securityOptions.RequireAuthenticode)
            {
                var authResult = _authenticodeVerifier.Verify(installerPath, _securityOptions.ExpectedPublisher);
                if (!authResult.IsValid)
                {
                    _logService.LogError($"Refusing to execute installer: Authenticode verification failed: {authResult.FailureReason}");
                    return false;
                }
            }

            _logService.LogInfo($"Launching verified installer: {installerPath}");
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

    private bool VerifyCompletedFile(string path, UpdateManifest manifest)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (manifest.ByteLength > 0 && info.Length != manifest.ByteLength))
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            if (!string.Equals(hash, manifest.Sha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_securityOptions.RequireAuthenticode)
            {
                var auth = _authenticodeVerifier.Verify(path, _securityOptions.ExpectedPublisher);
                if (!auth.IsValid) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Fails closed: an update that cannot be verified is not installed. Say why.
            _logService.LogWarning("[Update] The downloaded update could not be verified and will not be installed", ex);
            return false;
        }
    }

    public void ValidateUpdateUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            if (!(_securityOptions.AllowHttpForTesting && uri.IsLoopback))
            {
                throw new UpdateVerificationException(
                    $"Update download requires HTTPS. Insecure scheme '{uri.Scheme}' is rejected.");
            }
        }

        if (!_securityOptions.AllowedHosts.Contains(uri.Host))
        {
            // Allow loopback if specifically enabled for tests
            if (!(_securityOptions.AllowHttpForTesting && uri.IsLoopback))
            {
                throw new UpdateVerificationException(
                    $"Host '{uri.Host}' is not in the allow-list of trusted release hosts.");
            }
        }

        // If host is github.com or api.github.com, verify repository path
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.TrimStart('/');
            if (path.StartsWith("repos/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(6);
            }

            bool matchRepo = _securityOptions.AllowedRepositories.Any(r =>
                path.StartsWith(r.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

            if (!matchRepo)
            {
                throw new UpdateVerificationException(
                    $"Update URL path '{uri.AbsolutePath}' does not belong to an allow-listed AxePower repository.");
            }
        }
    }

    private async Task<HttpResponseMessage> SendRequestWithSafeRedirectsAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        const int maxRedirects = 5;
        int redirectCount = 0;

        while (redirectCount < maxRedirects)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or
                HttpStatusCode.Found or
                HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or
                (HttpStatusCode)308)
            {
                redirectCount++;
                var location = response.Headers.Location
                    ?? throw new UpdateVerificationException("Redirect received from server but Location header is missing.");

                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                ValidateUpdateUri(nextUri);

                response.Dispose();
                currentUri = nextUri;
                continue;
            }

            return response;
        }

        throw new UpdateVerificationException($"Too many redirects ({redirectCount}) while downloading update.");
    }

    private void ReportDownloadProgress(IProgress<double> progress, double percentage, string statusText)
    {
        CurrentDownloadProgress = percentage;
        progress.Report(percentage);
        DownloadProgressChanged?.Invoke(this, new UpdateDownloadProgressEventArgs(percentage, statusText));
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
                continue;
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

        foreach (var asset in assets)
        {
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.BrowserDownloadUrl, asset.Name, asset.Size);
            }
        }

        foreach (var asset in assets)
        {
            if (asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.BrowserDownloadUrl, asset.Name, asset.Size);
            }
        }

        foreach (var asset in assets)
        {
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return (asset.BrowserDownloadUrl, asset.Name, asset.Size);
            }
        }

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
                        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                        if (doc.RootElement.TryGetProperty("stargazers_count", out var starsProp))
                        {
                            totalStars += starsProp.GetInt32();
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or FormatException)
                {
                    // Best-effort: one repository's star count is decoration on the About page.
                }
            }

            try
            {
                var searchEndpoint = "https://api.github.com/search/issues?q=repo:mrAbhimanyuVishwakarma/AxePower+repo:mrAbhimanyuVishwakarma/AxePower_Download+is:issue+is:open";
                using var response = await _httpClient.GetAsync(searchEndpoint, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    if (doc.RootElement.TryGetProperty("total_count", out var countProp))
                    {
                        totalDiscussions = countProp.GetInt32();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or FormatException)
            {
                // Best-effort: the discussion count is decoration on the About page.
            }
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
    public UpdateManifest? Manifest { get; set; }
    public string? ManifestUrl { get; set; }
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
