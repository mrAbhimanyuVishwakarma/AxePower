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
    private bool _startWithLastSelectedMode;

    partial void OnStartWithLastSelectedModeChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.StartWithLastSelectedMode = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.StartWithWindows = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _startMinimised;

    partial void OnStartMinimisedChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.StartMinimised = value;
            SaveCurrentSettings();
        }
    }

    /// <summary>True when this process currently holds administrator rights.</summary>
    public bool IsRunningElevated => _elevationService?.IsElevated ?? false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestartElevated))]
    private bool _runAsAdministrator = true;

    /// <summary>The restart action is only worth offering when it would actually change something.</summary>
    public bool CanRestartElevated => RunAsAdministrator && !IsRunningElevated;

    partial void OnRunAsAdministratorChanged(bool value)
    {
        if (CurrentSettings == null) return;

        CurrentSettings.RunAsAdministrator = value;
        // Saving re-registers the logon task at the matching run level via StartupService,
        // which is what makes the choice survive a reboot without a UAC prompt.
        SaveCurrentSettings();

        _notificationService.Show(value
            ? "AxePower will run as administrator. Restart to apply."
            : "AxePower will run as a standard user. Restart to apply.");
    }

    /// <summary>Relaunches elevated now rather than making the user close and reopen the app.</summary>
    [RelayCommand]
    private void RestartElevated()
    {
        if (_elevationService == null) return;

        // --force stops the elevated child from treating this closing instance as the live one
        if (_elevationService.TryRelaunchElevated("--force") == ElevationService.RelaunchResult.Started)
            System.Windows.Application.Current?.Shutdown();
        else
            _notificationService.Show("Administrator restart was cancelled.");
    }

    [ObservableProperty]
    private bool _showNotifications = true;

    partial void OnShowNotificationsChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.ShowNotifications = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _notifyOnModeSwitch = true;

    partial void OnNotifyOnModeSwitchChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.NotifyOnModeSwitch = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _lowBatteryAlertSound = true;

    partial void OnLowBatteryAlertSoundChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.LowBatteryAlertSound = value;
            SaveCurrentSettings();
        }
    }

    /// <summary>"System" first, then every skin found (built-in, shipped and the user's own).</summary>
    public ObservableCollection<ThemeChoice> ThemeChoices { get; } = new();

    [ObservableProperty]
    private ThemeChoice? _selectedThemeChoice;

    partial void OnSelectedThemeChoiceChanged(ThemeChoice? value)
    {
        if (CurrentSettings == null || value == null || _isLoadingThemeChoices) return;

        CurrentSettings.Theme = value.Theme;
        CurrentSettings.SkinId = value.SkinId;
        _themeService.ApplyTheme(value.Theme, value.SkinId);
        SaveCurrentSettings();
    }

    private bool _isLoadingThemeChoices;

    /// <summary>Re-reads the skin folders, so a skin added while AxePower runs can be picked.
    /// Called when the Settings page is opened.</summary>
    public void RefreshThemeChoices()
    {
        _isLoadingThemeChoices = true;
        try
        {
            _themeService.Catalog.Reload();
            ThemeChoices.Clear();
            ThemeChoices.Add(new ThemeChoice(null, AppTheme.System, "System"));
            foreach (var skin in _themeService.Catalog.Skins)
                ThemeChoices.Add(new ThemeChoice(skin.Id, ThemeService.ToAppTheme(skin), skin.Name));

            // Older settings have no skin id: Dark/Light/Glass map to those skins
            string? id = CurrentSettings.SkinId
                ?? (CurrentSettings.Theme == AppTheme.System ? null : CurrentSettings.Theme.ToString().ToLowerInvariant());
            SelectedThemeChoice = ThemeChoices.FirstOrDefault(c => string.Equals(c.SkinId, id, StringComparison.OrdinalIgnoreCase))
                ?? ThemeChoices[0];
        }
        finally
        {
            _isLoadingThemeChoices = false;
        }
    }

    /// <summary>Opens %APPDATA%\AxePower\Skins, where your own skins go.</summary>
    [RelayCommand]
    private void OpenSkinsFolder()
    {
        try
        {
            var folder = AxePower.Core.Services.Skins.SkinCatalog.UserSkinsFolder;
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _notificationService.Show($"Could not open the skins folder: {ex.Message}");
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
        SaveCurrentSettings();
        _notificationService.Show($"Temperature unit set to {value}.");
    }

    partial void OnSelectedRefreshRateChanged(string value)
    {
        if (_isInitializingRefreshRates || CurrentSettings == null) return;

        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
        {
            CurrentSettings.DisplayRefreshRate = 0;
            SaveCurrentSettings();

            // Same rule as a mode change: 60 Hz only for Eco on battery
            Task.Run(_powerPlanService.SyncRefreshRateAsync);
            _notificationService.Show("Display refresh rate set to Auto (Smart).");
        }
        else
        {
            var numStr = value.Replace("Hz", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (int.TryParse(numStr, out int hz))
            {
                CurrentSettings.DisplayRefreshRate = hz;
                SaveCurrentSettings();

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
            SaveCurrentSettings();
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
    public IEnumerable<CloseBehaviour> AvailableCloseBehaviours => Enum.GetValues(typeof(CloseBehaviour)).Cast<CloseBehaviour>();

    private readonly UpdateService? _updateService;
    private readonly BatteryService? _batteryService;
    private readonly ElevationService? _elevationService;

    public string CurrentVersion => _updateService?.GetCurrentVersionString() ?? "2.0.0";

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
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _autoDownloadUpdates = true;

    partial void OnAutoDownloadUpdatesChanged(bool value)
    {
        if (CurrentSettings != null)
        {
            CurrentSettings.AutoDownloadUpdates = value;
            SaveCurrentSettings();
        }
    }

    /// <summary>
    /// CurrentSettings is a snapshot taken at construction, so first pull in the state the
    /// sidebar has saved since then; otherwise any Settings change would revert it.
    /// </summary>
    /// <summary>The Music settings group (null in minimal test setups, which hides it).</summary>
    public AxePower.App.ViewModels.Music.MusicSettingsViewModel? Music { get; }

    /// <summary>The Share Screen settings group (null in minimal test setups, which hides it).</summary>
    public AxePower.App.ViewModels.ShareScreen.ShareScreenSettingsViewModel? ShareScreen { get; }

    private string? _requestedSection;

    /// <summary>
    /// A card the page should scroll to on arrival, e.g. "ShareScreen" when the user clicked the
    /// gear on the sharing stage. The view clears it once it has obliged.
    /// </summary>
    public string? RequestedSection
    {
        get => _requestedSection;
        set { _requestedSection = value; OnPropertyChanged(nameof(RequestedSection)); }
    }

    private void SaveCurrentSettings()
    {
        var latest = _settingsService.GetSettings();
        CurrentSettings.SidebarMaintenanceExpanded = latest.SidebarMaintenanceExpanded;
        CurrentSettings.SidebarUtilitiesExpanded = latest.SidebarUtilitiesExpanded;
        CurrentSettings.SidebarCollapsed = latest.SidebarCollapsed;
        // The music section is edited elsewhere (Music group, header player); never save a stale copy of it
        CurrentSettings.Music = latest.Music;
        // Same for Share Screen: its pickers write straight through, so a snapshot here is stale
        CurrentSettings.ShareScreen = latest.ShareScreen;
        // Written in the background by the power-plan and Game Mode GPU services
        CurrentSettings.PowerPlansInitialized = latest.PowerPlansInitialized;
        CurrentSettings.GpuPreferenceBackups = latest.GpuPreferenceBackups;
        _settingsService.SaveSettings(CurrentSettings);
    }

    public SettingsViewModel(
        SettingsService settingsService,
        NotificationService notificationService,
        TimeoutService timeoutService,
        ThemeService themeService,
        DisplayService displayService,
        PowerPlanService powerPlanService,
        UpdateService updateService,
        BatteryService? batteryService = null,
        ElevationService? elevationService = null,
        AxePower.App.ViewModels.Music.MusicSettingsViewModel? music = null,
        AxePower.App.ViewModels.ShareScreen.ShareScreenSettingsViewModel? shareScreen = null,
        GameModeGpuService? gameModeGpu = null,
        IGpuDisplayModeController? displayModeController = null,
        AxePower.App.Services.IGameModePrompts? gameModePrompts = null)
    {
        Music = music;
        ShareScreen = shareScreen;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _timeoutService = timeoutService;
        _themeService = themeService;
        _displayService = displayService;
        _powerPlanService = powerPlanService;
        _updateService = updateService;
        _batteryService = batteryService;
        _elevationService = elevationService;
        _currentSettings = _settingsService.GetSettings();
        InitializeGameMode(gameModeGpu, displayModeController, gameModePrompts, gameModeGpu != null ? new GpuControlAppService() : null);
        RefreshThemeChoices();
        _startWithLastSelectedMode = _currentSettings.StartWithLastSelectedMode;
        _startWithWindows = _currentSettings.StartWithWindows;
        _startMinimised = _currentSettings.StartMinimised;
        _runAsAdministrator = _currentSettings.RunAsAdministrator;
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
