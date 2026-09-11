using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class StartupService
{
    // Auto-start runs through a scheduled task (at the user's normal privilege level)
    // rather than a Startup-folder shortcut, so it survives shortcut cleanup tools.
    private const string TaskName = "AxePower Startup";

    private readonly LogService _logService;
    private readonly SettingsService _settingsService;
    private readonly string _legacyShortcutPath;

    public StartupService(LogService logService, SettingsService settingsService)
    {
        _logService = logService;
        _settingsService = settingsService;

        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _legacyShortcutPath = Path.Combine(startupFolder, "AxePower.lnk");

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
            // Remove the pre-v1.6 Startup-folder shortcut in either case
            if (File.Exists(_legacyShortcutPath))
            {
                try { File.Delete(_legacyShortcutPath); } catch { }
            }

            if (enable)
            {
                var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(executablePath)) return;
                CreateStartupTask(executablePath);
            }
            else
            {
                RunSchTasks($"/Delete /F /TN \"{TaskName}\"");
                _logService.LogInfo("Removed AxePower startup task.");
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to modify Windows startup registration.", ex);
        }
    }

    private void CreateStartupTask(string executablePath)
    {
        string userId = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}") ?? string.Empty;
        string command = SecurityElement.Escape(executablePath) ?? string.Empty;
        string workingDir = SecurityElement.Escape(Path.GetDirectoryName(executablePath) ?? string.Empty) ?? string.Empty;

        // XML instead of plain schtasks flags: only XML can express "no execution time limit"
        // (the CLI default kills the task after 72 hours) and battery-friendly settings.
        string xml = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{userId}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{userId}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <AllowHardTerminate>false</AllowHardTerminate>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>--background</Arguments>
      <WorkingDirectory>{workingDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";

        string xmlPath = Path.Combine(Path.GetTempPath(), $"AxePower_startup_task_{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
            bool ok = RunSchTasks($"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"");
            if (ok)
                _logService.LogInfo("Registered AxePower startup task (highest run level).");
            else
                _logService.LogWarning("schtasks could not register the AxePower startup task.");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static bool RunSchTasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return false;
        proc.WaitForExit(15000);
        return proc.HasExited && proc.ExitCode == 0;
    }
}
