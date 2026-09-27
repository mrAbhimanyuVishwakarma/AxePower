using System;
using System.Windows;
using System.Windows.Threading;
using AxePower.App.Views;
using AxePower.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels;

/// <summary>
/// The shutdown / restart / sleep timer in the window's bottom-right corner. The countdown
/// itself lives in <see cref="PowerTimerService"/>; this refreshes the display once a second,
/// turns it red for the last two minutes and shows the on-screen warning.
/// </summary>
public partial class PowerTimerViewModel : ObservableObject, IDisposable
{
    private readonly PowerTimerService _timer;
    private readonly DispatcherTimer _refresh;

    public PowerTimerViewModel(PowerTimerService timer)
    {
        _timer = timer;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => Refresh();
        _timer.Changed += OnTimerChanged;
        _timer.WarningReached += OnWarningReached;
        Refresh();
    }

    // ── Setting it up ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShutdownSelected), nameof(IsRestartSelected), nameof(IsSleepSelected))]
    private PowerTimerAction _selectedAction = PowerTimerAction.Shutdown;

    public bool IsShutdownSelected
    {
        get => SelectedAction == PowerTimerAction.Shutdown;
        set { if (value) SelectedAction = PowerTimerAction.Shutdown; }
    }

    public bool IsRestartSelected
    {
        get => SelectedAction == PowerTimerAction.Restart;
        set { if (value) SelectedAction = PowerTimerAction.Restart; }
    }

    public bool IsSleepSelected
    {
        get => SelectedAction == PowerTimerAction.Sleep;
        set { if (value) SelectedAction = PowerTimerAction.Sleep; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputError))]
    private string _hoursText = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputError))]
    private string _minutesText = "30";

    /// <summary>Why Start is refused, or null when the typed time is usable.</summary>
    public string? InputError => TryReadDelay(out _, out string? error) ? null : error;

    [ObservableProperty]
    private bool _isFlyoutOpen;

    [RelayCommand]
    private void ToggleFlyout() => IsFlyoutOpen = !IsFlyoutOpen;

    [RelayCommand]
    private void Preset(string? minutes)
    {
        if (!int.TryParse(minutes, out int total)) return;
        HoursText = (total / 60).ToString();
        MinutesText = (total % 60).ToString();
    }

    [RelayCommand]
    private void Start()
    {
        if (!TryReadDelay(out var delay, out _)) return;
        _timer.Start(SelectedAction, delay);
        IsFlyoutOpen = false;
    }

    [RelayCommand]
    private void Cancel() => _timer.Cancel();

    [RelayCommand]
    private void AddTenMinutes() => _timer.Extend(TimeSpan.FromMinutes(10));

    /// <summary>Hours and minutes as typed: whole numbers, 1 minute to 24 hours in total.</summary>
    private bool TryReadDelay(out TimeSpan delay, out string? error)
    {
        delay = TimeSpan.Zero;
        error = null;
        int hours = 0, minutes = 0;
        if ((!string.IsNullOrWhiteSpace(HoursText) && !int.TryParse(HoursText.Trim(), out hours))
            || (!string.IsNullOrWhiteSpace(MinutesText) && !int.TryParse(MinutesText.Trim(), out minutes))
            || hours < 0 || minutes < 0)
        {
            error = "Type whole numbers of hours and minutes.";
            return false;
        }

        var total = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        if (total < PowerTimerService.Shortest)
        {
            error = "Set at least 1 minute.";
            return false;
        }
        if (total > PowerTimerService.Longest)
        {
            error = "The longest timer is 24 hours.";
            return false;
        }

        delay = total;
        return true;
    }

    // ── While it runs ───────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Red for the last two minutes.</summary>
    [ObservableProperty]
    private bool _isWarning;

    [ObservableProperty]
    private string _countdownText = string.Empty;

    [ObservableProperty]
    private string _runningLabel = string.Empty;

    [ObservableProperty]
    private string _runningGlyph = "\uE7E8";

    public string ButtonToolTip => IsRunning
        ? $"{RunningLabel} in {CountdownText}. Click to change or cancel."
        : "Power timer: shut down, restart or sleep after a set time";

    private void OnTimerChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(Refresh);

    private void OnWarningReached(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Refresh();
            PowerTimerWarningWindow.ShowFor(WarningMessage(_timer.Action), TimeSpan.FromSeconds(5), OpenFromWarning);
        });

    /// <summary>The warning's "Extend time in AxePower" line: bring AxePower forward with the timer open.</summary>
    private void OpenFromWarning()
    {
        (Application.Current?.MainWindow as MainWindow)?.BringToFront();
        IsFlyoutOpen = true;
    }

    private void Refresh()
    {
        IsRunning = _timer.IsRunning;
        IsWarning = _timer.IsInWarning;
        RunningLabel = Label(_timer.Action);
        RunningGlyph = Glyph(_timer.Action);
        CountdownText = Format(_timer.Remaining);
        OnPropertyChanged(nameof(ButtonToolTip));

        if (IsRunning) _refresh.Start();
        else _refresh.Stop();
    }

    internal static string Format(TimeSpan left) => left.TotalHours >= 1
        ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
        : $"{left.Minutes}:{left.Seconds:00}";

    internal static string WarningMessage(PowerTimerAction action) => action switch
    {
        PowerTimerAction.Restart => "System restarting in 2 minutes",
        PowerTimerAction.Sleep => "System going to sleep in 2 minutes",
        _ => "System shutting down in 2 minutes"
    };

    private static string Label(PowerTimerAction action) => action switch
    {
        PowerTimerAction.Restart => "Restart",
        PowerTimerAction.Sleep => "Sleep",
        _ => "Shut down"
    };

    private static string Glyph(PowerTimerAction action) => action switch
    {
        PowerTimerAction.Restart => "\uE777",
        PowerTimerAction.Sleep => "\uE708",
        _ => "\uE7E8"
    };

    public void Dispose()
    {
        _refresh.Stop();
        _timer.Changed -= OnTimerChanged;
        _timer.WarningReached -= OnWarningReached;
    }
}
