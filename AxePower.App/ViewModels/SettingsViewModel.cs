using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly DisplayService _displayService;
    private readonly PowerPlanService _powerPlanService;

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
    private string _selectedRefreshRate = "Auto (Smart)";

    public ObservableCollection<string> AvailableRefreshRates { get; } = new();

    private bool _isInitializingRefreshRates = false;

    public ObservableCollection<string> AvailableTemperatureUnits { get; } = new()
    {
        "Celsius (°C)",
        "Fahrenheit (°F)"
    };

    [ObservableProperty]
    private string _selectedTemperatureUnit = "Celsius (°C)";

    private bool _isInitializingTemperatureUnit = false;

    partial void OnSelectedTemperatureUnitChanged(string value)
    {
        if (_isInitializingTemperatureUnit || CurrentSettings == null) return;

        var unit = value.Contains("Fahrenheit", StringComparison.OrdinalIgnoreCase) || value.Contains("°F")
            ? TemperatureUnit.Fahrenheit
            : TemperatureUnit.Celsius;

        CurrentSettings.TemperatureUnit = unit;
        _settingsService.SaveSettings(CurrentSettings);
        _notificationService.Show($"Temperature unit set to {value}.");
    }

    partial void OnSelectedRefreshRateChanged(string value)
    {
        if (_isInitializingRefreshRates || CurrentSettings == null) return;

        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
        {
            CurrentSettings.DisplayRefreshRate = 0;
            _settingsService.SaveSettings(CurrentSettings);

            Task.Run(async () =>
            {
                var mode = await _powerPlanService.GetActivePowerModeAsync();
                _displayService.SetSmartRefreshRate(mode == PowerMode.Eco);
            });
            _notificationService.Show("Display refresh rate set to Auto (Smart).");
        }
        else
        {
            var numStr = value.Replace("Hz", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (int.TryParse(numStr, out int hz))
            {
                CurrentSettings.DisplayRefreshRate = hz;
                _settingsService.SaveSettings(CurrentSettings);

                _displayService.SetRefreshRate(hz);
                _notificationService.Show($"Display refresh rate set to {hz} Hz.");
            }
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

    public SettingsViewModel(
        SettingsService settingsService,
        NotificationService notificationService,
        TimeoutService timeoutService,
        ThemeService themeService,
        DisplayService displayService,
        PowerPlanService powerPlanService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _timeoutService = timeoutService;
        _themeService = themeService;
        _displayService = displayService;
        _powerPlanService = powerPlanService;
        _currentSettings = _settingsService.GetSettings();
        _selectedTheme = _currentSettings.Theme;

        InitializeTemperatureUnit();
        InitializeRefreshRates();
        _ = LoadTimeoutsAsync();
    }

    private void InitializeTemperatureUnit()
    {
        _isInitializingTemperatureUnit = true;
        try
        {
            SelectedTemperatureUnit = CurrentSettings.TemperatureUnit == TemperatureUnit.Fahrenheit
                ? "Fahrenheit (°F)"
                : "Celsius (°C)";
        }
        finally
        {
            _isInitializingTemperatureUnit = false;
        }
    }

    private void InitializeRefreshRates()
    {
        _isInitializingRefreshRates = true;
        try
        {
            AvailableRefreshRates.Clear();
            AvailableRefreshRates.Add("Auto (Smart)");

            var rates = _displayService.GetSupportedRefreshRates();
            foreach (var rate in rates)
            {
                AvailableRefreshRates.Add($"{rate} Hz");
            }

            if (CurrentSettings.DisplayRefreshRate > 0)
            {
                var match = AvailableRefreshRates.FirstOrDefault(r => r.StartsWith($"{CurrentSettings.DisplayRefreshRate}"));
                SelectedRefreshRate = match ?? $"{CurrentSettings.DisplayRefreshRate} Hz";
            }
            else
            {
                SelectedRefreshRate = "Auto (Smart)";
            }
        }
        finally
        {
            _isInitializingRefreshRates = false;
        }
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

        if (CurrentSettings.DisplayRefreshRate > 0)
        {
            _displayService.SetRefreshRate(CurrentSettings.DisplayRefreshRate);
        }
        else
        {
            var mode = await _powerPlanService.GetActivePowerModeAsync();
            _displayService.SetSmartRefreshRate(mode == PowerMode.Eco);
        }
        
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
        SelectedRefreshRate = "Auto (Smart)";
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
