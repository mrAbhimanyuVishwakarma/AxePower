using System;
using System.Diagnostics;
using System.Linq;

namespace AxePower.Core.Services;

public class AppKillerService
{
    [System.Runtime.InteropServices.DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private readonly System.Collections.Generic.List<int> _suspendedProcessIds = new();

    public int SuspendBackgroundApps()
    {
        int suspendedCount = 0;
        var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        
        var whitelist = new[] 
        { 
            "explorer", "taskmgr", "devenv", "Code", "powershell", "cmd", 
            "conhost", "svchost", "dllhost", "sihost", "ctfmon", "taskhostw",
            "SearchApp", "StartMenuExperienceHost", "ShellExperienceHost",
            "TextInputHost", "ApplicationFrameHost", "RuntimeBroker", "AxePower"
        };

        var processes = Process.GetProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == currentProcess.Id)
                    continue;

                if (process.SessionId != currentSessionId)
                    continue;

                if (whitelist.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (_suspendedProcessIds.Contains(process.Id))
                    continue;

                // Suspend applications with a visible window handle (ignoring empty titles often used by tray apps)
                if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    NtSuspendProcess(process.Handle);
                    _suspendedProcessIds.Add(process.Id);
                    suspendedCount++;
                }
            }
            catch
            {
                // Ignore processes we can't access
            }
        }
        
        return suspendedCount;
    }

    public int ResumeBackgroundApps()
    {
        int resumedCount = 0;
        foreach (var pid in _suspendedProcessIds)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                NtResumeProcess(process.Handle);
                resumedCount++;
            }
            catch
            {
                // Process might have been closed or already exited
            }
        }
        _suspendedProcessIds.Clear();
        return resumedCount;
    }
}
