using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Routines;

namespace AxePower.Core.Services.Routines;

/// <summary>What the routines can see at one moment. Running app names have no ".exe" and ignore case.</summary>
public sealed record RoutineWorld(DateTime Now, bool HasBattery, bool IsPluggedIn, int BatteryPercent, IReadOnlySet<string> RunningApps);

/// <summary>Reads the PC's state for the routines. Swapped for a fake in tests.</summary>
public interface IRoutineWorld
{
    RoutineWorld Read();
}

/// <summary>Everything a routine can do to the PC. Swapped for a fake in tests.</summary>
public interface IRoutineActions
{
    Task<PowerMode> GetPowerModeAsync();
    Task SetPowerModeAsync(PowerMode mode);

    /// <summary>Any mode change, whoever made it; the engine tells its own apart.</summary>
    event EventHandler<PowerMode>? PowerModeApplied;

    /// <summary>Closes these apps and returns the program paths to reopen them from.</summary>
    IReadOnlyList<string> CloseApps(IReadOnlyList<string> names);
    void Reopen(IReadOnlyList<string> paths);
    void Open(string target);

    /// <summary>Starts the power timer and returns when it is due, which identifies this countdown.</summary>
    DateTime StartPowerTimer(PowerTimerAction action, TimeSpan delay);

    /// <summary>Cancels the timer only if it is still the countdown that was started for <paramref name="due"/>.</summary>
    void CancelPowerTimer(DateTime due);

    void Notify(string message);
}

/// <summary>Whether a routine's "If" holds right now.</summary>
public static class RoutineConditions
{
    /// <summary>A running battery routine only ends 5 % above its level, so it does not flap at the line.</summary>
    public const int BatteryHysteresis = 5;

    public static bool Holds(Routine routine, RoutineWorld world, bool wasRunning)
    {
        if (routine.Conditions.Count == 0) return false;
        return routine.Match == RoutineMatch.All
            ? routine.Conditions.All(c => Holds(c, world, wasRunning))
            : routine.Conditions.Any(c => Holds(c, world, wasRunning));
    }

    public static bool Holds(RoutineCondition c, RoutineWorld world, bool wasRunning) => c.Kind switch
    {
        RoutineConditionKind.TimeBetween => InTimeRange(c, world.Now),
        RoutineConditionKind.AppRunning => RoutineText.NormalizeApp(c.App) is { Length: > 0 } app && world.RunningApps.Contains(app),
        RoutineConditionKind.PluggedIn => world.IsPluggedIn,
        RoutineConditionKind.OnBattery => world.HasBattery && !world.IsPluggedIn,
        RoutineConditionKind.BatteryAtOrBelow => world.HasBattery &&
            (wasRunning ? world.BatteryPercent < c.Percent + BatteryHysteresis : world.BatteryPercent <= c.Percent),
        _ => false
    };

    /// <summary>From is included, To is not. A range that ends before it starts runs past midnight and counts as the day it started.</summary>
    public static bool InTimeRange(RoutineCondition c, DateTime now)
    {
        if (RoutineTime.Parse(c.From) is not { } from || RoutineTime.Parse(c.To) is not { } to) return false;
        var time = TimeOnly.FromDateTime(now);
        var startDay = now.DayOfWeek;
        bool inRange;
        if (from == to) inRange = true;
        else if (from < to) inRange = time >= from && time < to;
        else if (time >= from) inRange = true;
        else if (time < to) { inRange = true; startDay = now.AddDays(-1).DayOfWeek; }
        else inRange = false;

        return inRange && c.Days switch
        {
            RoutineDays.Weekdays => startDay is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            RoutineDays.Weekends => startDay is DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true
        };
    }
}

