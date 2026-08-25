using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

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
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            set.Add(path);
        }
    }
}
