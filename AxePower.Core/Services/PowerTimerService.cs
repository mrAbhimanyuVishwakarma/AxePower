using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AxePower.Core.Services;

public enum PowerTimerAction
{
    Shutdown,
    Restart,
    Sleep
}

/// <summary>What the timer does when it runs out. Swapped for a fake in tests.</summary>
public interface IPowerActions
{
    void Run(PowerTimerAction action);
}

/// <summary>
/// Windows' own commands. Shutdown and restart are not forced, so an app holding unsaved work
/// can still ask the user; sleep never hibernates.
/// </summary>
public sealed class WindowsPowerActions : IPowerActions
{
    private readonly LogService? _log;

    public WindowsPowerActions(LogService? log = null) => _log = log;

    public void Run(PowerTimerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerTimerAction.Shutdown:
                    StartShutdown("/s /t 0");
                    break;
                case PowerTimerAction.Restart:
                    StartShutdown("/r /t 0");
                    break;
                case PowerTimerAction.Sleep:
                    if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
                        _log?.LogWarning($"[PowerTimer] Sleep was refused (error {Marshal.GetLastWin32Error()}).");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.LogError($"[PowerTimer] Could not {action}.", ex);
        }
    }

    private static void StartShutdown(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\shutdown.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

/// <summary>
/// Shut down, restart or sleep after a set time. Lives only inside AxePower: exiting the app
/// cancels it. Two minutes before the end it enters its warning state and raises
/// <see cref="WarningReached"/> once.
/// </summary>
public sealed class PowerTimerService : IDisposable
{
    public static readonly TimeSpan WarningLead = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Shortest = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    private readonly IPowerActions _actions;
    private readonly Func<DateTime> _utcNow;
    private readonly LogService? _log;
    private readonly object _lock = new();
    private readonly Timer? _timer;
    private bool _warned;

    public PowerTimerService(IPowerActions actions, LogService? log = null)
        : this(actions, () => DateTime.UtcNow, log, startClock: true)
    {
    }

    /// <summary>Tests pass their own clock and drive <see cref="Tick"/> themselves.</summary>
    internal PowerTimerService(IPowerActions actions, Func<DateTime> utcNow, LogService? log, bool startClock)
    {
        _actions = actions;
        _utcNow = utcNow;
        _log = log;
        if (startClock) _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsRunning { get; private set; }
    public PowerTimerAction Action { get; private set; }
    public DateTime DueUtc { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                if (!IsRunning) return TimeSpan.Zero;
                var left = DueUtc - _utcNow();
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>True in the last two minutes.</summary>
    public bool IsInWarning => IsRunning && Remaining <= WarningLead;

    /// <summary>Started, cancelled, extended or fired. Raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Two minutes left. Raised once per countdown, on any thread.</summary>
    public event EventHandler? WarningReached;

    /// <summary>Starts (or replaces) the countdown. The delay is clamped to 1 minute – 24 hours.</summary>
    public void Start(PowerTimerAction action, TimeSpan delay)
    {
        if (delay < Shortest) delay = Shortest;
        if (delay > Longest) delay = Longest;

        lock (_lock)
        {
            Action = action;
            DueUtc = _utcNow() + delay;
            IsRunning = true;
            // A countdown that begins inside the warning window has nothing to warn about
            _warned = delay <= WarningLead;
            _timer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        _log?.LogInfo($"[PowerTimer] {action} in {delay:h\\:mm\\:ss}.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            IsRunning = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        _log?.LogInfo("[PowerTimer] Cancelled.");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pushes the end back. Leaving the warning window re-arms the warning.</summary>
    public void Extend(TimeSpan by)
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            var due = DueUtc + by;
            if (due - _utcNow() > Longest) due = _utcNow() + Longest;
            DueUtc = due;
            if (DueUtc - _utcNow() > WarningLead) _warned = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Checks the clock: warns at two minutes, runs the action at zero.</summary>
    internal void Tick()
    {
        bool warn = false, fire = false;
        PowerTimerAction action;
        lock (_lock)
        {
            if (!IsRunning) return;
            action = Action;
            var left = DueUtc - _utcNow();
            if (left <= TimeSpan.Zero)
            {
                IsRunning = false;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                fire = true;
            }
            else if (!_warned && left <= WarningLead)
            {
                _warned = true;
                warn = true;
            }
        }

        if (warn) WarningReached?.Invoke(this, EventArgs.Empty);
        if (!fire) return;

        _log?.LogInfo($"[PowerTimer] Time is up: {action}.");
        Changed?.Invoke(this, EventArgs.Empty);
        _actions.Run(action);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            IsRunning = false;
            _timer?.Dispose();
        }
    }
}
