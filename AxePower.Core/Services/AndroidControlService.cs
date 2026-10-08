using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

public class AndroidControlOptions
{
    public bool StayAwake { get; set; } = true;
    public bool TurnScreenOff { get; set; } = false;
    public bool ForwardAudio { get; set; } = true;
    public int MaxFps { get; set; } = 60;
    public int MaxSize { get; set; } = 0; // 0 = native, 1080, 720
    public int BitRateMbps { get; set; } = 8;
    public string? SerialOrIp { get; set; }

    public string BuildArguments()
    {
        var sb = new StringBuilder();
        if (StayAwake) sb.Append("--stay-awake ");
        if (TurnScreenOff) sb.Append("--turn-screen-off ");
        if (!ForwardAudio) sb.Append("--no-audio ");
        if (MaxFps > 0) sb.Append($"--max-fps={MaxFps} ");
        if (MaxSize > 0) sb.Append($"--max-size={MaxSize} ");
        if (BitRateMbps > 0) sb.Append($"--video-bit-rate={BitRateMbps}M ");
        if (!string.IsNullOrWhiteSpace(SerialOrIp)) sb.Append($"-s \"{SerialOrIp.Trim()}\" ");
        return sb.ToString().Trim();
    }
}

public class AdbDeviceInfo
{
    public string Serial { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string State { get; set; } = "device";
    public bool IsWireless => Serial.Contains(":");
}

public class AndroidControlService
{
    private readonly LogService _logService;
    private readonly HttpClient _httpClient;
    private Process? _activeProcess;

    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxePower", "tools", "scrcpy");

    private static readonly string VersionFilePath = Path.Combine(ToolsDir, "installed_version.json");

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

    public async Task<AndroidControlLaunchResult> LaunchOrInstallAsync(AndroidControlOptions? options = null, CancellationToken ct = default)
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

            // 2. Check if scrcpy executable exists locally and is up to date
            string? scrcpyExe = await EnsureLatestScrcpyExecutableAsync(ct);
            if (string.IsNullOrEmpty(scrcpyExe))
            {
                _logService.LogError("scrcpy.exe could not be prepared.");
                return AndroidControlLaunchResult.Failed;
            }

