using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class GameScannerService
{
    private readonly LogService _logService;

    public GameScannerService(LogService logService)
    {
        _logService = logService;
    }

    public async Task<List<string>> ScanForGamesAsync()
    {
        return await Task.Run(() =>
        {
            var foundExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var gameDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Discover Steam libraries dynamically
                DiscoverSteamLibraries(gameDirectories);

                // 2. Discover Epic Games & GOG directories
                DiscoverEpicAndGogLibraries(gameDirectories);

                // 3. Scan all system drives for standard game folders
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                    string root = drive.RootDirectory.FullName;
                    AddIfDirectoryExists(gameDirectories, Path.Combine(root, "SteamLibrary", "steamapps", "common"));
                    AddIfDirectoryExists(gameDirectories, Path.Combine(root, "Epic Games"));
                    AddIfDirectoryExists(gameDirectories, Path.Combine(root, "GOG Games"));
                    AddIfDirectoryExists(gameDirectories, Path.Combine(root, "XboxGames"));
                    AddIfDirectoryExists(gameDirectories, Path.Combine(root, "Games"));
                }

                // Scan all discovered game folders for executables
                foreach (var gameDir in gameDirectories)
                {
                    try
                    {
                        var exeFiles = Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.AllDirectories)
                            .Where(f => !f.Contains("CrashReporter", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Contains("CrashReportClient", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Contains("DirectX", StringComparison.OrdinalIgnoreCase));

                        foreach (var file in exeFiles)
                        {
                            foundExecutables.Add(Path.GetFileName(file));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"Could not scan folder '{gameDir}': {ex.Message}");
                    }
                }

                // 4. Scan Desktop and Start Menu for shortcuts (.lnk)
                string[] shortcutDirs = {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                };

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    if (shell != null)
                    {
                        foreach (var dir in shortcutDirs)
                        {
                            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                            try
                            {
                                var lnkFiles = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories);
                                foreach (var lnk in lnkFiles)
                                {
                                    try
                                    {
                                        dynamic shortcut = shell.CreateShortcut(lnk);
                                        string targetPath = shortcut.TargetPath;
                                        if (!string.IsNullOrEmpty(targetPath) && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                        {
                                            string exeName = Path.GetFileName(targetPath);
                                            if (!exeName.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                                !exeName.Contains("setup", StringComparison.OrdinalIgnoreCase))
                                            {
                                                foundExecutables.Add(exeName);
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("Error scanning for games", ex);
            }

            return foundExecutables.OrderBy(x => x).ToList();
        });
    }

    private static void DiscoverSteamLibraries(HashSet<string> dirs)
    {
        try
        {
            string? steamPath = null;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                steamPath = key?.GetValue("SteamPath") as string;
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                steamPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            }

            if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
            {
                string mainCommon = Path.Combine(steamPath, "steamapps", "common");
                AddIfDirectoryExists(dirs, mainCommon);

                // Parse libraryfolders.vdf to find secondary libraries on D:, E:, etc.
                string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    foreach (var line in File.ReadAllLines(vdfPath))
                    {
                        if (line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                string libPath = parts.Last().Replace(@"\\", @"\");
                                AddIfDirectoryExists(dirs, Path.Combine(libPath, "steamapps", "common"));
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static void DiscoverEpicAndGogLibraries(HashSet<string> dirs)
    {
        try
        {
            string epic1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games");
            AddIfDirectoryExists(dirs, epic1);

            string epic2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games");
            AddIfDirectoryExists(dirs, epic2);

            string gog1 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GOG Galaxy", "Games");
            AddIfDirectoryExists(dirs, gog1);

            string gog2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy", "Games");
            AddIfDirectoryExists(dirs, gog2);
        }
        catch { }
    }

    private static void AddIfDirectoryExists(HashSet<string> set, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            set.Add(path);
        }
    }

    public async Task<List<InstalledGameInfo>> GetInstalledGamesAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<InstalledGameInfo>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Scan Steam AppManifests
                var steamAppsDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DiscoverSteamAppsDirs(steamAppsDirs);

                foreach (var steamApps in steamAppsDirs)
                {
                    if (!Directory.Exists(steamApps)) continue;

                    try
                    {
                        var acfFiles = Directory.GetFiles(steamApps, "appmanifest_*.acf");
                        foreach (var acf in acfFiles)
                        {
                            try
                            {
                                string content = File.ReadAllText(acf);
                                string? appId = ExtractAcfValue(content, "appid");
                                string? name = ExtractAcfValue(content, "name");
                                string? installDir = ExtractAcfValue(content, "installdir");

                                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)) continue;

                                // Filter out non-game software, tools, runtimes, and servers
                                if (IsSoftwareOrUtility(name))
                                {
                                    continue;
                                }

                                if (seenNames.Contains(name)) continue;

                                string commonDir = Path.Combine(steamApps, "common", installDir ?? "");
                                string exePath = "";
                                string exeName = "";

                                if (Directory.Exists(commonDir))
                                {
                                    var exes = Directory.EnumerateFiles(commonDir, "*.exe", SearchOption.AllDirectories)
                                        .Where(f => !f.Contains("CrashReport", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) &&
                                                    !f.Contains("DirectX", StringComparison.OrdinalIgnoreCase))
                                        .ToList();

                                    string cleanInstallDir = (installDir ?? name).Replace(" ", "").Replace("_", "");
                                    var match = exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Replace(" ", "").Replace("_", "").Equals(cleanInstallDir, StringComparison.OrdinalIgnoreCase))
                                                ?? exes.FirstOrDefault(e => !Path.GetFileName(e).StartsWith("unins", StringComparison.OrdinalIgnoreCase) &&
                                                                            !Path.GetFileName(e).StartsWith("setup", StringComparison.OrdinalIgnoreCase) &&
                                                                            !Path.GetFileName(e).Contains("launcher", StringComparison.OrdinalIgnoreCase))
                                                ?? exes.FirstOrDefault();

                                    if (match != null)
                                    {
                                        exePath = match;
                                        exeName = Path.GetFileName(match);
                                    }
                                }

                                if (string.IsNullOrEmpty(exeName))
                                {
                                    exeName = (installDir ?? name) + ".exe";
                                }

                                seenNames.Add(name);
                                results.Add(new InstalledGameInfo
                                {
                                    Name = name,
                                    ExecutableName = exeName,
                                    ExecutablePath = exePath,
                                    SteamAppId = appId,
                                    BannerUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg"
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // 2. Discover Epic Games manifests
                string epicManifests = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");

                if (Directory.Exists(epicManifests))
                {
                    foreach (var itemFile in Directory.GetFiles(epicManifests, "*.item"))
                    {
                        try
                        {
                            string json = File.ReadAllText(itemFile);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("DisplayName", out var nameProp) &&
                                root.TryGetProperty("InstallLocation", out var locProp) &&
                                root.TryGetProperty("LaunchExecutable", out var exeProp))
                            {
                                string displayName = nameProp.GetString() ?? "";
                                string installLoc = locProp.GetString() ?? "";
                                string launchExe = exeProp.GetString() ?? "";

                                if (!string.IsNullOrWhiteSpace(displayName) && !seenNames.Contains(displayName) && Directory.Exists(installLoc))
                                {
                                    string fullExe = Path.Combine(installLoc, launchExe);
                                    seenNames.Add(displayName);
                                    results.Add(new InstalledGameInfo
                                    {
                                        Name = displayName,
                                        ExecutableName = Path.GetFileName(launchExe),
                                        ExecutablePath = fullExe
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService?.LogError("Error discovering installed games", ex);
            }

            return results;
        });
    }

    private static string? ExtractAcfValue(string acfContent, string key)
    {
        var match = Regex.Match(acfContent, $@"""{key}""\s+""([^""]+)""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void DiscoverSteamAppsDirs(HashSet<string> steamAppsDirs)
    {
        try
        {
            string? steamPath = null;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                steamPath = key?.GetValue("SteamPath") as string;
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                steamPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            }

            if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
            {
                steamAppsDirs.Add(Path.Combine(steamPath, "steamapps"));

                string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    foreach (var line in File.ReadAllLines(vdfPath))
                    {
                        if (line.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                string libPath = parts.Last().Replace(@"\\", @"\");
                                if (Directory.Exists(libPath))
                                {
                                    steamAppsDirs.Add(Path.Combine(libPath, "steamapps"));
                                }
                            }
                        }
                    }
                }
            }

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                string p = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps");
                if (Directory.Exists(p)) steamAppsDirs.Add(p);
            }
        }
        catch { }
    }

    public static bool IsSoftwareOrUtility(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        string[] softwareKeywords = {
            "Lossless Scaling",
            "Wallpaper Engine",
            "Soundpad",
            "OBS Studio",
            "Blender",
            "3DMark",
            "VRMark",
            "PCMark",
            "Geekbench",
            "AIDA64",
            "Controller Companion",
            "reWASD",
            "DS4Windows",
            "ShareX",
            "Borderless Gaming",
            "VoiceAttack",
            "OVR Advanced Settings",
            "SteamVR",
            "VTube Studio",
            "Playnite",
            "RetroArch",
            "MSI Afterburner",
            "RivaTuner",
            "Steamworks",
            "Redistributable",
            "Proton",
            "DirectX",
            "Dedicated Server",
            "Benchmark",
            "Soundtrack",
            "SDK",
            "Tool"
        };

        foreach (var keyword in softwareKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

