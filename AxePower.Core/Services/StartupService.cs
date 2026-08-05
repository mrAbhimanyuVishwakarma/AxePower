using System;
using System.IO;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class StartupService
{
    private readonly LogService _logService;
    private readonly SettingsService _settingsService;
    private readonly string _shortcutPath;

    public StartupService(LogService logService, SettingsService settingsService)
    {
        _logService = logService;
        _settingsService = settingsService;
        
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _shortcutPath = Path.Combine(startupFolder, "AxePower.lnk");
        
        _settingsService.OnSettingsChanged += SettingsService_OnSettingsChanged;
    }

    private void SettingsService_OnSettingsChanged(object? sender, AppSettings e)
    {
        SetStartup(e.StartWithWindows);
    }

    public void Initialize()
    {
        var settings = _settingsService.GetSettings();
        SetStartup(settings.StartWithWindows);
    }

    private void SetStartup(bool enable)
    {
        try
        {
            var executablePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(executablePath)) return;

            if (enable)
            {
                if (File.Exists(_shortcutPath))
                {
                    // Check if it points to the correct path
                    // To be safe, we just recreate it
                    File.Delete(_shortcutPath);
                }

                Type? t = Type.GetTypeFromProgID("WScript.Shell");
                if (t != null)
                {
                    dynamic shell = Activator.CreateInstance(t)!;
                    dynamic shortcut = shell.CreateShortcut(_shortcutPath);
                    shortcut.TargetPath = executablePath;
                    shortcut.Arguments = "--background";
                    shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath);
                    shortcut.Save();
                    
                    _logService.LogInfo("Added AxePower to Windows startup folder.");
                }
            }
            else
            {
                if (File.Exists(_shortcutPath))
                {
                    File.Delete(_shortcutPath);
                    _logService.LogInfo("Removed AxePower from Windows startup folder.");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to modify startup shortcut.", ex);
        }
    }
}
