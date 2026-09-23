using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Finds and closes the running processes of an installed application.</summary>
public interface IProcessDetectionService
{
    /// <summary>Processes whose image lives under the app's install folder.</summary>
    IReadOnlyList<RunningProcessInfo> GetRunningProcesses(InstalledApplication application);

    bool IsApplicationRunning(InstalledApplication application);

    /// <summary>
    /// Asks the app's windows to close (or terminates it when <paramref name="forceKill"/>) and
    /// waits up to <paramref name="timeout"/>. True when none of its processes are left.
    /// </summary>
    Task<bool> TryCloseApplicationAsync(InstalledApplication application, TimeSpan timeout, bool forceKill = false);
}

/// <summary>
/// Detects running apps by image path, using Toolhelp snapshots and
/// QueryFullProcessImageName so elevated and 32-bit processes are seen too
/// (<c>Process.MainModule</c> throws for those).
/// </summary>
public sealed class ProcessDetectionService : IProcessDetectionService
{
    public IReadOnlyList<RunningProcessInfo> GetRunningProcesses(InstalledApplication application)
    {
        var folder = ProtectedPaths.Normalize(application.InstallLocation);
        // A missing or shared folder (C:\Program Files) would match unrelated programs
        if (folder is null || ProtectedPaths.GetBlockReason(folder) is not null) return [];

        var ownPid = Environment.ProcessId;
        var results = new List<RunningProcessInfo>();
        try
        {
            foreach (var entry in NativeProcesses.Snapshot())
            {
                if (entry.Pid is 0 or 4 || entry.Pid == ownPid) continue;
                var image = NativeProcesses.GetImagePath(entry.Pid);
                if (image is not null && ProtectedPaths.IsSameOrUnder(image, folder))
                    results.Add(new RunningProcessInfo(entry.Pid, entry.ExeName, image));
            }
        }
        catch
        {
            // Enumeration failed: report nothing rather than guess
        }
        return results;
    }

    public bool IsApplicationRunning(InstalledApplication application) =>
        GetRunningProcesses(application).Count > 0;

    public async Task<bool> TryCloseApplicationAsync(InstalledApplication application, TimeSpan timeout, bool forceKill = false)
    {
        var running = GetRunningProcesses(application);
        if (running.Count == 0) return true;

        foreach (var info in running)
        {
            try
            {
                using var process = Process.GetProcessById(info.Pid);
                if (forceKill)
                    process.Kill(entireProcessTree: true);
                else
                    process.CloseMainWindow();
            }
            catch
            {
                // Gone already, or it runs elevated and we can't signal it
            }
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (GetRunningProcesses(application).Count == 0) return true;
        }
        return GetRunningProcesses(application).Count == 0;
    }
}