            // 3. Launch found executable with custom options
            bool launched = StartScrcpyProcess(scrcpyExe, options);
            return launched ? AndroidControlLaunchResult.Launched : AndroidControlLaunchResult.Failed;
        }
        catch (Exception ex)
        {
            _logService.LogError("Exception in AndroidControlService.LaunchOrInstallAsync", ex);
            return AndroidControlLaunchResult.Failed;
        }
    }

    private bool StartScrcpyProcess(string exePath, AndroidControlOptions? options = null)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(exePath) ?? ToolsDir;
            var args = options?.BuildArguments() ?? "--stay-awake";

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            _logService.LogInfo($"Launching Android Control (scrcpy) with args: '{args}' from: {exePath}");
            _activeProcess = Process.Start(psi);
            return _activeProcess != null;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error starting scrcpy process", ex);
            return false;
        }
    }

    public async Task<List<AdbDeviceInfo>> GetConnectedDevicesAsync()
    {
        return await Task.Run(async () =>
        {
            var list = new List<AdbDeviceInfo>();
            string? adbExe = FindAdbExecutable();
            if (string.IsNullOrEmpty(adbExe)) return list;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var psi = new ProcessStartInfo
                {
                    FileName = adbExe,
                    Arguments = "devices -l",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var p = Process.Start(psi);
                if (p == null) return list;

                var outputTask = p.StandardOutput.ReadToEndAsync(cts.Token);
                var waitTask = p.WaitForExitAsync(cts.Token);

                await Task.WhenAll(outputTask, waitTask);
                string output = outputTask.Result;

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("List of devices") || string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string serial = parts[0];
                        string state = parts[1];
                        string model = "Android Device";

                        foreach (var part in parts)
                        {
                            if (part.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                            {
                                model = part.Substring(6).Replace('_', ' ');
                            }
                        }

                        list.Add(new AdbDeviceInfo
                        {
                            Serial = serial,
                            Model = model,
                            State = state
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"Error detecting ADB devices: {ex.Message}");
            }

            return list;
        });
    }

    public async Task<bool> InstallApkAsync(string apkPath, string? serial = null)
    {
        if (!File.Exists(apkPath)) return false;
        string? adbExe = FindAdbExecutable();
        if (string.IsNullOrEmpty(adbExe)) return false;

        try
        {
            string targetArg = !string.IsNullOrEmpty(serial) ? $"-s \"{serial}\" " : "";
            var psi = new ProcessStartInfo
            {
                FileName = adbExe,
                Arguments = $"{targetArg}install -r \"{apkPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            _logService.LogInfo($"Installing APK to device: {apkPath}");
            using var p = Process.Start(psi);
            if (p == null) return false;

            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            bool success = output.Contains("Success", StringComparison.OrdinalIgnoreCase);
            if (success) _logService.LogInfo($"APK install success: {apkPath}");
            else _logService.LogWarning($"APK install output: {output}");

            return success;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error installing APK", ex);
            return false;
        }
    }

    public async Task<bool> ConnectWirelessAdbAsync(string ipAndPort)
    {
        if (string.IsNullOrWhiteSpace(ipAndPort)) return false;
        string? adbExe = FindAdbExecutable();
        if (string.IsNullOrEmpty(adbExe)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbExe,
                Arguments = $"connect {ipAndPort.Trim()}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var p = Process.Start(psi);
            if (p == null) return false;

            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            return output.Contains("connected", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string? FindAdbExecutable()
    {
        if (Directory.Exists(ToolsDir))
        {
            var direct = Path.Combine(ToolsDir, "adb.exe");
            if (File.Exists(direct)) return direct;

            var matching = Directory.GetFiles(ToolsDir, "adb.exe", SearchOption.AllDirectories);
            if (matching.Length > 0) return matching[0];
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* A malformed PATH entry is skipped. */ }
        }

        return null;
    }

    private async Task<string?> EnsureLatestScrcpyExecutableAsync(CancellationToken ct)
    {
        string? existingExe = FindScrcpyExecutable();
        string installedTag = GetInstalledVersionTag();

        try
        {
            var endpoint = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
            var release = await _httpClient.GetFromJsonAsync<ScrcpyReleaseInfo>(endpoint, ct);

            if (release != null && !string.IsNullOrWhiteSpace(release.TagName))
            {
                string latestTag = release.TagName;

                if (string.IsNullOrEmpty(existingExe) || !string.Equals(installedTag, latestTag, StringComparison.OrdinalIgnoreCase))
                {
                    _logService.LogInfo($"[AndroidControl] New scrcpy release found: '{latestTag}' (installed: '{installedTag}'). Updating...");
                    bool downloaded = await DownloadAndExtractScrcpyReleaseAsync(release, ct);
                    if (downloaded)
                    {
                        SaveInstalledVersionTag(latestTag);
                        return FindScrcpyExecutable();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[AndroidControl] Auto-update check exception: {ex.Message}");
        }

        if (!string.IsNullOrEmpty(existingExe))
        {
            return existingExe;
        }

        _logService.LogInfo("Scrcpy not found locally. Downloading latest release from GitHub (Genymobile/scrcpy)...");
        bool initialDownloaded = await DownloadAndExtractLatestScrcpyAsync(ct);
        return initialDownloaded ? FindScrcpyExecutable() : null;
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

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "scrcpy.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* A malformed PATH entry is skipped. */ }
        }

        return null;
    }

    private static string GetInstalledVersionTag()
    {
        try
        {
            if (File.Exists(VersionFilePath))
            {
                var json = File.ReadAllText(VersionFilePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tag", out var tagElem))
                {
                    return tagElem.GetString() ?? string.Empty;
                }
            }
        }
        catch { /* Unreadable version file: treated as unknown, so the tools are checked for an update. */ }
        return string.Empty;
    }

    private static void SaveInstalledVersionTag(string tag)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            var content = JsonSerializer.Serialize(new { tag = tag, updated_at = DateTime.UtcNow.ToString("O") });
            File.WriteAllText(VersionFilePath, content);
        }
        catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: without the file the next start checks for an update again. */ }
    }

    private async Task<bool> DownloadAndExtractLatestScrcpyAsync(CancellationToken ct)
    {
        try
        {
            var endpoint = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
            var release = await _httpClient.GetFromJsonAsync<ScrcpyReleaseInfo>(endpoint, ct);
            if (release == null || release.Assets == null || release.Assets.Count == 0)
            {
                _logService.LogError("Could not parse scrcpy release assets from GitHub API.");
                return false;
            }

            bool success = await DownloadAndExtractScrcpyReleaseAsync(release, ct);
            if (success && !string.IsNullOrEmpty(release.TagName))
            {
                SaveInstalledVersionTag(release.TagName);
            }
            return success;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error downloading scrcpy", ex);
            return false;
        }
    }

    private async Task<bool> DownloadAndExtractScrcpyReleaseAsync(ScrcpyReleaseInfo release, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);

            ScrcpyReleaseAsset? winAsset = null;
            if (release.Assets != null)
            {
                foreach (var asset in release.Assets)
                {
                    if (asset.Name != null && asset.Name.Contains("win64", StringComparison.OrdinalIgnoreCase) && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        winAsset = asset;
                        break;
                    }
                }
            }

            if (winAsset == null || string.IsNullOrEmpty(winAsset.BrowserDownloadUrl))
            {
                _logService.LogError("Could not find scrcpy-win64 .zip asset in release.");
                return false;
            }

            var tempZip = Path.Combine(Path.GetTempPath(), $"scrcpy_{Guid.NewGuid():N}.zip");
            _logService.LogInfo($"Downloading scrcpy package from: {winAsset.BrowserDownloadUrl}");

            using (var resp = await _httpClient.GetAsync(winAsset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct);
            }

            _logService.LogInfo("Extracting scrcpy archive into tools directory...");
            ZipFile.ExtractToDirectory(tempZip, ToolsDir, overwriteFiles: true);

            try { File.Delete(tempZip); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: a leftover download in the temp folder is harmless. */ }

            _logService.LogInfo("Scrcpy downloaded and extracted successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to download and extract scrcpy", ex);
            return false;
        }
    }

    private class ScrcpyReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<ScrcpyReleaseAsset>? Assets { get; set; }
    }

    private class ScrcpyReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
