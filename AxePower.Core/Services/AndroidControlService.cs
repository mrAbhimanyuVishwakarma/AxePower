using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services;

public enum AndroidControlLaunchResult
{
    Launched,
    AlreadyRunning,
    DownloadedAndLaunched,
    Failed
}

public class AndroidControlService
{
    private readonly LogService _logService;
    private readonly HttpClient _httpClient;
    private Process? _activeProcess;

    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxePower", "tools", "scrcpy");

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public AndroidControlService(LogService logService)
    {
        _logService = logService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AxePower-AndroidControl");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    public async Task<AndroidControlLaunchResult> LaunchOrInstallAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Check if an active scrcpy window is already open
            var existingProcesses = Process.GetProcessesByName("scrcpy");
            if (existingProcesses.Length > 0)
            {
                foreach (var p in existingProcesses)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(p.MainWindowHandle);
                        return AndroidControlLaunchResult.AlreadyRunning;
                    }
                }
            }

            // 2. Check if scrcpy executable exists locally
            string? scrcpyExe = FindScrcpyExecutable();
            if (string.IsNullOrEmpty(scrcpyExe))
            {
                _logService.LogInfo("Scrcpy not found locally. Downloading latest release from GitHub (Genymobile/scrcpy)...");
                bool downloaded = await DownloadAndExtractLatestScrcpyAsync(ct);
                if (!downloaded)
                {
                    _logService.LogError("Failed to download scrcpy package from GitHub.");
                    return AndroidControlLaunchResult.Failed;
                }

                scrcpyExe = FindScrcpyExecutable();
                if (string.IsNullOrEmpty(scrcpyExe))
                {
                    _logService.LogError("scrcpy.exe not found after extraction.");
                    return AndroidControlLaunchResult.Failed;
                }

                bool launchedDownloaded = StartScrcpyProcess(scrcpyExe);
                return launchedDownloaded ? AndroidControlLaunchResult.DownloadedAndLaunched : AndroidControlLaunchResult.Failed;
            }

            // 3. Launch found executable
            bool launched = StartScrcpyProcess(scrcpyExe);
            return launched ? AndroidControlLaunchResult.Launched : AndroidControlLaunchResult.Failed;
        }
        catch (Exception ex)
        {
            _logService.LogError("Exception in AndroidControlService.LaunchOrInstallAsync", ex);
            return AndroidControlLaunchResult.Failed;
        }
    }

    private bool StartScrcpyProcess(string exePath)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(exePath) ?? ToolsDir;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--stay-awake",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            _logService.LogInfo($"Launching Android Control (scrcpy) from: {exePath}");
            _activeProcess = Process.Start(psi);
            return _activeProcess != null;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error starting scrcpy process", ex);
            return false;
        }
    }

    private string? FindScrcpyExecutable()
    {
        if (Directory.Exists(ToolsDir))
        {
            var direct = Path.Combine(ToolsDir, "scrcpy.exe");
            if (File.Exists(direct)) return direct;

            var matching = Directory.GetFiles(ToolsDir, "scrcpy.exe", SearchOption.AllDirectories);
            if (matching.Length > 0) return matching[0];
        }

        // Also check if scrcpy is installed on PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "scrcpy.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    private async Task<bool> DownloadAndExtractLatestScrcpyAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);

            // Fetch latest release asset
            var endpoint = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
            var release = await _httpClient.GetFromJsonAsync<ScrcpyReleaseInfo>(endpoint, ct);
            if (release == null || release.Assets == null || release.Assets.Count == 0)
            {
                _logService.LogError("Could not parse scrcpy release assets from GitHub API.");
                return false;
            }

            // Find win64 zip asset
            ScrcpyReleaseAsset? winAsset = null;
            foreach (var asset in release.Assets)
            {
                if (asset.Name != null && asset.Name.Contains("win64", StringComparison.OrdinalIgnoreCase) && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    winAsset = asset;
                    break;
                }
            }

            if (winAsset == null || string.IsNullOrEmpty(winAsset.BrowserDownloadUrl))
            {
                _logService.LogError("Could not find scrcpy-win64 .zip asset in latest release.");
                return false;
            }

            var tempZip = Path.Combine(Path.GetTempPath(), $"scrcpy_{Guid.NewGuid():N}.zip");
            _logService.LogInfo($"Downloading {winAsset.Name} from {winAsset.BrowserDownloadUrl}...");

            using (var response = await _httpClient.GetAsync(winAsset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(fs, ct);
            }

            _logService.LogInfo($"Extracting scrcpy to {ToolsDir}...");
            ZipFile.ExtractToDirectory(tempZip, ToolsDir, overwriteFiles: true);

            try { File.Delete(tempZip); } catch { }

            // If it extracted into a subdirectory (e.g. ToolsDir/scrcpy-win64-v3.1/...), move all files up to ToolsDir
            var subDirs = Directory.GetDirectories(ToolsDir);
            if (subDirs.Length == 1 && !File.Exists(Path.Combine(ToolsDir, "scrcpy.exe")))
            {
                var subDir = subDirs[0];
                foreach (var file in Directory.GetFiles(subDir))
                {
                    var dest = Path.Combine(ToolsDir, Path.GetFileName(file));
                    File.Move(file, dest, overwrite: true);
                }
                foreach (var dir in Directory.GetDirectories(subDir))
                {
                    var dest = Path.Combine(ToolsDir, Path.GetFileName(dir));
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    Directory.Move(dir, dest);
                }
                try { Directory.Delete(subDir, true); } catch { }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error in DownloadAndExtractLatestScrcpyAsync", ex);
            return false;
        }
    }

    private class ScrcpyReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public System.Collections.Generic.List<ScrcpyReleaseAsset>? Assets { get; set; }
    }

    private class ScrcpyReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
