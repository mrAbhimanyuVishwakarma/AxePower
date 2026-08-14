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
    private PowerMode _selectedMode = PowerMode.Smart;

    // Settings Bindings
    [ObservableProperty]
    private bool _autoSwitchEnabled;

    [ObservableProperty]
    private PowerMode _pluggedInMode;

    [ObservableProperty]
    private PowerMode _pluggedOutMode;

    [ObservableProperty]
    private PowerMode _lowBatteryMode;

    [ObservableProperty]
    private bool _showNotifications;

    [ObservableProperty]
    private bool _startWithWindows;

    public ObservableCollection<PowerMode> AvailableModes { get; } = new(new[] { PowerMode.Eco, PowerMode.Smart, PowerMode.Beast, PowerMode.GameMode });

    public ObservableCollection<double> BatteryTrendPoints { get; } = new();

    public DashboardViewModel(
        PowerPlanService powerPlanService, 
        BatteryService batteryService,
        NotificationService notificationService,
        SettingsService settingsService,
        AppKillerService appKillerService)
    {
        _powerPlanService = powerPlanService;
        _batteryService = batteryService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _appKillerService = appKillerService;

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
    }

    partial void OnAutoSwitchEnabledChanged(bool value) => SaveSettings();
    partial void OnPluggedInModeChanged(PowerMode value) => SaveSettings();
    partial void OnPluggedOutModeChanged(PowerMode value) => SaveSettings();
    partial void OnLowBatteryModeChanged(PowerMode value) => SaveSettings();
    partial void OnShowNotificationsChanged(bool value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();

    private void SaveSettings()
    {
        var settings = _settingsService.GetSettings();
        settings.AutoSwitchEnabled = AutoSwitchEnabled;
        settings.PluggedInMode = PluggedInMode;
        settings.UnpluggedMode = PluggedOutMode;
        settings.LowBatteryMode = LowBatteryMode;
        settings.ShowNotifications = ShowNotifications;
        settings.StartWithWindows = StartWithWindows;
        _settingsService.SaveSettings(settings);
    }

    private async Task RefreshStatusAsync()
    {
        var status = _batteryService.GetCurrentStatus();
        BatteryPercentage = status.HasBattery ? $"{status.BatteryPercentage}%" : "100%";
        PluggedInStatus = status.IsPluggedIn ? "Plugged In" : "On Battery";

        var mode = await _powerPlanService.GetActivePowerModeAsync();
        CurrentMode = mode switch
        {
            PowerMode.Eco => "Eco",
            PowerMode.Smart => "Smart",
            PowerMode.Beast => "Beast",
            _ => mode.ToString()
        };
        SelectedMode = mode;
        
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
            var settings = _settingsService.GetSettings();
            settings.LastSelectedPowerMode = mode;
            _settingsService.SaveSettings(settings);
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
                await RefreshStatusAsync();
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
    private void CheckForUpdates()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases",
            UseShellExecute = true
        });
    }
}
