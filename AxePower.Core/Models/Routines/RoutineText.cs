using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Services;

namespace AxePower.Core.Models.Routines;

/// <summary>Routines in words, for their cards: "If Steam is running → Beast mode, close Discord".</summary>
public static class RoutineText
{
    public static string Describe(RoutineCondition c) => c.Kind switch
    {
        RoutineConditionKind.TimeBetween => $"{c.From}–{c.To}" + c.Days switch
        {
            RoutineDays.Weekdays => " on weekdays",
            RoutineDays.Weekends => " at weekends",
            _ => string.Empty
        },
        RoutineConditionKind.AppRunning => $"{AppName(c.App)} is running",
        RoutineConditionKind.PluggedIn => "plugged in",
        RoutineConditionKind.OnBattery => "on battery",
        RoutineConditionKind.BatteryAtOrBelow => $"battery at or below {c.Percent}%",
        _ => c.Kind.ToString()
    };

    public static string Describe(RoutineAction a) => a.Kind switch
    {
        RoutineActionKind.SetPowerMode => $"{ModeName(a.Mode)} mode",
        RoutineActionKind.CloseApps => "close " + string.Join(", ", a.Apps.Where(n => !string.IsNullOrWhiteSpace(n)).Select(AppName)),
        RoutineActionKind.OpenApp => $"open {ShortTarget(a.Target)}",
        RoutineActionKind.StartPowerTimer => $"{TimerVerb(a.TimerAction)} in {a.Minutes} min",
        RoutineActionKind.ShowNotification => "notify",
        _ => a.Kind.ToString()
    };

    public static string Summary(Routine r)
    {
        string join = r.Match == RoutineMatch.All ? " and " : " or ";
        string conditions = r.Conditions.Count == 0 ? "…" : string.Join(join, r.Conditions.Select(Describe));
        string actions = r.Actions.Count == 0 ? "…" : string.Join(", ", r.Actions.Select(Describe));
        return $"If {conditions} → {actions}";
    }

    public static string ModeName(PowerMode mode) => mode == PowerMode.GameMode ? "Game" : mode.ToString();

    /// <summary>"steam.exe" → "Steam".</summary>
    public static string AppName(string app)
    {
        var name = NormalizeApp(app);
        return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>A program's name as Windows lists running processes: no folder, no ".exe".</summary>
    public static string NormalizeApp(string? app)
    {
        var name = (app ?? string.Empty).Trim().Trim('"');
        int slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
        if (slash >= 0) name = name[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string ShortTarget(string target)
    {
        var t = (target ?? string.Empty).Trim().Trim('"');
        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.Host;
        return AppName(t);
    }

    private static string TimerVerb(PowerTimerAction action) => action switch
    {
        PowerTimerAction.Restart => "restart",
        PowerTimerAction.Sleep => "sleep",
        _ => "shut down"
    };
}

/// <summary>Ready-made routines to start from. Each opens in the editor, so the owner can adjust it first.</summary>
public static class RoutineTemplates
{
    public sealed record Template(string Id, string Name, string Icon, string Description, Func<Routine> Create);

    public static IReadOnlyList<Template> All { get; } = new[]
    {
        new Template("gaming", "Gaming", "", "A game starts → Beast mode, close background apps", () => new Routine
        {
            Name = "Gaming",
            Icon = "",
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.AppRunning } },
            Actions =
            {
                new RoutineAction { Kind = RoutineActionKind.SetPowerMode, Mode = PowerMode.Beast },
                new RoutineAction { Kind = RoutineActionKind.CloseApps, Apps = { "Discord" } }
            }
        }),
        new Template("battery-saver", "Battery saver", "", "On battery at 20% or less → Eco mode", () => new Routine
        {
            Name = "Battery saver",
            Icon = "",
            Conditions =
            {
                new RoutineCondition { Kind = RoutineConditionKind.OnBattery },
                new RoutineCondition { Kind = RoutineConditionKind.BatteryAtOrBelow, Percent = 20 }
            },
            Actions = { new RoutineAction { Kind = RoutineActionKind.SetPowerMode, Mode = PowerMode.Eco } }
        }),
        new Template("night", "Night", "", "23:00–07:00 → Eco mode", () => new Routine
        {
            Name = "Night",
            Icon = "",
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.TimeBetween, From = "23:00", To = "07:00" } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.SetPowerMode, Mode = PowerMode.Eco } }
        }),
        new Template("bedtime", "Bedtime", "", "01:00–05:00 → sleep in 10 min, with the timer's warning", () => new Routine
        {
            Name = "Bedtime",
            Icon = "",
            Conditions = { new RoutineCondition { Kind = RoutineConditionKind.TimeBetween, From = "01:00", To = "05:00" } },
            Actions = { new RoutineAction { Kind = RoutineActionKind.StartPowerTimer, TimerAction = PowerTimerAction.Sleep, Minutes = 10 } }
        }),
    };
}
