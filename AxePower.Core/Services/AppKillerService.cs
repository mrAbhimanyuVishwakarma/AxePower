using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace AxePower.Core.Services;

public class AppKillerService
{
    [System.Runtime.InteropServices.DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [System.Runtime.InteropServices.DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private readonly LogService? _log;
    private readonly List<int> _suspendedProcessIds = new();
    private readonly List<string> _killedAppPaths = new();

    /// <summary>
    /// Comprehensive set of Windows-critical and system-essential processes that must
    /// NEVER be killed or suspended. Terminating any of these will crash Windows,
    /// cause a BSOD, break the display, lose authentication, or disable hardware.
    ///
    /// Sources: Microsoft documentation, Chris Titus WinUtil whitelist,
    /// Winhance protected process list, Windows Internals reference.
    /// </summary>
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Kernel & Session bootstrap (BSOD if killed) ──
        "System",
        "smss",                  // Session Manager Subsystem
        "csrss",                 // Client/Server Runtime – CRITICAL_PROCESS_DIED BSOD
        "wininit",               // Windows Initialization
        "winlogon",              // User logon/session – instant logoff/BSOD
        "services",              // Service Control Manager
        "lsass",                 // Local Security Authority – auth/login
        "lsaiso",                // LSA Isolated (Credential Guard)

        // ── Desktop & display (black screen / display crash) ──
        "dwm",                   // Desktop Window Manager – renders all windows
        "fontdrvhost",           // Font Driver Host – text rendering
        "LogonUI",               // Lock screen / logon UI

        // ── Driver framework hosts (hardware stops working) ──
        "WUDFHost",              // Windows User-Mode Driver Framework Host
        "dasHost",               // Device Association Framework Provider Host
        "deviceenumerationservice",

        // ── Service hosting & COM (widespread service failures) ──
        "svchost",               // Hosts dozens of Windows services
        "dllhost",               // COM Surrogate
        "conhost",               // Console Window Host
        "taskhostw",             // Task Host Window
        "RuntimeBroker",         // UWP permission broker
        "WmiPrvSE",              // WMI Provider Host

        // ── Shell & desktop experience ──
        "explorer",              // Windows Shell – taskbar, desktop, file manager
        "sihost",                // Shell Infrastructure Host
        "ShellExperienceHost",   // Taskbar, Action Center
        "StartMenuExperienceHost",
        "SearchApp",             // Windows Search UI
        "SearchHost",            // Windows 11 Search Host
        "SearchIndexer",         // Search indexing (crash = search breaks)
        "TextInputHost",         // Touch keyboard, emoji picker, IME
        "ApplicationFrameHost",  // UWP window frame host
        "SystemSettings",        // Settings app
        "LockApp",               // Lock screen app
        "ctfmon",                // Input method / language bar

        // ── Security & Defender ──
        "MsMpEng",               // Windows Defender Antimalware Engine
        "NisSrv",                // Network Inspection Service (Defender)
        "SecurityHealthService", // Windows Security Health
        "SecurityHealthSystray",
        "SgrmBroker",            // System Guard Runtime Monitor Broker
        "MpDefenderCoreService",

        // ── Audio subsystem (instant audio loss) ──
        "audiodg",               // Windows Audio Device Graph Isolation

        // ── Networking (instant network loss) ──
        "NetworkManager",
        "lsm",                   // Local Session Manager

        // ── Windows Update & maintenance (breaks updates) ──
        "TiWorker",              // Windows Modules Installer Worker
        "TrustedInstaller",      // Windows Modules Installer
        "wuauclt",               // Windows Update client
        "musNotification",       // Windows Update notification
        "MoUsoCoreWorker",       // Update orchestrator
        "UsoClient",             // Update session orchestrator

        // ── Registry & file system ──
        "Registry",              // Registry process (Win10+)
        "vds",                   // Virtual Disk Service
        "vdsldr",                // Virtual Disk Service loader

        // ── Memory & cache management ──
        "Memory Compression",    // System memory compression

        // ── Graphics driver hosts (display crash) ──
        "igfxCUIService",        // Intel Graphics
        "igfxEM",
        "igfxHK",
        "IntelCpHDCPSvc",        // Intel Content Protection
        "IntelCpHeciSvc",        // Intel ME
        "nvcontainer",           // NVIDIA Container
        "NVDisplay.Container",   // NVIDIA Display Container
        "atiesrxx",              // AMD External Events Utility
        "amdow",                 // AMD Display
        "AMDRSServ",             // AMD Radeon Settings

        // ── Task Manager & developer tools (user needs these) ──
        "taskmgr",               // Task Manager
        "devenv",                // Visual Studio
        "Code",                  // VS Code
        "powershell",            // PowerShell
        "pwsh",                  // PowerShell 7
        "WindowsTerminal",       // Windows Terminal
        "cmd",                   // Command Prompt

        // ── AxePower itself ──
        "AxePower",

        // ── Accessibility ──
        "Narrator",
        "osk",                   // On-Screen Keyboard
        "Magnify",               // Magnifier
    };

    /// <summary>
    /// Returns true if the given process name is a critical Windows system process
    /// that must never be killed or suspended. Used by both the service layer and
    /// the ViewModel to filter the UI display.
    /// </summary>
    public static bool IsCriticalProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return true;

        // Strip .exe suffix if present
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        if (CriticalProcesses.Contains(name)) return true;

        // Catch svchost variants (svchost, SvcHost, etc.) and numbered instances
        if (name.StartsWith("svchost", StringComparison.OrdinalIgnoreCase)) return true;

        // Catch any process running from the Windows system directories
        // (checked by path in KillBackgroundApps, here just by known prefixes)
        return false;
    }

