using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.App.Services;

namespace AxePower.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly TimeoutService _timeoutService;
    private readonly ThemeService _themeService;

    [ObservableProperty]
    private AppSettings _currentSettings;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.Theme = value;
            _themeService.ApplyTheme(value);
        }
    }

    [ObservableProperty]
    private int _monitorTimeoutAc;

    [ObservableProperty]
    private int _monitorTimeoutDc;

    [ObservableProperty]
    private int _standbyTimeoutAc;

    [ObservableProperty]
    private int _standbyTimeoutDc;

    public IEnumerable<PowerMode> AvailableModes => Enum.GetValues(typeof(PowerMode)).Cast<PowerMode>();
    public IEnumerable<AppTheme> AvailableThemes => Enum.GetValues(typeof(AppTheme)).Cast<AppTheme>();
    public IEnumerable<CloseBehaviour> AvailableCloseBehaviours => Enum.GetValues(typeof(CloseBehaviour)).Cast<CloseBehaviour>();

    public SettingsViewModel(SettingsService settingsService, NotificationService notificationService, TimeoutService timeoutService, ThemeService themeService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _timeoutService = timeoutService;
        _themeService = themeService;
        _currentSettings = _settingsService.GetSettings();
        _selectedTheme = _currentSettings.Theme;
        _ = LoadTimeoutsAsync();
    }

    private async Task LoadTimeoutsAsync()
    {
        var monitor = await _timeoutService.GetMonitorTimeoutsAsync();
        MonitorTimeoutAc = monitor.acMinutes;
        MonitorTimeoutDc = monitor.dcMinutes;

        var standby = await _timeoutService.GetStandbyTimeoutsAsync();
        StandbyTimeoutAc = standby.acMinutes;
        StandbyTimeoutDc = standby.dcMinutes;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _settingsService.SaveSettings(CurrentSettings);
        
        await _timeoutService.SetMonitorTimeoutAsync(MonitorTimeoutAc, true);
        await _timeoutService.SetMonitorTimeoutAsync(MonitorTimeoutDc, false);
        await _timeoutService.SetStandbyTimeoutAsync(StandbyTimeoutAc, true);
        await _timeoutService.SetStandbyTimeoutAsync(StandbyTimeoutDc, false);

        _notificationService.Show("Settings saved.");
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        CurrentSettings = new AppSettings();
        SelectedTheme = CurrentSettings.Theme;
        _notificationService.Show("Defaults restored. Click Save to apply.");
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
