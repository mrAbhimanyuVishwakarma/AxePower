using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Models.Routines;
using AxePower.Core.Services;
using AxePower.Core.Services.Routines;

namespace AxePower.App.ViewModels;

/// <summary>
/// Tools → Routines: the owner's if-then routines, ready-made ones to start from, and the editor.
/// Saving writes them to settings; <see cref="RoutineEngine"/> picks the change up and runs them.
/// </summary>
public partial class RoutinesViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly RoutineEngine? _engine;

    public RoutinesViewModel(SettingsService settings, RoutineEngine? engine = null)
    {
        _settings = settings;
        _engine = engine;
        if (_engine != null) _engine.RunningChanged += OnRunningChanged;
        Load();
    }

    public ObservableCollection<RoutineItem> Routines { get; } = new();
    public IReadOnlyList<RoutineTemplates.Template> Templates => RoutineTemplates.All;
    public bool HasRoutines => Routines.Count > 0;

    /// <summary>Set by the main window: goes back to the Tools page.</summary>
    public Action<string>? NavigateTo { get; set; }

    /// <summary>Asks before deleting. Tests replace it.</summary>
    public Func<string, bool> ConfirmDelete { get; set; } = name =>
        MessageBox.Show($"Delete the routine \"{name}\"?", "AxePower", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsListing))]
    private RoutineEditorViewModel? _editor;

    public bool IsEditing => Editor != null;
    public bool IsListing => Editor == null;

    [RelayCommand]
    private void Back() => NavigateTo?.Invoke("Tools");

    [RelayCommand]
    private void NewRoutine() => Editor = new RoutineEditorViewModel(new Routine
    {
        Conditions = { new RoutineCondition { Kind = RoutineConditionKind.PluggedIn } },
        Actions = { new RoutineAction { Kind = RoutineActionKind.SetPowerMode, Mode = PowerMode.Smart } }
    }, isNew: true);

    [RelayCommand]
    private void UseTemplate(RoutineTemplates.Template? template)
    {
        if (template != null) Editor = new RoutineEditorViewModel(template.Create(), isNew: true);
    }

    [RelayCommand]
    private void CancelEditor() => Editor = null;

    [RelayCommand]
    private void SaveEditor()
    {
        if (Editor == null) return;
        var routine = Editor.ToRoutine();
        var problems = routine.Problems();
        if (problems.Count > 0)
        {
            Editor.Error = string.Join(" ", problems);
            return;
        }

        Save(all =>
        {
            int index = all.FindIndex(r => r.Id == routine.Id);
            if (index >= 0) all[index] = routine;
            else all.Add(routine);
        });
        Editor = null;
    }

    internal void Edit(RoutineItem item) => Editor = new RoutineEditorViewModel(item.Routine.Clone(), isNew: false);

    internal void Delete(RoutineItem item)
    {
        if (!ConfirmDelete(item.Name)) return;
        Save(all => all.RemoveAll(r => r.Id == item.Routine.Id));
    }

    internal void SetEnabled(RoutineItem item, bool enabled) =>
        Save(all => { if (all.Find(r => r.Id == item.Routine.Id) is { } r) r.Enabled = enabled; });

    private void Save(Action<List<Routine>> change)
    {
        var settings = _settings.GetSettings();
        settings.Routines ??= new();
        change(settings.Routines);
        _settings.SaveSettings(settings);
        Load();
    }

    private void Load()
    {
        Routines.Clear();
        foreach (var routine in _settings.GetSettings().Routines ?? new())
            Routines.Add(new RoutineItem(routine, this, _engine?.IsRunning(routine.Id) == true));
        OnPropertyChanged(nameof(HasRoutines));
    }

    private void OnRunningChanged(object? sender, EventArgs e)
    {
        void Refresh()
        {
            foreach (var item in Routines) item.IsRunning = _engine?.IsRunning(item.Routine.Id) == true;
        }
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Refresh);
        else Refresh();
    }

    public void Dispose()
    {
        if (_engine != null) _engine.RunningChanged -= OnRunningChanged;
    }
}

/// <summary>One saved routine on the list.</summary>
public partial class RoutineItem : ObservableObject
{
    private readonly RoutinesViewModel _owner;
    private readonly bool _ready;

    public RoutineItem(Routine routine, RoutinesViewModel owner, bool isRunning)
    {
        Routine = routine;
        _owner = owner;
        _isEnabled = routine.Enabled;
        _isRunning = isRunning;
        _ready = true;
    }

    public Routine Routine { get; }
    public string Name => Routine.Name;
    public string Icon => string.IsNullOrEmpty(Routine.Icon) ? "" : Routine.Icon;
    public string Summary => RoutineText.Summary(Routine);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isRunning;

