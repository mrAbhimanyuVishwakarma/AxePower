using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using AxePower.Core.Services;

namespace AxePower.Core.Models.Routines;

/// <summary>Whether every condition must hold, or any one of them.</summary>
public enum RoutineMatch
{
    All,
    Any
}

public enum RoutineConditionKind
{
    TimeBetween,
    AppRunning,
    PluggedIn,
    OnBattery,
    BatteryAtOrBelow
}

public enum RoutineDays
{
    EveryDay,
    Weekdays,
    Weekends
}

public enum RoutineActionKind
{
    SetPowerMode,
    CloseApps,
    OpenApp,
    StartPowerTimer,
    ShowNotification
}

/// <summary>
/// One "If": something about the PC that is either true or not right now. A routine runs while its
/// conditions hold and ends when they stop holding (skillsMD/ROUTINES_PLAN.md).
/// </summary>
public sealed class RoutineCondition
{
    [JsonPropertyName("kind")]
    public RoutineConditionKind Kind { get; set; }

    /// <summary>TimeBetween: start, as "HH:mm". A range that ends before it starts runs past midnight.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = "23:00";

    /// <summary>TimeBetween: end, as "HH:mm", not included.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "07:00";

    /// <summary>TimeBetween: the days the range starts on.</summary>
    [JsonPropertyName("days")]
    public RoutineDays Days { get; set; } = RoutineDays.EveryDay;

    /// <summary>AppRunning: the program's file name, with or without ".exe".</summary>
    [JsonPropertyName("app")]
    public string App { get; set; } = string.Empty;

    /// <summary>BatteryAtOrBelow: 1–100.</summary>
    [JsonPropertyName("percent")]
    public int Percent { get; set; } = 20;

    public RoutineCondition Clone() => (RoutineCondition)MemberwiseClone();
}

/// <summary>One "Then": something AxePower does when the routine starts.</summary>
public sealed class RoutineAction
{
    [JsonPropertyName("kind")]
    public RoutineActionKind Kind { get; set; }

    /// <summary>SetPowerMode.</summary>
    [JsonPropertyName("mode")]
    public PowerMode Mode { get; set; } = PowerMode.Eco;

    /// <summary>CloseApps: program file names, with or without ".exe".</summary>
    [JsonPropertyName("apps")]
    public List<string> Apps { get; set; } = new();

    /// <summary>OpenApp: a program, file or web address.</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>StartPowerTimer.</summary>
    [JsonPropertyName("timerAction")]
    public PowerTimerAction TimerAction { get; set; } = PowerTimerAction.Sleep;

    /// <summary>StartPowerTimer: 1 minute to 24 hours.</summary>
    [JsonPropertyName("minutes")]
    public int Minutes { get; set; } = 10;

    /// <summary>ShowNotification.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    public RoutineAction Clone()
    {
        var copy = (RoutineAction)MemberwiseClone();
        copy.Apps = new List<string>(Apps ?? new());
        return copy;
    }
}

/// <summary>If these conditions hold, then do these actions; optionally put things back when they stop holding.</summary>
public sealed class Routine
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>A Segoe MDL2 Assets glyph for the routine's card.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("match")]
    public RoutineMatch Match { get; set; } = RoutineMatch.All;

    [JsonPropertyName("conditions")]
    public List<RoutineCondition> Conditions { get; set; } = new();

    [JsonPropertyName("actions")]
    public List<RoutineAction> Actions { get; set; } = new();

    /// <summary>When the conditions stop holding: the previous power mode, reopened apps, a cancelled timer.</summary>
    [JsonPropertyName("undoWhenItEnds")]
    public bool UndoWhenItEnds { get; set; } = true;

    public Routine Clone() => new()
    {
        Id = Id,
        Name = Name,
        Icon = Icon,
        Enabled = Enabled,
        Match = Match,
        Conditions = (Conditions ?? new()).Select(c => c.Clone()).ToList(),
        Actions = (Actions ?? new()).Select(a => a.Clone()).ToList(),
        UndoWhenItEnds = UndoWhenItEnds
    };

    /// <summary>What stops this routine being saved, in words for the editor. Empty when it can be saved.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("Give the routine a name.");
        if (Conditions.Count == 0) problems.Add("Add at least one \"If\".");
        if (Actions.Count == 0) problems.Add("Add at least one \"Then\".");

        foreach (var c in Conditions)
        {
            switch (c.Kind)
            {
                case RoutineConditionKind.TimeBetween:
                    if (RoutineTime.Parse(c.From) is null || RoutineTime.Parse(c.To) is null)
                        problems.Add("Write times as hours and minutes, like 23:00.");
                    break;
                case RoutineConditionKind.AppRunning:
                    if (string.IsNullOrWhiteSpace(c.App)) problems.Add("Pick the app to watch for.");
                    break;
                case RoutineConditionKind.BatteryAtOrBelow:
                    if (c.Percent is < 1 or > 100) problems.Add("Battery level must be between 1 and 100 %.");
                    break;
            }
        }

        foreach (var a in Actions)
        {
            switch (a.Kind)
            {
                case RoutineActionKind.CloseApps:
                    if (a.Apps.Count(n => !string.IsNullOrWhiteSpace(n)) == 0) problems.Add("List the apps to close.");
                    break;
                case RoutineActionKind.OpenApp:
                    if (string.IsNullOrWhiteSpace(a.Target)) problems.Add("Pick the app, file or website to open.");
                    break;
                case RoutineActionKind.StartPowerTimer:
                    if (a.Minutes is < 1 or > 24 * 60) problems.Add("The timer must be 1 minute to 24 hours.");
                    break;
                case RoutineActionKind.ShowNotification:
                    if (string.IsNullOrWhiteSpace(a.Message)) problems.Add("Write the notification's text.");
                    break;
            }
        }

        return problems.Distinct().ToList();
    }
}

/// <summary>Times of day as the routines store them: "HH:mm", 24-hour.</summary>
public static class RoutineTime
{
    public static TimeOnly? Parse(string? text) =>
        TimeOnly.TryParseExact((text ?? string.Empty).Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;
}
