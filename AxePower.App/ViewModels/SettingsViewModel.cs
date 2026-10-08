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

    /// <summary>Windows High Contrast is on, so the High Contrast skin shows in place of the chosen one.</summary>
    public bool IsHighContrastActive => _themeService.IsHighContrastActive;

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
            // Dark, Light and Glass first, then the rest A-Z, which puts each "(Glass)" twin under its skin
            var builtIn = AxePower.Core.Services.Skins.SkinCatalog.BuiltInIds;
            var ordered = _themeService.Catalog.Skins
                .OrderBy(s => Array.IndexOf(builtIn, s.Id) is var i && i >= 0 ? i : builtIn.Length)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var skin in ordered)
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
            _notificationService.ShowFailure("Open the skins folder", ex);
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

    /// <summary>False on PCs without a precision touchpad; the switch is then greyed out.</summary>
    [ObservableProperty]
    private bool _isTouchpadAvailable = true;

    private TouchpadService? _touchpad;
    private bool _syncingTouchpad;

    /// <summary>
    /// Reads the touchpad's real state from Windows, so this switch always matches the one in
    /// Windows Settings, including after a laptop Fn key or a change made there.
    /// </summary>
    public void RefreshTouchpadState()
    {
        var state = _touchpad?.IsEnabled();
        IsTouchpadAvailable = state is not null;
        if (state is not bool enabled || enabled == IsTouchpadEnabled) return;

        _syncingTouchpad = true;
        try { IsTouchpadEnabled = enabled; }
        finally { _syncingTouchpad = false; }
    }

    partial void OnIsTouchpadEnabledChanged(bool value)
    {
        if (_syncingTouchpad) return;
        _ = ApplyTouchpadAsync(value);
    }

    private async Task ApplyTouchpadAsync(bool value)
    {
        if (_touchpad == null) return;

        bool applied = await _touchpad.SetEnabledAsync(value);
        if (applied)
        {
            if (CurrentSettings != null)
            {
                CurrentSettings.TouchpadEnabled = value;
                SaveCurrentSettings();
            }
            _notificationService.Show(value ? "Touchpad turned on." : "Touchpad turned off.");
            return;
        }

        // Windows did not change it: put the switch back to what is really happening
        _notificationService.Show(IsTouchpadAvailable
            ? "Windows did not change the touchpad. Try the switch in Windows Settings."
            : "This PC has no precision touchpad that Windows can switch off.");
        RefreshTouchpadState();
    }

    [RelayCommand]
    private void OpenTouchpadSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notificationService.Show("Could not open the touchpad settings.");
        }
    }

    public IEnumerable<PowerMode> AvailableModes => Enum.GetValues(typeof(PowerMode)).Cast<PowerMode>();
    public IEnumerable<CloseBehaviour> AvailableCloseBehaviours => Enum.GetValues(typeof(CloseBehaviour)).Cast<CloseBehaviour>();

    private readonly UpdateService? _updateService;
    private readonly BatteryService? _batteryService;
    private readonly ElevationService? _elevationService;

    public string CurrentVersion => _updateService?.GetCurrentVersionString() ?? UpdateService.FallbackVersion;

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

    // ── AxePower Mobile Remote Access ──────────────────────────────────────

    [ObservableProperty]
    private bool _mobileRemoteAccessEnabled;

    /// <summary>False while the cloud bridge is switched off in code; the toggle is then disabled and explained.</summary>
    public bool RemoteAccessAvailable => CloudBridgeService.IsAvailable;

    public string RemoteAccessNotice => CloudBridgeService.IsAvailable
        ? "Sign in with the same AxePower account on phone and PC to connect anywhere in the world."
        : CloudBridgeService.UnavailableReason;

    partial void OnMobileRemoteAccessEnabledChanged(bool value)
    {
        if (CurrentSettings?.RemoteAccess != null)
        {
            CurrentSettings.RemoteAccess.Enabled = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _mobileAllowTelemetry = true;

    partial void OnMobileAllowTelemetryChanged(bool value)
    {
        if (CurrentSettings?.RemoteAccess != null)
        {
            CurrentSettings.RemoteAccess.AllowTelemetry = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _mobileAllowBatterySync = true;

    partial void OnMobileAllowBatterySyncChanged(bool value)
    {
        if (CurrentSettings?.RemoteAccess != null)
        {
            CurrentSettings.RemoteAccess.AllowBatterySync = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _mobileAllowPowerModeControl = true;

    partial void OnMobileAllowPowerModeControlChanged(bool value)
    {
        if (CurrentSettings?.RemoteAccess != null)
        {
            CurrentSettings.RemoteAccess.AllowPowerModeControl = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _mobileAllowBatteryTuning = true;

    partial void OnMobileAllowBatteryTuningChanged(bool value)
    {
        if (CurrentSettings?.RemoteAccess != null)
        {
            CurrentSettings.RemoteAccess.AllowBatteryTuning = value;
            SaveCurrentSettings();
        }
    }

    [ObservableProperty]
    private bool _mobileAllowSystemPower = false;

    partial void OnMobileAllowSystemPowerChanged(bool value)
    {
        if (CurrentSettings?.RemoteAccess != null)
        {
            CurrentSettings.RemoteAccess.AllowSystemPower = value;
            SaveCurrentSettings();
        }
    }

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
        set
        {
            _requestedSection = value;
            OnPropertyChanged(nameof(RequestedSection));
            // A deep link now simply opens its category
            if (!string.IsNullOrEmpty(value) && Categories.Any(c => c.Id == value)) SelectedCategory = value;
        }
    }

    /// <summary>Settings › Diagnostics & Privacy: inspect, export and delete what AxePower has logged.</summary>
    public DiagnosticsViewModel? Diagnostics { get; }

    /// <summary>Settings › Change history: what AxePower changed in Windows, and rolling it back.</summary>
    public ChangeHistoryViewModel? ChangeHistory { get; }

    // ── Categories (the collapsible list beside the settings) ───────────────

    /// <summary>One entry per settings card; "About" opens the About page instead.</summary>
    public IReadOnlyList<SettingsCategoryItem> Categories { get; } = new[]
    {
        new SettingsCategoryItem("Appearance", "Appearance & Display", "\uE790"),
        new SettingsCategoryItem("GameMode", "Game Mode", "\uE7FC"),
        new SettingsCategoryItem("Notifications", "Notifications & Alerts", "\uEA8F"),
        new SettingsCategoryItem("Startup", "Startup & Elevation", "\uE7E8"),
        new SettingsCategoryItem("Taskbar", "Taskbar preferences", "\uE75B"),
        new SettingsCategoryItem("AxePowerMobile", "AxePower Mobile", "\uE8EA"),
        new SettingsCategoryItem("ShareScreen", "Friends", "\uE779"),
        new SettingsCategoryItem("Music", "Music & Playlists", "\uE8D6"),
        new SettingsCategoryItem("Updates", "Updates & Maintenance", "\uE777"),
        new SettingsCategoryItem("Diagnostics", "Diagnostics & Privacy", "\uE9D9"),
        new SettingsCategoryItem("ChangeHistory", "Change history", "\uE81C"),
    };

    [ObservableProperty]
    private string _selectedCategory = "Appearance";

    partial void OnSelectedCategoryChanged(string value)
    {
        foreach (var category in Categories) category.IsSelected = category.Id == value;
    }

    [RelayCommand]
    private void SelectCategory(string? id)
    {
        if (!string.IsNullOrEmpty(id)) SelectedCategory = id;
    }

    /// <summary>Folds the list to an icon rail; remembered across restarts.</summary>
    [ObservableProperty]
    private bool _isCategoryListCollapsed;

    partial void OnIsCategoryListCollapsedChanged(bool value)
    {
        var settings = _settingsService.GetSettings();
        if (settings.SettingsCategoriesCollapsed == value) return;
        settings.SettingsCategoriesCollapsed = value;
        _settingsService.SaveSettings(settings);
    }

    [RelayCommand]
    private void ToggleCategoryList() => IsCategoryListCollapsed = !IsCategoryListCollapsed;

    // ── Taskbar preferences (which items the tray menu shows) ───────────────

    /// <summary>Every tray menu item, in menu order; Settings and Exit are always shown.</summary>
    public IReadOnlyList<TrayMenuOption> TrayMenuOptions { get; private set; } = Array.Empty<TrayMenuOption>();

    private void LoadTrayMenuOptions()
    {
        var hidden = new HashSet<string>(CurrentSettings.HiddenTrayMenuItems ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        string? previousGroup = null;
        TrayMenuOptions = AxePower.App.Models.TrayMenuCatalog.Entries.Select(entry =>
        {
            var option = new TrayMenuOption(entry.Id, entry.Title, entry.Group == previousGroup ? null : entry.Group,
                entry.Permanent, entry.Permanent || !hidden.Contains(entry.Id), SaveTrayMenuChoice);
            previousGroup = entry.Group;
            return option;
        }).ToList();
    }

    private void SaveTrayMenuChoice(TrayMenuOption option)
    {
        if (option.IsPermanent) return;
        var settings = _settingsService.GetSettings();
        settings.HiddenTrayMenuItems ??= new List<string>();
        settings.HiddenTrayMenuItems.RemoveAll(id => string.Equals(id, option.Id, StringComparison.OrdinalIgnoreCase));
        if (!option.IsShown) settings.HiddenTrayMenuItems.Add(option.Id);
        _settingsService.SaveSettings(settings);
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
        AxePower.App.Services.IGameModePrompts? gameModePrompts = null,
        TouchpadService? touchpad = null,
        DiagnosticsViewModel? diagnostics = null,
        ChangeHistoryViewModel? changeHistory = null)
    {
        _touchpad = touchpad;
        Diagnostics = diagnostics;
        ChangeHistory = changeHistory;
        Music = music;
        ShareScreen = shareScreen;
        _settingsService = settingsService;
        _notificationService = notificationService;
        _timeoutService = timeoutService;
        _themeService = themeService;
        _themeService.ThemeChanged += _ => OnPropertyChanged(nameof(IsHighContrastActive));
        _displayService = displayService;
        _powerPlanService = powerPlanService;
        _updateService = updateService;
        _batteryService = batteryService;
        _elevationService = elevationService;
        _currentSettings = _settingsService.GetSettings();
        _isCategoryListCollapsed = _currentSettings.SettingsCategoriesCollapsed;
        LoadTrayMenuOptions();
        foreach (var category in Categories) category.IsSelected = category.Id == _selectedCategory;
        InitializeGameMode(gameModeGpu, displayModeController, gameModePrompts, gameModeGpu != null ? new GpuControlAppService() : null);
        RefreshThemeChoices();
        _startWithLastSelectedMode = _currentSettings.StartWithLastSelectedMode;
        _startWithWindows = _currentSettings.StartWithWindows;
        _startMinimised = _currentSettings.StartMinimised;
        _runAsAdministrator = _currentSettings.RunAsAdministrator;
        _showNotifications = _currentSettings.ShowNotifications;
        _notifyOnModeSwitch = _currentSettings.NotifyOnModeSwitch;
        _lowBatteryAlertSound = _currentSettings.LowBatteryAlertSound;
        // Windows is the source of truth; the saved value is only a fallback when it cannot be read
        _isTouchpadEnabled = _touchpad?.IsEnabled() ?? _currentSettings.TouchpadEnabled;
        _isTouchpadAvailable = _touchpad?.IsEnabled() is not null;
        _autoCheckUpdates = _currentSettings.AutoCheckUpdates;
        _autoDownloadUpdates = _currentSettings.AutoDownloadUpdates;
        _mobileRemoteAccessEnabled = CloudBridgeService.IsAvailable && (_currentSettings.RemoteAccess?.Enabled ?? false);
        _mobileAllowTelemetry = _currentSettings.RemoteAccess?.AllowTelemetry ?? true;
        _mobileAllowBatterySync = _currentSettings.RemoteAccess?.AllowBatterySync ?? true;
        _mobileAllowPowerModeControl = _currentSettings.RemoteAccess?.AllowPowerModeControl ?? true;
        _mobileAllowBatteryTuning = _currentSettings.RemoteAccess?.AllowBatteryTuning ?? true;
        _mobileAllowSystemPower = _currentSettings.RemoteAccess?.AllowSystemPower ?? false;

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

    private AboutViewModel? _linkedAbout;

    /// <summary>The About page's view model — the one MainWindow's update banner and patch notes bind to.</summary>
    private static MainViewModel? MainVm => System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel;

    /// <summary>
    /// Mirrors the About page's update state (button text, checking, last checked) so both pages
    /// show the same thing whichever one ran the check. Called when the Settings page loads.
    /// </summary>
    public void LinkUpdateStateToAbout()
    {
        var about = MainVm?.AboutVm;
        if (about == null || ReferenceEquals(about, _linkedAbout)) return;
        if (_linkedAbout != null) _linkedAbout.PropertyChanged -= OnAboutUpdateStateChanged;
        _linkedAbout = about;
        about.PropertyChanged += OnAboutUpdateStateChanged;
        CopyUpdateStateFrom(about);
    }

    private void OnAboutUpdateStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is AboutViewModel about
            && e.PropertyName is nameof(AboutViewModel.CheckButtonText) or nameof(AboutViewModel.IsCheckingForUpdate) or nameof(AboutViewModel.LastCheckedTime))
            CopyUpdateStateFrom(about);
    }

    private void CopyUpdateStateFrom(AboutViewModel about)
    {
        IsCheckingForUpdate = about.IsCheckingForUpdate;
        CheckButtonText = about.CheckButtonText;
        LastCheckedTime = about.LastCheckedTime;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdate) return;

        // Same check as the About page: the up-to-date banner with its patch notes, or the
        // update dialog with Update Now. That dialog lives on the About page, so go there for it.
        var mainVm = MainVm;
        if (mainVm?.AboutVm is { } about)
        {
            LinkUpdateStateToAbout();
            await about.CheckForUpdatesCommand.ExecuteAsync(null);
            if (about.IsUpdateAvailable && about.IsUpdateModalOpen) mainVm.Navigate("About");
            return;
        }

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
            _notificationService.ShowFailure("Check for updates", ex);
        }
    }

    private async Task ResetCheckButtonTextAfterDelay()
    {
        await Task.Delay(3000);
        CheckButtonText = "Check for updates";
    }
}

/// <summary>A row in the Settings category list.</summary>
public partial class SettingsCategoryItem : ObservableObject
{
    public SettingsCategoryItem(string id, string title, string glyph)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>A tray menu item's row in Settings → Taskbar preferences.</summary>
public partial class TrayMenuOption : ObservableObject
{
    private readonly Action<TrayMenuOption> _save;

    public TrayMenuOption(string id, string title, string? groupHeader, bool isPermanent, bool isShown, Action<TrayMenuOption> save)
    {
        Id = id;
        Title = title;
        GroupHeader = groupHeader;
        IsPermanent = isPermanent;
        _isShown = isShown;
        _save = save;
    }

    public string Id { get; }
    public string Title { get; }

    /// <summary>Set on the first item of each group, so the list reads like the menu.</summary>
    public string? GroupHeader { get; }

    /// <summary>Settings and Exit: always in the menu, so the switch is locked on.</summary>
    public bool IsPermanent { get; }
    public bool CanChange => !IsPermanent;

    [ObservableProperty]
    private bool _isShown;

    partial void OnIsShownChanged(bool value) => _save(this);
}
