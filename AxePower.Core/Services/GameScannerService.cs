using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
                // Scan Steam directory
                string steamPath = @"C:\Program Files (x86)\Steam\steamapps\common";
                if (Directory.Exists(steamPath))
                {
                    var exeFiles = Directory.EnumerateFiles(steamPath, "*.exe", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("CrashReporter", StringComparison.OrdinalIgnoreCase) && 
                                    !f.Contains("redist", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("unins", StringComparison.OrdinalIgnoreCase));

                    foreach (var file in exeFiles)
                    {
                        foundExecutables.Add(Path.GetFileName(file));
                    }
                }
                
                // Scan Epic Games directory
                string epicPath = @"C:\Program Files\Epic Games";
                if (Directory.Exists(epicPath))
                {
                    var exeFiles = Directory.EnumerateFiles(epicPath, "*.exe", SearchOption.AllDirectories)
                        .Where(f => !f.Contains("CrashReportClient", StringComparison.OrdinalIgnoreCase) &&
                                    !f.Contains("unins", StringComparison.OrdinalIgnoreCase));

                    foreach (var file in exeFiles)
                    {
                        foundExecutables.Add(Path.GetFileName(file));
                    }
                }

                // Scan Desktop and Start Menu for shortcuts
                string[] shortcutDirs = {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                };

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);

                foreach (var dir in shortcutDirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                    var lnkFiles = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories);
                    foreach (var lnk in lnkFiles)
                    {
                        try
                        {
                            dynamic shortcut = shell.CreateShortcut(lnk);
                            string targetPath = shortcut.TargetPath;
                            
                            // Check if it's a known game store shortcut or looks like a game
                            if (!string.IsNullOrEmpty(targetPath) && targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                string exeName = Path.GetFileName(targetPath);
                                // Filter out common non-game shortcuts
                                if (!exeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                                    !exeName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
                                    !lnk.Contains("Uninstall", StringComparison.OrdinalIgnoreCase))
                                {
                                    foundExecutables.Add(exeName);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogError("Error scanning for games.", ex);
            }

            return foundExecutables.OrderBy(x => x).ToList();
        });
    }
}
