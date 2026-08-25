using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AxePower.Core.Services;

public enum MsiAfterburnerLaunchResult
{
    Launched,
    AlreadyRunning,
    Installing,
    InstalledAndLaunched,
    Failed
}

public class MsiAfterburnerService
{
    private readonly LogService _logService;
    private readonly NotificationService _notificationService;

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public MsiAfterburnerService(LogService logService, NotificationService notificationService)
    {
        _logService = logService;
        _notificationService = notificationService;
    }

    public string? GetAfterburnerPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSI Afterburner", "MSIAfterburner.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MSI Afterburner", "MSIAfterburner.exe")
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MSI Afterburner")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI Afterburner");
            if (key != null)
            {
                var installLoc = key.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLoc))
                {
                    var exePath = Path.Combine(installLoc, "MSIAfterburner.exe");
                    if (File.Exists(exePath)) return exePath;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"Failed to read Afterburner registry: {ex.Message}");
        }

        return null;
    }

    public string? GetRtssPath()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server", "RTSS.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server", "RTSS.exe")
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public bool IsAfterburnerRunning()
    {
        return Process.GetProcessesByName("MSIAfterburner").Length > 0;
    }

    public async Task<MsiAfterburnerLaunchResult> LaunchOrInstallAsync()
    {
        try
        {
            var exePath = GetAfterburnerPath();

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                if (IsAfterburnerRunning())
                {
                    _logService.LogInfo("MSI Afterburner is already running.");
                    TryFocusAfterburnerWindow();
                    return MsiAfterburnerLaunchResult.AlreadyRunning;
                }

                // Apply monitoring settings template if config is missing or needs setup
                EnsureMonitoringConfigApplied(Path.GetDirectoryName(exePath)!);

                // Start MSI Afterburner minimized
                _logService.LogInfo($"Launching MSI Afterburner minimized from: {exePath}");
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-min",
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = true
                };
                Process.Start(psi);

                // Also launch RTSS minimized if available and not already running
                var rtssPath = GetRtssPath();
                if (!string.IsNullOrEmpty(rtssPath) && File.Exists(rtssPath) && Process.GetProcessesByName("RTSS").Length == 0)
                {
                    try
                    {
                        var rtssPsi = new ProcessStartInfo
                        {
                            FileName = rtssPath,
                            WindowStyle = ProcessWindowStyle.Minimized,
                            UseShellExecute = true
                        };
                        Process.Start(rtssPsi);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"Failed to launch RTSS: {ex.Message}");
                    }
                }

                return MsiAfterburnerLaunchResult.Launched;
            }

            // Not installed: Run installer
            _logService.LogInfo("MSI Afterburner not detected. Starting installer...");
            _notificationService.Show("MSI Afterburner not found. Starting download & installation...");

            bool installed = await Task.Run(InstallMsiAfterburner);
            if (installed)
            {
                var newExePath = GetAfterburnerPath();
                if (!string.IsNullOrEmpty(newExePath) && File.Exists(newExePath))
                {
                    EnsureMonitoringConfigApplied(Path.GetDirectoryName(newExePath)!);

                    var psi = new ProcessStartInfo
                    {
                        FileName = newExePath,
                        Arguments = "-min",
                        WindowStyle = ProcessWindowStyle.Minimized,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return MsiAfterburnerLaunchResult.InstalledAndLaunched;
                }
            }

            return MsiAfterburnerLaunchResult.Failed;
        }
        catch (Exception ex)
        {
            _logService.LogError("Error in MSI Afterburner launch/install", ex);
            return MsiAfterburnerLaunchResult.Failed;
        }
    }

    private bool InstallMsiAfterburner()
    {
        try
        {
            // Try installing via winget first
            var wingetPsi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --id Guru3D.Afterburner --accept-package-agreements --accept-source-agreements --silent --disable-interactivity",
                UseShellExecute = true,
                Verb = "runas"
            };

            var proc = Process.Start(wingetPsi);
            proc?.WaitForExit(180000); // Wait up to 3 minutes

            var afterburnerPath = GetAfterburnerPath();
            if (!string.IsNullOrEmpty(afterburnerPath) && File.Exists(afterburnerPath))
            {
                return true;
            }

            // Fallback: PowerShell download from official MSI servers
            var downloadScript = @"
$url = 'https://download.msi.com/uti_exe/vga/MSIAfterburnerSetup.zip'
$zip = Join-Path $env:TEMP 'MSIAfterburnerSetup.zip'
$dest = Join-Path $env:TEMP 'MSIAfterburnerSetup'
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
if (Test-Path $zip) {
    Expand-Archive -Path $zip -DestinationPath $dest -Force
    $installer = Get-ChildItem -Path $dest -Filter '*.exe' -Recurse | Select-Object -First 1
    if ($installer) {
        Start-Process -FilePath $installer.FullName -ArgumentList '/S' -Wait
    }
}
";
            var psPsi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{downloadScript}\"",
                UseShellExecute = true,
                Verb = "runas"
            };

            var psProc = Process.Start(psPsi);
            psProc?.WaitForExit(180000);

            return !string.IsNullOrEmpty(GetAfterburnerPath());
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to install MSI Afterburner", ex);
            return false;
        }
    }

    private void TryFocusAfterburnerWindow()
    {
        try
        {
            var procs = Process.GetProcessesByName("MSIAfterburner");
            foreach (var proc in procs)
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(proc.MainWindowHandle);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"Failed to focus MSI Afterburner window: {ex.Message}");
        }
    }

    public void EnsureMonitoringConfigApplied(string afterburnerDir)
    {
        try
        {
            var profilesDir = Path.Combine(afterburnerDir, "Profiles");
            Directory.CreateDirectory(profilesDir);

            var cfgPath = Path.Combine(profilesDir, "MSIAfterburner.cfg");

            // Write or ensure current complete monitoring preset
            if (!File.Exists(cfgPath) || new FileInfo(cfgPath).Length < 100)
            {
                File.WriteAllText(cfgPath, MonitoringConfigPreset);
                _logService.LogInfo("Applied MSI Afterburner hardware monitoring preset.");
            }

            // Also configure RTSS if available
            var rtssDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server");
            if (Directory.Exists(rtssDir))
            {
                var rtssProfilesDir = Path.Combine(rtssDir, "Profiles");
                Directory.CreateDirectory(rtssProfilesDir);
                var globalRtssPath = Path.Combine(rtssProfilesDir, "Global");
                if (!File.Exists(globalRtssPath))
                {
                    File.WriteAllText(globalRtssPath, RtssGlobalConfigPreset);
                    _logService.LogInfo("Applied RTSS global profile preset.");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"Failed to ensure monitoring config: {ex.Message}");
        }
    }

    private const string MonitoringConfigPreset = @"[Properties]
Skin=MSIWin11Dark.usf
StartWithWindows=0
StartMinimized=1
HwPollPeriod=1000
LockProfiles=0
ShowHints=1
ShowTooltips=1
LCDFont=font4x6.dat
RememberSettings=0
FirstRun=0
FirstUserDefineClick=1
FirstServerRun=0
CurrentGpu=0
Sync=1
Link=1
LinkThermal=1
FanSync=1
CurrentFan=0
ShowOSDTime=0
CaptureOSD=1
BenchmarkPath=%ABDir%\Benchmark.txt
AppendBenchmark=1
ScreenCaptureFormat=png
ScreenCaptureQuality=100
VideoCaptureFormat=MJPG
VideoCaptureQuality=85
VideoCaptureFramerate=30
WindowX=953
WindowY=285
ProfileContents=1
Profile2D=-1
Profile3D=-1
SwAutoFanControl=0
RestoreAfterSuspendedMode=1
PauseMonitoring=0
AttachMonitoringWindow=1
HideMonitoring=0
MonitoringWindowOnTop=1
LogPath=%ABDir%\HardwareMonitoring.hml
EnableLog=0
RecreateLog=0
LogLimit=10
OSDLayout=1
UnlockVoltageControl=0
UnlockVoltageMonitoring=0
OEM=0
ForceConstantVoltage=0
SingleTrayIconMode=0
Fahrenheit=0
Time24=0
LCDGraph=0
LayeredWindowMode=1
LayeredWindowAlpha=244
ScaleFactor=100
MonitoringWindowX=1660
MonitoringWindowY=285
MonitoringWindowW=900
MonitoringWindowH=800

[Source GPU1 temperature]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=100
MinLimit=0

[Source GPU1 usage]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=100
MinLimit=0

[Source GPU1 power]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=300.0
MinLimit=0.0

[Source CPU temperature]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=100
MinLimit=0

[Source CPU usage]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=100
MinLimit=0

[Source CPU clock]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=5000
MinLimit=0

[Source CPU power]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=200.0
MinLimit=0.0

[Source RAM usage]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=16384
MinLimit=0

[Source Framerate]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=200
MinLimit=0
OSDItemType=2

[Source Framerate 0.1% Low]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=200
MinLimit=0

[Source Framerate 1% Low]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=200
MinLimit=0

[Source GPU1 core clock]
ShowInOSD=1
ShowInLCD=0
ShowInTray=0
EnableDataFiltering=0
MaxLimit=2500
MinLimit=0
";

    private const string RtssGlobalConfigPreset = @"[OSD]
EnableOSD=1
EnableBgnd=1
EnableFill=0
EnableStat=0
BaseColor=00FF8000
BgndColor=00000000
FillColor=80000000
PositionX=1
PositionY=1
ZoomRatio=3
CoordinateSpace=0
EnableFrameColorBar=0
FrameColorBarMode=0
RefreshPeriod=500
IntegerFramerate=1
MaximumFrametime=0
EnableFrametimeHistory=0
FrametimeHistoryWidth=-32
FrametimeHistoryHeight=-4
FrametimeHistoryStyle=0
ScaleToFit=0
[Statistics]
FramerateAveragingInterval=1000
PeakFramerateCalc=0
PercentileCalc=0
FrametimeCalc=0
PercentileBuffer=0
[Framerate]
Limit=0
LimitDenominator=1
LimitTime=0
LimitTimeDenominator=1
SyncDisplay=0
SyncScanline0=0
SyncScanline1=0
SyncPeriods=0
SyncLimiter=0
PassiveWait=1
ReflexSleep=0
ReflexSetLatencyMarker=1
[Hooking]
EnableHooking=1
EnableFloatingInjectionAddress=0
EnableDynamicOffsetDetection=0
HookLoadLibrary=0
HookDirectDraw=0
HookDirect3D8=1
HookDirect3D9=1
HookDirect3DSwapChain9Present=1
HookDXGI=1
HookDirect3D12=1
HookOpenGL=1
HookVulkan=1
InjectionDelay=15000
UseDetours=0
[Font]
Height=-9
Weight=400
Face=Unispace
Load=
";
}
