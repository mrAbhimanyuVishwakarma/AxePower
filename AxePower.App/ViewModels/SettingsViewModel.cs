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

public class PowerModeDisplayItem
{
    public PowerMode Mode { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Glyph { get; set; } = "\uE945";
    public string ColorHex { get; set; } = "#3B82F6";
}

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

    public ObservableCollection<PowerModeDisplayItem> AvailablePowerModeItems { get; } = new()
    {
        new PowerModeDisplayItem { Mode = PowerMode.Eco, DisplayName = "Eco Mode", Glyph = "\uE945", ColorHex = "#10B981" },
        new PowerModeDisplayItem { Mode = PowerMode.Smart, DisplayName = "Smart Mode", Glyph = "\uE945", ColorHex = "#3B82F6" },
        new PowerModeDisplayItem { Mode = PowerMode.Beast, DisplayName = "Beast Mode", Glyph = "\uE945", ColorHex = "#F97316" },
        new PowerModeDisplayItem { Mode = PowerMode.GameMode, DisplayName = "Game Mode", Glyph = "\uE7FC", ColorHex = "#8B5CF6" }
    };

    public ObservableCollection<string> AvailableFanModes { get; } = new()
    {
        "Smart",
        "Eco (Quiet)",
        "Beast (Turbo)"
    };

    [ObservableProperty]
    private PowerMode _selectedUnpluggedMode = PowerMode.Smart;

    partial void OnSelectedUnpluggedModeChanged(PowerMode value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.UnpluggedMode = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private PowerMode _selectedPluggedInMode = PowerMode.Smart;

    partial void OnSelectedPluggedInModeChanged(PowerMode value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.PluggedInMode = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private string _selectedFanModeOnBattery = "Smart";

    partial void OnSelectedFanModeOnBatteryChanged(string value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.FanModeOnBattery = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _startWithLastSelectedMode;

    partial void OnStartWithLastSelectedModeChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.StartWithLastSelectedMode = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.StartWithWindows = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _startMinimised;

    partial void OnStartMinimisedChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.StartMinimised = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _showNotifications = true;

    partial void OnShowNotificationsChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.ShowNotifications = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _notifyOnModeSwitch = true;

    partial void OnNotifyOnModeSwitchChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.NotifyOnModeSwitch = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _lowBatteryAlertSound = true;

    partial void OnLowBatteryAlertSoundChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.LowBatteryAlertSound = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private AppTheme _selectedTheme;

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.Theme = value;
            _themeService.ApplyTheme(value);
            _settingsService.SaveSettings(CurrentSettings);
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

    [ObservableProperty]
    private bool _isTouchpadEnabled = true;

    partial void OnIsTouchpadEnabledChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.TouchpadEnabled = value;
            _settingsService.SaveSettings(CurrentSettings);
        }

        _batteryService?.SetTouchpad(value);
        _notificationService.Show(value ? "Touchpad enabled." : "Touchpad disabled.");
    }

    [RelayCommand]
    private void OpenTouchpadSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true });
        }
        catch { }
    }

    public IEnumerable<PowerMode> AvailableModes => Enum.GetValues(typeof(PowerMode)).Cast<PowerMode>();
    public IEnumerable<AppTheme> AvailableThemes => Enum.GetValues(typeof(AppTheme)).Cast<AppTheme>();
    public IEnumerable<CloseBehaviour> AvailableCloseBehaviours => Enum.GetValues(typeof(CloseBehaviour)).Cast<CloseBehaviour>();

    private readonly UpdateService? _updateService;
    private readonly BatteryService? _batteryService;

    public string CurrentVersion => _updateService?.GetCurrentVersionString() ?? "1.6.0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedDisplay))]
    private string _lastCheckedTime = "Not checked yet";

    public string LastCheckedDisplay => LastCheckedTime == "Not checked yet" ? "Not checked yet" : LastCheckedTime;

    [ObservableProperty]
    private bool _autoCheckUpdates = true;

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.AutoCheckUpdates = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    [ObservableProperty]
    private bool _autoDownloadUpdates = true;

    partial void OnAutoDownloadUpdatesChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.AutoDownloadUpdates = value;
            _settingsService.SaveSettings(CurrentSettings);
        }
    }

    public SettingsViewModel(
        SettingsService settingsService,
        NotificationService notificationService,
        TimeoutService timeoutService,
        ThemeService themeService,
        DisplayService displayService,
        PowerPlanService powerPlanService,
        UpdateService updateService,
        BatteryService? batteryService = null)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _timeoutService = timeoutService;
        _themeService = themeService;
        _displayService = displayService;
        _powerPlanService = powerPlanService;
        _updateService = updateService;
        _batteryService = batteryService;
        _currentSettings = _settingsService.GetSettings();
        _selectedTheme = _currentSettings.Theme;
        _selectedUnpluggedMode = _currentSettings.UnpluggedMode;
        _selectedPluggedInMode = _currentSettings.PluggedInMode;
        _selectedFanModeOnBattery = _currentSettings.FanModeOnBattery ?? "Smart";
        _startWithLastSelectedMode = _currentSettings.StartWithLastSelectedMode;
        _startWithWindows = _currentSettings.StartWithWindows;
        _startMinimised = _currentSettings.StartMinimised;
        _showNotifications = _currentSettings.ShowNotifications;
        _notifyOnModeSwitch = _currentSettings.NotifyOnModeSwitch;
        _lowBatteryAlertSound = _currentSettings.LowBatteryAlertSound;
        _isTouchpadEnabled = _currentSettings.TouchpadEnabled;
        _autoCheckUpdates = _currentSettings.AutoCheckUpdates;
        _autoDownloadUpdates = _currentSettings.AutoDownloadUpdates;

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
    private void OpenHelp()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.OpenToolHelp(null);
        }
    }

    [ObservableProperty]
    private bool _isCheckingForUpdate;

    [ObservableProperty]
    private string _checkButtonText = "Check for updates";

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdate) return;

        IsCheckingForUpdate = true;
        CheckButtonText = "Checking...";
        LastCheckedTime = DateTime.Now.ToString("MMM dd, yyyy • h:mm tt");
        if (_updateService == null)
        {
            IsCheckingForUpdate = false;
            CheckButtonText = "✓ Already Updated";
            _notificationService.Show("Already Updated");
            _ = ResetCheckButtonTextAfterDelay();
            return;
        }

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            IsCheckingForUpdate = false;
            if (result.HasUpdate)
            {
                CheckButtonText = "Update Available";
                _notificationService.Show($"🚀 New update available: v{result.LatestVersion}! Check About page.");
            }
            else
            {
                CheckButtonText = "✓ Already Updated";
                _notificationService.Show("Already Updated");
            }
            _ = ResetCheckButtonTextAfterDelay();
        }
        catch (Exception ex)
        {
            IsCheckingForUpdate = false;
            CheckButtonText = "Check for updates";
            _notificationService.Show("Failed to check for updates: " + ex.Message);
        }
    }

    private async Task ResetCheckButtonTextAfterDelay()
    {
        await Task.Delay(3000);
        CheckButtonText = "Check for updates";
    }
}