    public string StatusText => !IsEnabled ? "Off" : IsRunning ? "Running" : "Waiting";

    partial void OnIsEnabledChanged(bool value)
    {
        if (_ready) _owner.SetEnabled(this, value);
    }

    [RelayCommand]
    private void Edit() => _owner.Edit(this);

    [RelayCommand]
    private void Delete() => _owner.Delete(this);
}

public sealed record Choice<T>(T Value, string Label);

/// <summary>The options each dropdown in the editor offers, in words.</summary>
public static class RoutineChoices
{
    public static IReadOnlyList<Choice<RoutineConditionKind>> ConditionKinds { get; } = new[]
    {
        new Choice<RoutineConditionKind>(RoutineConditionKind.TimeBetween, "Time is between"),
        new Choice<RoutineConditionKind>(RoutineConditionKind.AppRunning, "An app is running"),
        new Choice<RoutineConditionKind>(RoutineConditionKind.PluggedIn, "Plugged in"),
        new Choice<RoutineConditionKind>(RoutineConditionKind.OnBattery, "On battery"),
        new Choice<RoutineConditionKind>(RoutineConditionKind.BatteryAtOrBelow, "Battery at or below"),
    };

    public static IReadOnlyList<Choice<RoutineDays>> Days { get; } = new[]
    {
        new Choice<RoutineDays>(RoutineDays.EveryDay, "Every day"),
        new Choice<RoutineDays>(RoutineDays.Weekdays, "Weekdays"),
        new Choice<RoutineDays>(RoutineDays.Weekends, "Weekends"),
    };

    public static IReadOnlyList<Choice<RoutineActionKind>> ActionKinds { get; } = new[]
    {
        new Choice<RoutineActionKind>(RoutineActionKind.SetPowerMode, "Set power mode"),
        new Choice<RoutineActionKind>(RoutineActionKind.CloseApps, "Close apps"),
        new Choice<RoutineActionKind>(RoutineActionKind.OpenApp, "Open an app or website"),
        new Choice<RoutineActionKind>(RoutineActionKind.StartPowerTimer, "Start the power timer"),
        new Choice<RoutineActionKind>(RoutineActionKind.ShowNotification, "Show a notification"),
    };

    public static IReadOnlyList<Choice<PowerMode>> Modes { get; } = new[]
    {
        new Choice<PowerMode>(PowerMode.Eco, "Eco"),
        new Choice<PowerMode>(PowerMode.Smart, "Smart"),
        new Choice<PowerMode>(PowerMode.Beast, "Beast"),
        new Choice<PowerMode>(PowerMode.GameMode, "Game Mode"),
    };

    public static IReadOnlyList<Choice<PowerTimerAction>> TimerActions { get; } = new[]
    {
        new Choice<PowerTimerAction>(PowerTimerAction.Sleep, "Sleep"),
        new Choice<PowerTimerAction>(PowerTimerAction.Shutdown, "Shut down"),
        new Choice<PowerTimerAction>(PowerTimerAction.Restart, "Restart"),
    };
}

/// <summary>The routine being made or changed.</summary>
public partial class RoutineEditorViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly string _icon;
    private readonly bool _enabled;

    public RoutineEditorViewModel(Routine routine, bool isNew)
    {
        _id = routine.Id;
        _icon = routine.Icon;
        _enabled = routine.Enabled;
        IsNew = isNew;
        _name = routine.Name;
        _matchAll = routine.Match == RoutineMatch.All;
        _undoWhenItEnds = routine.UndoWhenItEnds;
        foreach (var c in routine.Conditions) Conditions.Add(new ConditionRow(c, this));
        foreach (var a in routine.Actions) Actions.Add(new ActionRow(a, this));
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "New routine" : "Edit routine";

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllSelected), nameof(IsAnySelected))]
    private bool _matchAll;

    // Radio buttons: each only acts when switched on. Unchecking the other one writes false back,
    // and acting on that too would flip MatchAll forever.
    public bool IsAllSelected
    {
        get => MatchAll;
        set { if (value) MatchAll = true; }
    }

    public bool IsAnySelected
    {
        get => !MatchAll;
        set { if (value) MatchAll = false; }
    }

    [ObservableProperty]
    private bool _undoWhenItEnds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public ObservableCollection<ConditionRow> Conditions { get; } = new();
    public ObservableCollection<ActionRow> Actions { get; } = new();

    [RelayCommand]
    private void AddCondition() => Conditions.Add(new ConditionRow(new RoutineCondition { Kind = RoutineConditionKind.TimeBetween }, this));

    [RelayCommand]
    private void AddAction() => Actions.Add(new ActionRow(new RoutineAction { Kind = RoutineActionKind.ShowNotification }, this));

    internal void Remove(ConditionRow row) => Conditions.Remove(row);
    internal void Remove(ActionRow row) => Actions.Remove(row);

    public Routine ToRoutine() => new()
    {
        Id = _id,
        Icon = _icon,
        Enabled = _enabled,
        Name = (Name ?? string.Empty).Trim(),
        Match = MatchAll ? RoutineMatch.All : RoutineMatch.Any,
        UndoWhenItEnds = UndoWhenItEnds,
        Conditions = Conditions.Select(c => c.ToCondition()).ToList(),
        Actions = Actions.Select(a => a.ToAction()).ToList()
    };
}

