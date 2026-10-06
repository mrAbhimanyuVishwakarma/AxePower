using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Routines;

namespace AxePower.Core.Services.Routines;

/// <summary>The PC's clock, power source, battery and running apps. Cheap enough to read every few seconds.</summary>
public sealed class WindowsRoutineWorld : IRoutineWorld
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private const byte NoSystemBattery = 128;
    private const byte Unknown = 255;

    public RoutineWorld Read()
    {
        bool hasBattery = false, pluggedIn = true;
        int percent = 100;
        if (GetSystemPowerStatus(out var status))
        {
            hasBattery = status.BatteryFlag != Unknown && (status.BatteryFlag & NoSystemBattery) == 0;
            pluggedIn = status.ACLineStatus != 0;   // 1 online, 255 unknown: only 0 means on battery
            if (status.BatteryLifePercent != Unknown) percent = status.BatteryLifePercent;
        }

        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try { apps.Add(process.ProcessName); }
            catch (Exception caught) when (ExpectedFailures.Process(caught)) { /* Exited while listed. */ }
            finally { process.Dispose(); }
        }

        return new RoutineWorld(DateTime.Now, hasBattery, pluggedIn, percent, apps);
    }
}

/// <summary>
/// What routines do, through AxePower's own services: the power plans, the power timer (so shutting
/// down always comes with its two-minute warning) and the in-app notification.
///
/// AxePower runs elevated. Apps it opens go through Explorer, so they start as the signed-in user
/// and not with administrator rights.
/// </summary>
public sealed class WindowsRoutineActions : IRoutineActions
{
    private readonly PowerPlanService _plans;
    private readonly PowerTimerService _timer;
    private readonly NotificationService _notifications;
    private readonly LogService _log;

    public WindowsRoutineActions(PowerPlanService plans, PowerTimerService timer, NotificationService notifications, LogService log)
    {
        _plans = plans;
        _timer = timer;
        _notifications = notifications;
        _log = log;
        _plans.ModeApplied += (s, mode) => PowerModeApplied?.Invoke(this, mode);
    }

    public event EventHandler<PowerMode>? PowerModeApplied;

    public Task<PowerMode> GetPowerModeAsync() => _plans.GetActivePowerModeAsync();

    public async Task SetPowerModeAsync(PowerMode mode)
    {
        if (!await _plans.ApplyModeAsync(mode))
            _log.LogWarning($"[Routines] {mode} mode could not be applied.");
    }

    /// <summary>Asks each app's window to close, waits up to 5 s, then ends it. Never touches system processes.</summary>
    public IReadOnlyList<string> CloseApps(IReadOnlyList<string> names)
    {
        var wanted = names.Select(RoutineText.NormalizeApp).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reopen = new List<string>();
        int me = Environment.ProcessId;
        int session;
        using (var current = Process.GetCurrentProcess()) session = current.SessionId;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == me || process.SessionId != session || !wanted.Contains(process.ProcessName)
                        || AppKillerService.IsCriticalProcess(process.ProcessName))
                        continue;

                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch (Exception caught) when (ExpectedFailures.Process(caught)) { /* Protected process: its path stays unknown. */ }
                    if (path != null && path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;

                    bool closed = process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow() && process.WaitForExit(5000);
                    if (!closed && !process.HasExited) process.Kill();

                    if (path != null && !reopen.Contains(path, StringComparer.OrdinalIgnoreCase)) reopen.Add(path);
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[Routines] Could not close {process.ProcessName}: {ex.Message}");
                }
            }
        }
        return reopen;
    }

    public void Reopen(IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Where(File.Exists)) Open(path);
    }

    public void Open(string target) =>
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{target.Trim().Trim('"')}\"", UseShellExecute = true });

    public DateTime StartPowerTimer(PowerTimerAction action, TimeSpan delay)
    {
        _timer.Start(action, delay);
        return _timer.DueUtc;
    }

    public void CancelPowerTimer(DateTime due)
    {
        if (_timer.IsRunning && _timer.DueUtc == due) _timer.Cancel();
    }

    public void Notify(string message) => _notifications.Show(message);
}
