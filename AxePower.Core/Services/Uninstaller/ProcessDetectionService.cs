using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Detects whether a process from the application's InstallLocation is currently running.</summary>
public sealed class ProcessDetectionService
{
    /// <summary>
    /// Returns true if any process whose main module is under the application's InstallLocation is running.
    /// Uses path-based matching, NOT display-name matching, to avoid false positives.
    /// </summary>
    public bool IsApplicationRunning(InstalledApplication application)
    {
        if (string.IsNullOrWhiteSpace(application.InstallLocation))
            return false;

        var normalizedInstall = application.InstallLocation.TrimEnd('\\', '/')
                                           .ToLowerInvariant();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var modulePath = process.MainModule?.FileName ?? string.Empty;
                    if (string.IsNullOrEmpty(modulePath)) continue;

                    if (modulePath.ToLowerInvariant().StartsWith(normalizedInstall))
                        return true;
                }
                catch
                {
                    // Access denied on some system processes — skip
                }
            }
        }
        catch
        {
            // Process enumeration failed — assume not running
        }

        return false;
    }

    /// <summary>
    /// Attempts to gracefully close running processes from the InstallLocation.
    /// Returns true if all known processes exited within the timeout.
    /// </summary>
    public async Task<bool> TryCloseApplicationAsync(
        InstalledApplication application,
        TimeSpan timeout,
        bool forceKill = false)
    {
        if (string.IsNullOrWhiteSpace(application.InstallLocation))
            return true;

        var normalizedInstall = application.InstallLocation.TrimEnd('\\', '/')
                                           .ToLowerInvariant();

        var toClose = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var modulePath = process.MainModule?.FileName ?? string.Empty;
                    if (!string.IsNullOrEmpty(modulePath) &&
                        modulePath.ToLowerInvariant().StartsWith(normalizedInstall))
                        toClose.Add(process);
                }
                catch { }
            }
        }
        catch { }

        if (toClose.Count == 0) return true;

        foreach (var proc in toClose)
        {
            try
            {
                if (forceKill)
                    proc.Kill(entireProcessTree: true);
                else
                    proc.CloseMainWindow();
            }
            catch { }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            if (toClose.All(p =>
            {
                try { return p.HasExited; }
                catch { return true; }
            }))
                return true;
        }

        return toClose.All(p =>
        {
            try { return p.HasExited; }
            catch { return true; }
        });
    }
}