/// <summary>One "If" in the editor.</summary>
public partial class ConditionRow : ObservableObject
{
    private readonly RoutineEditorViewModel _owner;

    public ConditionRow(RoutineCondition c, RoutineEditorViewModel owner)
    {
        _owner = owner;
        _kind = c.Kind;
        _from = c.From;
        _to = c.To;
        _days = c.Days;
        _app = c.App;
        _percentText = c.Percent.ToString();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTime), nameof(ShowApp), nameof(ShowPercent))]
    private RoutineConditionKind _kind;

    [ObservableProperty] private string _from;
    [ObservableProperty] private string _to;
    [ObservableProperty] private RoutineDays _days;
    [ObservableProperty] private string _app;
    [ObservableProperty] private string _percentText;

    public bool ShowTime => Kind == RoutineConditionKind.TimeBetween;
    public bool ShowApp => Kind == RoutineConditionKind.AppRunning;
    public bool ShowPercent => Kind == RoutineConditionKind.BatteryAtOrBelow;

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    /// <summary>Picks a program, or a shortcut to one, and keeps its file name.</summary>
    [RelayCommand]
    private void BrowseApp()
    {
        if (ProgramPicker.Pick("Pick the app to watch for") is { } exe) App = System.IO.Path.GetFileName(exe);
    }

    public RoutineCondition ToCondition() => new()
    {
        Kind = Kind,
        From = (From ?? string.Empty).Trim(),
        To = (To ?? string.Empty).Trim(),
        Days = Days,
        App = (App ?? string.Empty).Trim(),
        Percent = int.TryParse(PercentText?.Trim().TrimEnd('%'), out var p) ? p : 0
    };
}

/// <summary>One "Then" in the editor.</summary>
public partial class ActionRow : ObservableObject
{
    private readonly RoutineEditorViewModel _owner;

    public ActionRow(RoutineAction a, RoutineEditorViewModel owner)
    {
        _owner = owner;
        _kind = a.Kind;
        _mode = a.Mode;
        _appsText = string.Join(", ", a.Apps);
        _target = a.Target;
        _timerAction = a.TimerAction;
        _minutesText = a.Minutes.ToString();
        _message = a.Message;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMode), nameof(ShowApps), nameof(ShowTarget), nameof(ShowTimer), nameof(ShowMessage))]
    private RoutineActionKind _kind;

    [ObservableProperty] private PowerMode _mode;
    [ObservableProperty] private string _appsText;
    [ObservableProperty] private string _target;
    [ObservableProperty] private PowerTimerAction _timerAction;
    [ObservableProperty] private string _minutesText;
    [ObservableProperty] private string _message;

    public bool ShowMode => Kind == RoutineActionKind.SetPowerMode;
    public bool ShowApps => Kind == RoutineActionKind.CloseApps;
    public bool ShowTarget => Kind == RoutineActionKind.OpenApp;
    public bool ShowTimer => Kind == RoutineActionKind.StartPowerTimer;
    public bool ShowMessage => Kind == RoutineActionKind.ShowNotification;

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    [RelayCommand]
    private void BrowseTarget()
    {
        if (ProgramPicker.Pick("Pick the app to open") is { } exe) Target = exe;
    }

    [RelayCommand]
    private void BrowseApps()
    {
        if (ProgramPicker.Pick("Pick an app to close") is not { } exe) return;
        var name = System.IO.Path.GetFileName(exe);
        AppsText = string.IsNullOrWhiteSpace(AppsText) ? name : $"{AppsText.TrimEnd(' ', ',')}, {name}";
    }

    public RoutineAction ToAction() => new()
    {
        Kind = Kind,
        Mode = Mode,
        Apps = (AppsText ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Target = (Target ?? string.Empty).Trim(),
        TimerAction = TimerAction,
        Minutes = int.TryParse(MinutesText?.Trim(), out var m) ? m : 0,
        Message = (Message ?? string.Empty).Trim()
    };
}

/// <summary>The file picker for programs, resolving a shortcut to the program it opens.</summary>
internal static class ProgramPicker
{
    public static string? Pick(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
            DereferenceLinks = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
