using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AxePower.Core.Services;

public enum DriverUpdateLaunchResult
{
    StartedNew,
    FocusedExisting,
    CancelledByUser,
    Failed
}

public class DriverUpdateService
{
    private readonly LogService _logService;
    private Process? _activeProcess;

    private const int SW_RESTORE = 9;
    private const int SW_SHOWNORMAL = 1;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public DriverUpdateService(LogService logService)
    {
        _logService = logService;
    }

    /// <summary>
    /// Checks if an Update PC process is already running. If so, restores and brings its window to the front.
    /// Otherwise, launches a new elevated PowerShell session minimized in the taskbar.
    /// </summary>
    public DriverUpdateLaunchResult LaunchDriverAndSystemUpdate()
    {
        try
        {
            // If already running, focus and restore existing window
            if (TryFocusExistingUpdateWindow())
            {
                return DriverUpdateLaunchResult.FocusedExisting;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var scriptsDir = Path.Combine(localAppData, "AxePower", "Scripts");
            Directory.CreateDirectory(scriptsDir);

            var scriptPath = Path.Combine(scriptsDir, "UpdateDrivers.ps1");

            var scriptContent = @"
# AxePower - Automated Driver & System Update
$Host.UI.RawUI.WindowTitle = ""AxePower - Driver & System Update (Administrator)""
Clear-Host

Write-Host ""========================================================================="" -ForegroundColor Cyan
Write-Host ""                    AXEPOWER DRIVER & SYSTEM UPDATER                    "" -ForegroundColor Cyan
Write-Host ""========================================================================="" -ForegroundColor Cyan
Write-Host "" Running elevated update pipeline..."" -ForegroundColor Gray
Write-Host """"

# ---------------------------------------------------------
# STEP 1: Windows Updates
# ---------------------------------------------------------
Write-Host ""[Step 1/3] Checking & Installing Windows Updates..."" -ForegroundColor Yellow
Write-Host ""---------------------------------------------------------"" -ForegroundColor DarkGray

try {
    $updateSession = New-Object -ComObject Microsoft.Update.Session
    $updateSearcher = $updateSession.CreateUpdateSearcher()
    Write-Host ""Searching for pending Windows Updates..."" -ForegroundColor Gray
    $searchResult = $updateSearcher.Search(""IsInstalled=0 and Type='Software' and IsHidden=0"")

    if ($searchResult.Updates.Count -eq 0) {
        Write-Host ""[+] Windows is up to date! No pending software updates found."" -ForegroundColor Green
    } else {
        Write-Host ""[+] Found $($searchResult.Updates.Count) pending update(s):"" -ForegroundColor Yellow
        $updatesToDownload = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($update in $searchResult.Updates) {
            Write-Host ""   - $($update.Title)"" -ForegroundColor White
            $updatesToDownload.Add($update) | Out-Null
        }

        Write-Host ""Downloading updates..."" -ForegroundColor Cyan
        $downloader = $updateSession.CreateUpdateDownloader()
        $downloader.Updates = $updatesToDownload
        $downloader.Download()

        $updatesToInstall = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($update in $searchResult.Updates) {
            if ($update.IsDownloaded) {
                $updatesToInstall.Add($update) | Out-Null
            }
        }

        if ($updatesToInstall.Count -gt 0) {
            Write-Host ""Installing updates..."" -ForegroundColor Cyan
            $installer = $updateSession.CreateUpdateInstaller()
            $installer.Updates = $updatesToInstall
            $installResult = $installer.Install()
            Write-Host ""[+] Windows Updates completed with Result Code: $($installResult.ResultCode)"" -ForegroundColor Green
        }
    }
} catch {
    Write-Host ""[!] Windows Update notice: $($_.Exception.Message)"" -ForegroundColor DarkYellow
    Write-Host ""Triggering interactive update scan via USOClient..."" -ForegroundColor Gray
    Start-Process -FilePath ""usoclient.exe"" -ArgumentList ""StartInteractiveScan"" -NoNewWindow -Wait -ErrorAction SilentlyContinue
}

Write-Host """"

# ---------------------------------------------------------
# STEP 2: Graphics Driver & GPU Vendor App Updates
# ---------------------------------------------------------
Write-Host ""[Step 2/3] Checking GPU & Graphics Driver Updates..."" -ForegroundColor Yellow
Write-Host ""---------------------------------------------------------"" -ForegroundColor DarkGray

$gpus = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue
$hasNvidia = $false
$hasAmd = $false
$hasIntel = $false

foreach ($gpu in $gpus) {
    Write-Host ""[+] Detected GPU: $($gpu.Name)"" -ForegroundColor White
    if ($gpu.DriverVersion) {
        Write-Host ""    Current Driver Version: $($gpu.DriverVersion)"" -ForegroundColor Gray
    }

    if ($gpu.Name -match ""(?i)NVIDIA|GeForce|RTX|GTX|Quadro"") { $hasNvidia = $true }
    if ($gpu.Name -match ""(?i)AMD|Radeon"") { $hasAmd = $true }
    if ($gpu.Name -match ""(?i)Intel|Arc|Iris|UHD|HD Graphics"") { $hasIntel = $true }
}

if ($hasNvidia) {
    Write-Host ""--> Checking NVIDIA App & GeForce Experience updates via winget..."" -ForegroundColor Cyan
    winget upgrade --id Nvidia.NvidiaApp --accept-package-agreements --accept-source-agreements --silent --disable-interactivity 2>$null
    winget upgrade --id Nvidia.GeForceExperience --accept-package-agreements --accept-source-agreements --silent --disable-interactivity 2>$null
}

if ($hasAmd) {
    Write-Host ""--> Checking AMD Software Adrenalin updates via winget..."" -ForegroundColor Cyan
    winget upgrade --id AdvancedMicroDevices.AMDSoftware --accept-package-agreements --accept-source-agreements --silent --disable-interactivity 2>$null
}

if ($hasIntel) {
    Write-Host ""--> Checking Intel Driver & Support Assistant updates via winget..."" -ForegroundColor Cyan
    winget upgrade --id Intel.IntelDriverAndSupportAssistant --accept-package-agreements --accept-source-agreements --silent --disable-interactivity 2>$null
    winget upgrade --id Intel.ArcControl --accept-package-agreements --accept-source-agreements --silent --disable-interactivity 2>$null
}

Write-Host """"

# ---------------------------------------------------------
# STEP 3: Full Package & System Upgrades via WinGet
# ---------------------------------------------------------
Write-Host ""[Step 3/3] Upgrading All Installed System Packages via WinGet..."" -ForegroundColor Yellow
Write-Host ""---------------------------------------------------------"" -ForegroundColor DarkGray

winget upgrade --all --include-unknown --accept-package-agreements --accept-source-agreements --silent --disable-interactivity

Write-Host """"
Write-Host ""========================================================================="" -ForegroundColor Green
Write-Host ""           ALL SYSTEM & DRIVER UPDATES COMPLETED SUCCESSFULLY            "" -ForegroundColor Green
Write-Host ""========================================================================="" -ForegroundColor Green
Write-Host """"
Write-Host ""Press any key to close this window..."" -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey(""NoEcho,IncludeKeyDown"")
";

            File.WriteAllText(scriptPath, scriptContent);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-WindowStyle Minimized -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                WindowStyle = ProcessWindowStyle.Minimized,
                UseShellExecute = true,
                Verb = "runas"
            };

            _logService.LogInfo("Launching elevated driver update script minimized...");
            _activeProcess = Process.Start(psi);
            return DriverUpdateLaunchResult.StartedNew;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled UAC prompt
            _logService.LogWarning("Driver update UAC prompt was cancelled by user.");
            return DriverUpdateLaunchResult.CancelledByUser;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error launching driver and system update", ex);
            return DriverUpdateLaunchResult.Failed;
        }
    }

    /// <summary>
    /// Finds any running Update PC window, restores it from minimized state, and brings it to the foreground.
    /// </summary>
    public bool TryFocusExistingUpdateWindow()
    {
        IntPtr foundHwnd = IntPtr.Zero;

        // 1. Check cached process
        if (_activeProcess != null)
        {
            try
            {
                if (!_activeProcess.HasExited)
                {
                    _activeProcess.Refresh();
                    if (_activeProcess.MainWindowHandle != IntPtr.Zero)
                    {
                        foundHwnd = _activeProcess.MainWindowHandle;
                    }
                }
                else
                {
                    _activeProcess = null;
                }
            }
            catch
            {
                _activeProcess = null;
            }
        }

        // 2. Search for any window whose title matches the update window
        if (foundHwnd == IntPtr.Zero)
        {
            EnumWindows((hWnd, lParam) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (!string.IsNullOrEmpty(title) && title.Contains("AxePower - Driver & System Update", StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hWnd;
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);
        }

        // 3. Search powershell processes
        if (foundHwnd == IntPtr.Zero)
        {
            foreach (var proc in Process.GetProcessesByName("powershell"))
            {
                try
                {
                    if (!proc.HasExited && !string.IsNullOrEmpty(proc.MainWindowTitle) &&
                        proc.MainWindowTitle.Contains("AxePower - Driver & System Update", StringComparison.OrdinalIgnoreCase))
                    {
                        foundHwnd = proc.MainWindowHandle;
                        break;
                    }
                }
                catch { }
            }
        }

        if (foundHwnd != IntPtr.Zero)
        {
            ShowWindow(foundHwnd, SW_RESTORE);
            SetForegroundWindow(foundHwnd);
            _logService.LogInfo("Update PC is already running. Restored and focused existing window.");
            return true;
        }

        return false;
    }
}
