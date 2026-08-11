using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using System.Collections.ObjectModel;

namespace AxePower.App.ViewModels;

public partial class AutoSwitchViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;

    [ObservableProperty]
    private bool _autoSwitchEnabled;

    [ObservableProperty]
    private PowerMode _pluggedInMode;

    [ObservableProperty]
    private PowerMode _unpluggedMode;

    [ObservableProperty]
    private PowerMode _lowBatteryMode;

    [ObservableProperty]
    private int _lowBatteryThresholdPercent;

    [ObservableProperty]
    private bool _suspendBackgroundAppsOnBattery;

    public ObservableCollection<PowerMode> AvailableModes { get; } = new(new[] { PowerMode.Eco, PowerMode.Smart, PowerMode.Beast });

    [ObservableProperty]
    private bool _customRuleEnabled;

    [ObservableProperty]
    private bool _showNotifications;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _pluggedInEnabled = true;

    [ObservableProperty]
    private bool _unpluggedEnabled = true;

    [ObservableProperty]
    private bool _lowBatteryEnabled = true;

    public AutoSwitchViewModel(
        SettingsService settingsService, 
        NotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();
        AutoSwitchEnabled = settings.AutoSwitchEnabled;
        PluggedInMode = settings.PluggedInMode;
        UnpluggedMode = settings.UnpluggedMode;
        LowBatteryMode = settings.LowBatteryMode;
        LowBatteryThresholdPercent = settings.LowBatteryThresholdPercent;
        SuspendBackgroundAppsOnBattery = settings.SuspendBackgroundAppsOnBattery;
        ShowNotifications = settings.ShowNotifications;
        StartWithWindows = settings.StartWithWindows;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var settings = _settingsService.GetSettings();
        settings.AutoSwitchEnabled = AutoSwitchEnabled;
        settings.PluggedInMode = PluggedInMode;
        settings.UnpluggedMode = UnpluggedMode;
        settings.LowBatteryMode = LowBatteryMode;
        settings.LowBatteryThresholdPercent = LowBatteryThresholdPercent;
        settings.SuspendBackgroundAppsOnBattery = SuspendBackgroundAppsOnBattery;
        settings.ShowNotifications = ShowNotifications;
        settings.StartWithWindows = StartWithWindows;

        _settingsService.SaveSettings(settings);
        _notificationService.Show("Auto Switch settings saved successfully.");
    }

    [RelayCommand]
    private void OpenHelp()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/mrAbhimanyuVishwakarma/AxePower/blob/main/README.md",
            UseShellExecute = true
        });
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