/// <summary>
/// Runs the owner's routines. Every few seconds it reads the PC's state; a routine whose "If" has
/// become true starts and does its "Then"; one whose "If" is no longer true ends and, when it
/// undoes, puts back what it changed.
///
/// Undo is per setting. The power mode keeps a stack: the mode before any routine touched it,
/// then one entry per running routine that set it, newest on top. A routine ending takes its entry
/// out, and the mode returns to whatever is now on top — so a game ending on battery lands on the
/// battery routine's mode, not the one from before both. A mode change from anywhere else (the
/// dashboard, the tray, Auto Switch) means the owner took over: routines stop restoring the mode.
/// </summary>
public sealed class RoutineEngine : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>The first look waits for startup, which applies Smart, so a routine is not overridden by it.</summary>
    public static readonly TimeSpan FirstLookDelay = TimeSpan.FromSeconds(10);

    private readonly SettingsService _settings;
    private readonly IRoutineWorld _world;
    private readonly IRoutineActions _actions;
    private readonly LogService? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Run> _running = new();
    private readonly List<(Guid Id, PowerMode Mode)> _modeOwners = new();
    private PowerMode? _modeBefore;
    private volatile bool _applyingMode;
    private PowerMode? _outsideModeChange;
    private Timer? _poll;
    private bool _stopped;

    private sealed class Run
    {
        public Run(Routine routine) => Routine = routine;
        public Routine Routine { get; }
        public List<string> Reopen { get; } = new();
        public DateTime? TimerDue { get; set; }
    }

    public RoutineEngine(SettingsService settings, IRoutineWorld world, IRoutineActions actions, LogService? log = null)
    {
        _settings = settings;
        _world = world;
        _actions = actions;
        _log = log;
        _actions.PowerModeApplied += OnPowerModeApplied;
        _settings.OnSettingsChanged += OnSettingsChanged;
    }

    /// <summary>A routine started or ended. Raised on any thread.</summary>
    public event EventHandler? RunningChanged;

    public bool IsRunning(Guid id)
    {
        lock (_sync) return _running.ContainsKey(id);
    }

    public int RunningCount
    {
        get { lock (_sync) return _running.Count; }
    }

    /// <summary>Starts looking every <see cref="PollInterval"/>, the first time after <paramref name="firstLook"/>.</summary>
    public void Start(TimeSpan? firstLook = null)
    {
        _poll ??= new Timer(_ => Kick(), null, firstLook ?? FirstLookDelay, PollInterval);
    }

    private void OnSettingsChanged(object? sender, AppSettings e)
    {
        if (_poll != null) Kick();
    }

    private void OnPowerModeApplied(object? sender, PowerMode mode)
    {
        if (_applyingMode) return;
        lock (_sync) _outsideModeChange = mode;
    }

    private async void Kick()
    {
        try { await EvaluateAsync(); }
        catch (Exception ex) { _log?.LogError("[Routines] Evaluation failed.", ex); }
    }

    /// <summary>Looks once: ends routines that no longer hold, starts ones that now do.</summary>
    public async Task EvaluateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopped) return;
            TakeOutsideModeChange();

            var world = _world.Read();
            var routines = (_settings.GetSettings().Routines ?? new())
                .GroupBy(r => r.Id).Select(g => g.First()).ToList();
            var byId = routines.ToDictionary(r => r.Id);
            bool changed = false;

            List<Run> running;
            lock (_sync) running = _running.Values.ToList();
            foreach (var run in running)
            {
                bool stillHolds = byId.TryGetValue(run.Routine.Id, out var current) && current.Enabled
                    && RoutineConditions.Holds(current, world, wasRunning: true);
                if (stillHolds) continue;
                await EndAsync(run, run.Routine.UndoWhenItEnds);
                changed = true;
            }

            foreach (var routine in routines)
            {
                if (!routine.Enabled || IsRunning(routine.Id) || routine.Problems().Count > 0) continue;
                if (!RoutineConditions.Holds(routine, world, wasRunning: false)) continue;
                await StartRunAsync(routine.Clone());
                changed = true;
            }

            if (changed) RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops looking and ends every running routine, undoing those set to undo. For app exit.</summary>
    public async Task StopAsync()
    {
        _poll?.Dispose();
        _poll = null;
        await _gate.WaitAsync();
        try
        {
            if (_stopped) return;
            _stopped = true;
            TakeOutsideModeChange();
            List<Run> running;
            lock (_sync) running = _running.Values.ToList();
            foreach (var run in running) await EndAsync(run, run.Routine.UndoWhenItEnds);
            if (running.Count > 0) RunningChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TakeOutsideModeChange()
    {
        PowerMode? outside;
        lock (_sync)
        {
            outside = _outsideModeChange;
            _outsideModeChange = null;
        }
        if (outside is not { } mode || _modeOwners.Count == 0 || _modeOwners[^1].Mode == mode) return;

        _log?.LogInfo($"[Routines] Power mode changed to {mode} outside routines; routines will not restore it.");
        _modeOwners.Clear();
        _modeBefore = null;
    }

    private async Task StartRunAsync(Routine routine)
    {
        var run = new Run(routine);
        lock (_sync) _running[routine.Id] = run;
        _log?.LogInfo($"[Routines] '{routine.Name}' started: {RoutineText.Summary(routine)}");

        foreach (var action in routine.Actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case RoutineActionKind.SetPowerMode:
                        if (_modeOwners.Count == 0) _modeBefore = await _actions.GetPowerModeAsync();
                        _modeOwners.RemoveAll(o => o.Id == routine.Id);
                        _modeOwners.Add((routine.Id, action.Mode));
                        await ApplyModeAsync(action.Mode);
                        break;
                    case RoutineActionKind.CloseApps:
                        run.Reopen.AddRange(_actions.CloseApps(action.Apps.Where(a => !string.IsNullOrWhiteSpace(a)).ToList()));
                        break;
                    case RoutineActionKind.OpenApp:
                        _actions.Open(action.Target.Trim());
                        break;
                    case RoutineActionKind.StartPowerTimer:
                        run.TimerDue = _actions.StartPowerTimer(action.TimerAction, TimeSpan.FromMinutes(action.Minutes));
                        break;
                    case RoutineActionKind.ShowNotification:
                        _actions.Notify(action.Message.Trim());
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.LogError($"[Routines] '{routine.Name}': {RoutineText.Describe(action)} failed.", ex);
            }
        }
    }

    private async Task EndAsync(Run run, bool undo)
    {
        var routine = run.Routine;
        lock (_sync) _running.Remove(routine.Id);
        _log?.LogInfo($"[Routines] '{routine.Name}' ended{(undo ? ", undoing its changes" : string.Empty)}.");

        if (undo)
        {
            try
            {
                if (run.TimerDue is { } due) _actions.CancelPowerTimer(due);
                if (run.Reopen.Count > 0) _actions.Reopen(run.Reopen.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
            catch (Exception ex)
            {
                _log?.LogError($"[Routines] '{routine.Name}': undo failed.", ex);
            }
        }

        int index = _modeOwners.FindIndex(o => o.Id == routine.Id);
        if (index < 0) return;
        bool wasOnTop = index == _modeOwners.Count - 1;
        _modeOwners.RemoveAt(index);
        var back = _modeOwners.Count > 0 ? _modeOwners[^1].Mode : _modeBefore;
        if (_modeOwners.Count == 0) _modeBefore = null;

        if (undo && wasOnTop && back is { } mode)
        {
            try { await ApplyModeAsync(mode); }
            catch (Exception ex) { _log?.LogError($"[Routines] '{routine.Name}': restoring {mode} failed.", ex); }
        }
    }

    private async Task ApplyModeAsync(PowerMode mode)
    {
        _applyingMode = true;
        try { await _actions.SetPowerModeAsync(mode); }
        finally { _applyingMode = false; }
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _actions.PowerModeApplied -= OnPowerModeApplied;
        _settings.OnSettingsChanged -= OnSettingsChanged;
    }
}
