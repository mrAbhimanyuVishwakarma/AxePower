using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Routines;
using AxePower.Core.Services;
using AxePower.Core.Services.Routines;

namespace AxePower.Tests.Routines;

/// <summary>The routine engine over a fake PC and fake actions. Nothing here touches the real machine.</summary>
public sealed class RoutineEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerRoutines", Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly FakeWorld _world = new();
    private readonly FakeActions _actions = new();
    private readonly RoutineEngine _engine;

    public RoutineEngineTests()
    {
        _settings = new SettingsService(new LogService(), _dir);
        _engine = new RoutineEngine(_settings, _world, _actions);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Save(params Routine[] routines)
    {
        var s = _settings.GetSettings();
        s.Routines = routines.ToList();
        _settings.SaveSettings(s);
    }

    private static Routine Battery(PowerMode mode = PowerMode.Eco, bool undo = true) => new()
    {
        Name = "Battery",
        Conditions = { new RoutineCondition { Kind = RoutineConditionKind.OnBattery } },
        Actions = { new RoutineAction { Kind = RoutineActionKind.SetPowerMode, Mode = mode } },
        UndoWhenItEnds = undo
    };

    private static Routine Game(string app = "cs2", bool undo = true) => new()
    {
        Name = "Gaming",
        Conditions = { new RoutineCondition { Kind = RoutineConditionKind.AppRunning, App = app + ".exe" } },
        Actions =
        {
            new RoutineAction { Kind = RoutineActionKind.SetPowerMode, Mode = PowerMode.Beast },
            new RoutineAction { Kind = RoutineActionKind.CloseApps, Apps = { "Discord" } }
        },
        UndoWhenItEnds = undo
    };

    [Fact]
    public async Task StartsWhenItsIfHolds_AndRunsItsActionsOnce()
    {
        Save(Battery());
        await _engine.EvaluateAsync();
        Assert.Empty(_actions.ModesSet);

        _world.PluggedIn = false;
        await _engine.EvaluateAsync();
        await _engine.EvaluateAsync();

        Assert.Equal(new[] { PowerMode.Eco }, _actions.ModesSet);
        Assert.Equal(1, _engine.RunningCount);
    }

    [Fact]
    public async Task EndingPutsThePreviousModeBack()
    {
        Save(Battery());
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();
        _world.PluggedIn = true;
        await _engine.EvaluateAsync();

        Assert.Equal(new[] { PowerMode.Eco, PowerMode.Smart }, _actions.ModesSet);
        Assert.Equal(0, _engine.RunningCount);
    }

    [Fact]
    public async Task WithoutUndo_TheModeStaysWhenItEnds()
    {
        Save(Battery(undo: false));
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();
        _world.PluggedIn = true;
        await _engine.EvaluateAsync();

        Assert.Equal(new[] { PowerMode.Eco }, _actions.ModesSet);
    }

    [Fact]
    public async Task AGameEndingOnBattery_LandsOnTheBatteryRoutinesMode_ThenTheOriginal()
    {
        Save(Battery(), Game());
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();                 // battery → Eco
        _world.Apps.Add("cs2");
        await _engine.EvaluateAsync();                 // game → Beast
        _world.Apps.Remove("cs2");
        await _engine.EvaluateAsync();                 // game ends → back to Eco, not Smart
        _world.PluggedIn = true;
        await _engine.EvaluateAsync();                 // battery ends → Smart

        Assert.Equal(new[] { PowerMode.Eco, PowerMode.Beast, PowerMode.Eco, PowerMode.Smart }, _actions.ModesSet);
    }

    [Fact]
    public async Task ARoutineUnderneathEnding_LeavesTheTopOnesModeAlone()
    {
        Save(Battery(), Game());
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();                 // Eco
        _world.Apps.Add("cs2");
        await _engine.EvaluateAsync();                 // Beast on top
        _world.PluggedIn = true;
        await _engine.EvaluateAsync();                 // battery ends underneath: nothing to apply
        Assert.Equal(new[] { PowerMode.Eco, PowerMode.Beast }, _actions.ModesSet);

        _world.Apps.Remove("cs2");
        await _engine.EvaluateAsync();                 // last one ends: the mode from before both
        Assert.Equal(PowerMode.Smart, _actions.ModesSet[^1]);
    }

    [Fact]
    public async Task AModeChangeFromElsewhere_MeansRoutinesStopRestoringIt()
    {
        Save(Battery());
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();                 // Eco
        _actions.ChangeModeElsewhere(PowerMode.Beast); // the owner picks Beast on the dashboard
        _world.PluggedIn = true;
        await _engine.EvaluateAsync();

        Assert.Equal(new[] { PowerMode.Eco }, _actions.ModesSet);
        Assert.Equal(PowerMode.Beast, _actions.Mode);
    }

    [Fact]
    public async Task ClosedAppsAreReopened_OnlyWhenItUndoes()
    {
        _actions.ClosedPaths["discord"] = @"C:\Apps\Discord.exe";
        Save(Game());
        _world.Apps.Add("CS2");                         // process names ignore case
        await _engine.EvaluateAsync();
        Assert.Equal(new[] { "Discord" }, _actions.Closed);

        _world.Apps.Clear();
        await _engine.EvaluateAsync();
        Assert.Equal(new[] { @"C:\Apps\Discord.exe" }, _actions.Reopened);

        Save(Game(undo: false));
        _world.Apps.Add("cs2");
        await _engine.EvaluateAsync();
        _world.Apps.Clear();
        await _engine.EvaluateAsync();
        Assert.Single(_actions.Reopened);
    }

    [Fact]
    public async Task ItsPowerTimerIsCancelled_WhenItEnds()
    {
        Save(new Routine
        {
            Name = "Bedtime",
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.TimeBetween, From = "01:00", To = "05:00" } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.StartPowerTimer, TimerAction = PowerTimerAction.Sleep, Minutes = 10 } }
        });
        _world.Now = new DateTime(2026, 9, 28, 1, 30, 0);
        await _engine.EvaluateAsync();
        Assert.Equal<(PowerTimerAction, TimeSpan)?>((PowerTimerAction.Sleep, TimeSpan.FromMinutes(10)), _actions.TimerStarted);

        _world.Now = new DateTime(2026, 9, 28, 5, 0, 0);
        await _engine.EvaluateAsync();
        Assert.Equal(new[] { FakeActions.Due }, _actions.TimersCancelled);
    }

    [Fact]
    public async Task TurningARunningRoutineOff_EndsAndUndoesIt()
    {
        var battery = Battery();
        Save(battery);
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();

        battery.Enabled = false;
        Save(battery);
        await _engine.EvaluateAsync();

        Assert.Equal(new[] { PowerMode.Eco, PowerMode.Smart }, _actions.ModesSet);
        Assert.False(_engine.IsRunning(battery.Id));
    }

    [Fact]
    public async Task StoppingAxePower_UndoesWhatIsRunning()
    {
        Save(Battery());
        _world.PluggedIn = false;
        await _engine.EvaluateAsync();
        await _engine.StopAsync();
        await _engine.EvaluateAsync();                 // stopped: looks no more

        Assert.Equal(new[] { PowerMode.Eco, PowerMode.Smart }, _actions.ModesSet);
    }

    [Fact]
    public async Task ARoutineThatCannotBeSaved_NeverRuns()
    {
        Save(new Routine
        {
            Name = "Broken",
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.PluggedIn } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.ShowNotification, Message = "" } }
        });
        await _engine.EvaluateAsync();
        Assert.Empty(_actions.Notes);
    }

    [Fact]
    public async Task AnyOfThese_StartsOnOne_AllOfThese_NeedsEvery()
    {
        var any = new Routine
        {
            Name = "Any",
            Match = RoutineMatch.Any,
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.OnBattery }, new RoutineCondition { Kind = RoutineConditionKind.AppRunning, App = "obs64" } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.ShowNotification, Message = "any" } }
        };
        var all = new Routine
        {
            Name = "All",
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.OnBattery }, new RoutineCondition { Kind = RoutineConditionKind.AppRunning, App = "obs64" } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.ShowNotification, Message = "all" } }
        };
        Save(any, all);
        _world.Apps.Add("obs64");
        await _engine.EvaluateAsync();
        Assert.Equal(new[] { "any" }, _actions.Notes);

        _world.PluggedIn = false;
        await _engine.EvaluateAsync();
        Assert.Equal(new[] { "any", "all" }, _actions.Notes);
    }

    [Fact]
    public void Battery_StartsAtItsLevel_AndOnlyEndsFivePercentAbove()
    {
        var c = new RoutineCondition { Kind = RoutineConditionKind.BatteryAtOrBelow, Percent = 20 };
        RoutineWorld At(int percent) => new(DateTime.Now, true, false, percent, new HashSet<string>());

        Assert.False(RoutineConditions.Holds(c, At(21), wasRunning: false));
        Assert.True(RoutineConditions.Holds(c, At(20), wasRunning: false));
        Assert.True(RoutineConditions.Holds(c, At(24), wasRunning: true));
        Assert.False(RoutineConditions.Holds(c, At(25), wasRunning: true));
        Assert.False(RoutineConditions.Holds(c, new RoutineWorld(DateTime.Now, false, true, 10, new HashSet<string>()), false));
    }

    [Theory]
    [InlineData("23:00", "07:00", RoutineDays.EveryDay, "2026-09-28 23:30", true)]
    [InlineData("23:00", "07:00", RoutineDays.EveryDay, "2026-09-28 06:59", true)]
    [InlineData("23:00", "07:00", RoutineDays.EveryDay, "2026-09-28 07:00", false)]
    [InlineData("09:00", "17:00", RoutineDays.EveryDay, "2026-09-28 08:59", false)]
    [InlineData("09:00", "17:00", RoutineDays.Weekdays, "2026-09-28 10:00", true)]    // Monday
    [InlineData("09:00", "17:00", RoutineDays.Weekdays, "2026-09-27 10:00", false)]   // Sunday
    [InlineData("23:00", "07:00", RoutineDays.Weekends, "2026-09-28 02:00", true)]    // Monday 02:00 belongs to Sunday night
    [InlineData("23:00", "07:00", RoutineDays.Weekends, "2026-09-29 02:00", false)]   // Tuesday 02:00 belongs to Monday night
    public void TimeRanges_RunPastMidnight_AndCountAsTheDayTheyStarted(string from, string to, RoutineDays days, string now, bool expected)
    {
        var c = new RoutineCondition { Kind = RoutineConditionKind.TimeBetween, From = from, To = to, Days = days };
        Assert.Equal(expected, RoutineConditions.InTimeRange(c, DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private sealed class FakeWorld : IRoutineWorld
    {
        public DateTime Now { get; set; } = new(2026, 9, 28, 12, 0, 0);
        public bool PluggedIn { get; set; } = true;
        public int Percent { get; set; } = 80;
        public HashSet<string> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public RoutineWorld Read() => new(Now, true, PluggedIn, Percent, new HashSet<string>(Apps, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class FakeActions : IRoutineActions
    {
        public static readonly DateTime Due = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

        public PowerMode Mode { get; private set; } = PowerMode.Smart;
        public List<PowerMode> ModesSet { get; } = new();
        public List<string> Closed { get; } = new();
        public Dictionary<string, string> ClosedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Reopened { get; } = new();
        public List<string> Opened { get; } = new();
        public (PowerTimerAction, TimeSpan)? TimerStarted { get; private set; }
        public List<DateTime> TimersCancelled { get; } = new();
        public List<string> Notes { get; } = new();

        public event EventHandler<PowerMode>? PowerModeApplied;

        public Task<PowerMode> GetPowerModeAsync() => Task.FromResult(Mode);

        public Task SetPowerModeAsync(PowerMode mode)
        {
            Mode = mode;
            ModesSet.Add(mode);
            PowerModeApplied?.Invoke(this, mode);   // as PowerPlanService raises it, from inside the apply
            return Task.CompletedTask;
        }

        public void ChangeModeElsewhere(PowerMode mode)
        {
            Mode = mode;
            PowerModeApplied?.Invoke(this, mode);
        }

        public IReadOnlyList<string> CloseApps(IReadOnlyList<string> names)
        {
            Closed.AddRange(names);
            return names.Select(n => ClosedPaths.TryGetValue(n, out var p) ? p : null).OfType<string>().ToList();
        }

        public void Reopen(IReadOnlyList<string> paths) => Reopened.AddRange(paths);
        public void Open(string target) => Opened.Add(target);

        public DateTime StartPowerTimer(PowerTimerAction action, TimeSpan delay)
        {
            TimerStarted = (action, delay);
            return Due;
        }

        public void CancelPowerTimer(DateTime due) => TimersCancelled.Add(due);
        public void Notify(string message) => Notes.Add(message);
    }
}
