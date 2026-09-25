using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using AxePower.App.Services;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// The Shortcuts section of Music Settings: the binding for each action, whether they work
/// outside AxePower, and which ones Windows refused.
/// </summary>
public partial class ShortcutsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private bool _loading;

    public ShortcutsViewModel(SettingsService settings, HotkeyService hotkeys)
    {
        _settings = settings;
        _hotkeys = hotkeys;

        foreach (ShortcutAction action in Enum.GetValues<ShortcutAction>())
            Shortcuts.Add(new ShortcutRowViewModel(action, this));

        _hotkeys.ConflictsChanged += (_, _) => Application.Current?.Dispatcher?.BeginInvoke(RefreshConflicts);

        Load();
    }

    public ObservableCollection<ShortcutRowViewModel> Shortcuts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalHotkeysNote))]
    private bool _globalHotkeysEnabled;

    [ObservableProperty] private int _seekSeconds = 5;
    [ObservableProperty] private int _volumeStepPercent = 5;

    /// <summary>The row currently waiting for a key press, if any.</summary>
    [ObservableProperty]
    private ShortcutRowViewModel? _recording;

    public string GlobalHotkeysNote => GlobalHotkeysEnabled
        ? "These shortcuts work even when AxePower is not the active window."
        : "Shortcuts are off. Turn this on to control playback from any application.";

    partial void OnGlobalHotkeysEnabledChanged(bool value) => Save(s => s.GlobalHotkeysEnabled = value);
    partial void OnSeekSecondsChanged(int value) => Save(s => s.SeekSeconds = Math.Clamp(value, 1, 60));
    partial void OnVolumeStepPercentChanged(int value) => Save(s => s.VolumeStepPercent = Math.Clamp(value, 1, 25));

    /// <summary>Starts listening for the next key press, which becomes the row's binding.</summary>
    public void BeginRecording(ShortcutRowViewModel row)
    {
        if (Recording is { } previous && previous != row) previous.IsRecording = false;
        Recording = row;
        row.IsRecording = true;
        row.Error = null;
    }

    public void CancelRecording()
    {
        if (Recording is not { } row) return;
        row.IsRecording = false;
        Recording = null;
    }

    /// <summary>
    /// Records a pressed combination. A press that is only modifiers is ignored, so holding
    /// Ctrl before the real key does not end the recording early.
    /// </summary>
    public void RecordKey(Key key, ModifierKeys modifiers)
    {
        if (Recording is not { } row) return;

        if (key is Key.Escape)
        {
            CancelRecording();
            return;
        }

        // Backspace on an empty combination clears the binding instead of setting one.
        if (key is Key.Back && modifiers == ModifierKeys.None)
        {
            Apply(row, string.Empty);
            return;
        }

        if (IsModifier(key)) return;

        var shortcut = new Shortcut(Convert(modifiers), key.ToString());
        if (shortcut.Modifiers == ShortcutModifiers.None)
        {
            row.Error = "Use at least one of Ctrl, Alt, Shift or Win.";
            return;
        }

        Apply(row, shortcut.ToString());
    }

    private void Apply(ShortcutRowViewModel row, string binding)
    {
        var shortcuts = Current();

        if (shortcuts.FindConflict(row.Action, binding) is { } clash)
        {
            // The old binding is kept: silently stealing a shortcut from another action would
            // leave that one unbound without saying so.
            row.Error = $"Already used by “{ShortcutSettings.Describe(clash)}”.";
            return;
        }

        Save(s => s.Set(row.Action, binding));
        row.IsRecording = false;
        Recording = null;
        Load();
    }

    [RelayCommand]
    private void ResetOne(ShortcutRowViewModel? row)
    {
        if (row is null) return;
        Save(s => s.Reset(row.Action));
        Load();
    }

    [RelayCommand]
    private void ResetAll()
    {
        Save(s => s.ResetAll());
        Load();
    }

    private ShortcutSettings Current() => _settings.GetSettings().Music.Shortcuts ?? new ShortcutSettings();

    private void Load()
    {
        _loading = true;
        try
        {
            var shortcuts = Current();
            GlobalHotkeysEnabled = shortcuts.GlobalHotkeysEnabled;
            SeekSeconds = shortcuts.SeekSeconds;
            VolumeStepPercent = shortcuts.VolumeStepPercent;

            foreach (var row in Shortcuts) row.Binding = shortcuts.Resolve(row.Action);
        }
        finally { _loading = false; }

        RefreshConflicts();
    }

    private void RefreshConflicts()
    {
        var byAction = _hotkeys.Conflicts.ToDictionary(c => c.Action, c => c.Reason);
        foreach (var row in Shortcuts)
            row.SystemConflict = byAction.TryGetValue(row.Action, out var reason) ? reason : null;
    }

    private void Save(Action<ShortcutSettings> change)
    {
        if (_loading) return;

        var settings = _settings.GetSettings();
        settings.Music.Shortcuts ??= new ShortcutSettings();
        change(settings.Music.Shortcuts);
        _settings.SaveSettings(settings);
    }

    private static bool IsModifier(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    private static ShortcutModifiers Convert(ModifierKeys modifiers)
    {
        var result = ShortcutModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ShortcutModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ShortcutModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= ShortcutModifiers.Windows;
        return result;
    }
}

/// <summary>One action and what it is bound to.</summary>
public partial class ShortcutRowViewModel : ObservableObject
{
    private readonly ShortcutsViewModel _owner;

    public ShortcutRowViewModel(ShortcutAction action, ShortcutsViewModel owner)
    {
        Action = action;
        _owner = owner;
    }

    public ShortcutAction Action { get; }
    public string Name => ShortcutSettings.Describe(Action);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBinding))]
    private string _binding = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBinding))]
    private bool _isRecording;

    [ObservableProperty]
    private string? _error;

    /// <summary>The reason Windows gave for refusing this shortcut, if it did.</summary>
    [ObservableProperty]
    private string? _systemConflict;

    public string DisplayBinding => IsRecording
        ? "Press a key…"
        : string.IsNullOrWhiteSpace(Binding) ? "Not set" : Binding;

    [RelayCommand]
    private void Record() => _owner.BeginRecording(this);
}
