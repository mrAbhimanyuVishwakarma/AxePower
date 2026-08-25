using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly PowerPlanService _powerPlanService;
    private readonly BatteryService _batteryService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService _settingsService;
    private readonly AppKillerService _appKillerService;
    private readonly UpdateService? _updateService;
    private readonly ControllerNavigationService? _controllerService;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private string _batteryPercentage = "0%";

    [ObservableProperty]
    private string _pluggedInStatus = "Unknown";

    [ObservableProperty]
    private string _currentMode = "Unknown";

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _applyStatusText = "Apply Mode";

    [ObservableProperty]
    private PowerMode _selectedMode = PowerMode.Beast;

    // Settings Bindings
    [ObservableProperty]
    private bool _autoSwitchEnabled;

    [ObservableProperty]
    private PowerMode _pluggedInMode = PowerMode.Smart;

    [ObservableProperty]
    private PowerMode _pluggedOutMode = PowerMode.Eco;

    [ObservableProperty]
    private PowerMode _lowBatteryMode = PowerMode.Eco;

    [ObservableProperty]
    private bool _showNotifications;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _isControllerNavigationEnabled;

    [ObservableProperty]
    private bool _isControllerBusy;

    [ObservableProperty]
    private bool _isFnLockEnabled;

    [ObservableProperty]
    private int _rawBatteryPercentage = 100;

    public ObservableCollection<PowerMode> AvailableModes { get; } = new(new[] { PowerMode.Eco, PowerMode.Smart, PowerMode.Beast, PowerMode.GameMode });

    public ObservableCollection<double> BatteryTrendPoints { get; } = new();

    public DashboardViewModel(
        PowerPlanService powerPlanService, 
        BatteryService batteryService,
        NotificationService notificationService,
        SettingsService settingsService,
        AppKillerService appKillerService,
        ControllerNavigationService? controllerService = null,
        UpdateService? updateService = null)
    {
        _powerPlanService = powerPlanService;
        _batteryService = batteryService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _appKillerService = appKillerService;
        _controllerService = controllerService;
        _updateService = updateService;

        if (_controllerService != null)
        {
            IsControllerNavigationEnabled = _controllerService.IsRunning();
        }

        LoadSettings();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (s, e) => await RefreshStatusAsync();
        _timer.Start();

        _ = RefreshStatusAsync();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();
        AutoSwitchEnabled = settings.AutoSwitchEnabled;
        PluggedInMode = settings.PluggedInMode;
        PluggedOutMode = settings.UnpluggedMode;
        LowBatteryMode = settings.LowBatteryMode;
        ShowNotifications = settings.ShowNotifications;
        StartWithWindows = settings.StartWithWindows;
        IsFnLockEnabled = settings.FnLockEnabled;
    }

    partial void OnAutoSwitchEnabledChanged(bool value) => SaveSettings();
    partial void OnPluggedInModeChanged(PowerMode value) => SaveSettings();
    partial void OnPluggedOutModeChanged(PowerMode value) => SaveSettings();
    partial void OnLowBatteryModeChanged(PowerMode value) => SaveSettings();
    partial void OnShowNotificationsChanged(bool value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();
    partial void OnIsFnLockEnabledChanged(bool value)
    {
        _batteryService.SetFnLock(value);
        SaveSettings();
        _notificationService.Show(
            value ? "Function lock enabled (F1–F12 special keys locked)." : "Function lock disabled.");
    }

    private void SaveSettings()
    {
        var settings = _settingsService.GetSettings();
        settings.AutoSwitchEnabled = AutoSwitchEnabled;
        settings.PluggedInMode = PluggedInMode;
        settings.UnpluggedMode = PluggedOutMode;
        settings.LowBatteryMode = LowBatteryMode;
        settings.ShowNotifications = ShowNotifications;
        settings.StartWithWindows = StartWithWindows;
        settings.FnLockEnabled = IsFnLockEnabled;
        _settingsService.SaveSettings(settings);
    }

    private bool _hasInitializedMode = false;

    private async Task RefreshStatusAsync(bool syncSelectedMode = false)
    {
        var status = _batteryService.GetCurrentStatus();
        RawBatteryPercentage = status.HasBattery ? status.BatteryPercentage : 100;
        BatteryPercentage = $"{RawBatteryPercentage}%";
        PluggedInStatus = status.IsPluggedIn ? "Plugged In" : "On Battery";

        var mode = await _powerPlanService.GetActivePowerModeAsync();
        CurrentMode = mode switch
        {
            PowerMode.Eco => "Eco",
            PowerMode.Smart => "Smart",
            PowerMode.Beast => "Beast",
            _ => mode.ToString()
        };

        if (!_hasInitializedMode || syncSelectedMode)
        {
            SelectedMode = mode;
            _hasInitializedMode = true;
        }
        
        // Update trend
        var history = await _batteryService.GetHistoryAsync();
        BatteryTrendPoints.Clear();
        foreach (var point in history)
        {
            BatteryTrendPoints.Add(point.Percentage);
        }
    }

    [RelayCommand]
    private void SelectMode(string modeStr)
    {
        if (Enum.TryParse<PowerMode>(modeStr, out var mode))
        {
            SelectedMode = mode;
        }
    }

    [RelayCommand]
    private async Task ApplyModeAsync()
    {
        IsLoading = true;
        ApplyStatusText = "Applying...";
        try
        {
            var success = await _powerPlanService.ApplyModeAsync(SelectedMode);
            if (success)
            {
                await RefreshStatusAsync(syncSelectedMode: true);
                var settings = _settingsService.GetSettings();
                settings.LastSelectedPowerMode = SelectedMode;
                _settingsService.SaveSettings(settings);
                _notificationService.Show($"{SelectedMode} applied successfully.");
            }
            else
            {
                _notificationService.Show($"Failed to apply {SelectedMode}.");
            }
        }
        finally
        {
            IsLoading = false;
            ApplyStatusText = "Apply Mode";
        }
    }

    [ObservableProperty]
    private bool _appsSuspended = false;

    [RelayCommand]
    private void ToggleBackgroundApps()
    {
        if (AppsSuspended)
        {
            int resumed = _appKillerService.ResumeBackgroundApps();
            _notificationService.Show($"Resumed {resumed} app(s).");
            AppsSuspended = false;
        }
        else
        {
            int suspended = _appKillerService.SuspendBackgroundApps();
            _notificationService.Show($"Suspended {suspended} app(s).");
            AppsSuspended = true;
        }
    }

    [RelayCommand]
    private void OpenHelp()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.OpenToolHelp(null);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateService == null)
        {
            _notificationService.Show("Already Updated");
            return;
        }

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (result.HasUpdate)
            {
                _notificationService.Show($"🚀 New update available: v{result.LatestVersion}! Check About page.");
            }
            else
            {
                _notificationService.Show("Already Updated");
            }
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to check for updates: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenControllerHelp()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.OpenToolHelp("Gopher360");
        }
    }

    [RelayCommand]
    public async Task ToggleControllerNavigationAsync()
    {
        if (_controllerService == null || IsControllerBusy) return;

        IsControllerBusy = true;
        try
        {
            var newState = await _controllerService.ToggleAsync();
            IsControllerNavigationEnabled = newState == ControllerNavigationState.Running;
            if (IsControllerNavigationEnabled)
            {
                _notificationService.Show("Controller Navigation Enabled (Gopher360)");
            }
            else
            {
                _notificationService.Show("Controller Navigation Disabled");
            }
        }
        catch (Exception ex)
        {
            _notificationService.Show($"Controller Navigation Error: {ex.Message}");
        }
        finally
        {
            IsControllerBusy = false;
        }
    }
}