    /// <summary>
    /// Returns true if a process at the given file path is inside a protected
    /// Windows system directory and should not be killed.
    /// </summary>
    private static bool IsSystemPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return filePath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);
    }

    public AppKillerService(LogService? log = null) => _log = log;

    public int SuspendBackgroundApps()
    {
        int suspendedCount = 0;
        var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;

        var processes = Process.GetProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == currentProcess.Id)
                    continue;

                if (process.SessionId != currentSessionId)
                    continue;

                if (IsCriticalProcess(process.ProcessName))
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Protected, elevated or already exited: left running, which is the safe outcome.
            }
        }
        
        return suspendedCount;
    }

    public int ResumeBackgroundApps()
    {
        int resumedCount = 0;
        var stillSuspended = new List<int>();
        foreach (var pid in _suspendedProcessIds)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                int status = NtResumeProcess(process.Handle);
                if (status == 0)
                {
                    resumedCount++;
                }
                else
                {
                    // A suspended app that is not resumed stays frozen: never let that pass quietly.
                    stillSuspended.Add(pid);
                    _log?.LogWarning($"[AppKiller] Could not resume process {pid} (status 0x{status:X8}); it stays frozen until resumed or ended in Task Manager.");
                }
            }
            catch (ArgumentException)
            {
                // It exited while suspended: nothing to resume.
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                stillSuspended.Add(pid);
                _log?.LogWarning($"[AppKiller] Could not resume process {pid}; it stays frozen until resumed or ended in Task Manager", ex);
            }
        }
        // Failures stay listed, so the next resume tries them again.
        _suspendedProcessIds.Clear();
        _suspendedProcessIds.AddRange(stillSuspended);
        return resumedCount;
    }

    public int KillBackgroundApps(List<string> appsToClose)
    {
        if (appsToClose == null || !appsToClose.Any()) return 0;
        
        int killedCount = 0;
        var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        
        var processes = Process.GetProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (process.Id == currentProcess.Id)
                    continue;

                if (process.SessionId != currentSessionId)
                    continue;

                // SAFETY: Never kill critical system processes regardless of what the user selected
                if (IsCriticalProcess(process.ProcessName))
                    continue;

                if (appsToClose.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase) || 
                    appsToClose.Contains(process.ProcessName + ".exe", StringComparer.OrdinalIgnoreCase))
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Access denied on MainModule: it is still closed, just not restarted later.
                    }

                    // SAFETY: Don't kill processes that live inside C:\Windows
                    if (IsSystemPath(path))
                        continue;

                    process.Kill();
                    killedCount++;

                    if (!string.IsNullOrEmpty(path) && !_killedAppPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _killedAppPaths.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Protected, elevated or already exited: left running, which is the safe outcome.
            }
        }
        
        return killedCount;
    }

    public int RestartBackgroundApps()
    {
        int restartedCount = 0;
        foreach (var path in _killedAppPaths)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    });
                    restartedCount++;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log?.LogWarning($"[AppKiller] Could not restart {path} after Game Mode", ex);
            }
        }
        _killedAppPaths.Clear();
        return restartedCount;
    }
}
