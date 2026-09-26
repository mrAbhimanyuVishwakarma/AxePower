using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    private readonly SystemTelemetryService? _telemetryService;
    private readonly DisplayService? _displayService;
    private readonly GraphicsDriverService? _graphicsDriverService;
    private System.Collections.Generic.List<GraphicsAdapterInfo> _graphicsAdapters = new();

    // 4 Performance Metrics (2x2 Grid: CPU, GPU, RAM, WiFi)
    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> GpuHistory { get; } = new();
    public ObservableCollection<double> MemoryHistory { get; } = new();
    public ObservableCollection<double> NetworkHistory { get; } = new();

    [ObservableProperty]
    private string _cpuDisplay = "—";

    [ObservableProperty]
    private string _gpuName = "GPU";

    [ObservableProperty]
    private string _gpuDisplay = "—";

    [ObservableProperty]
    private string _memoryDisplay = "—";

    [ObservableProperty]
    private string _networkName = "NET";

    [ObservableProperty]
    private string _networkDisplay = "—";

    public ObservableCollection<DiskDriveSnapshot> Disks { get; } = new();

    // Status bar: real display + GPU hardware summary
    [ObservableProperty]
    private string _displaySummary = "—";

    [ObservableProperty]
    private string _hdrStatus = "—";

    [ObservableProperty]
    private string _cpuSummary = "—";

    [ObservableProperty]
    private string _gpuSummary = "—";

    public ObservableCollection<PowerMode> AvailableModes { get; } = new(new[] { PowerMode.Eco, PowerMode.Smart, PowerMode.Beast, PowerMode.GameMode });

    public ObservableCollection<double> BatteryTrendPoints { get; } = new();

    public ObservableCollection<AppFeatureItem> NewFeatures { get; } = new();

    [ObservableProperty]
    private AppFeatureItem? _selectedFeature;

    public DashboardViewModel(
        PowerPlanService powerPlanService, 
        BatteryService batteryService,
        NotificationService notificationService,
        SettingsService settingsService,
        AppKillerService appKillerService,
        ControllerNavigationService? controllerService = null,
        UpdateService? updateService = null,
        SystemTelemetryService? telemetryService = null,
        DisplayService? displayService = null,
        GraphicsDriverService? graphicsDriverService = null,
        GameModeGuard? gameModeGuard = null,
        AxePower.App.Services.IGameModePrompts? gameModePrompts = null)
    {
        _gameModeGuard = gameModeGuard;
        _gameModePrompts = gameModePrompts;
        _powerPlanService = powerPlanService;
        _batteryService = batteryService;
        _notificationService = notificationService;
        _settingsService = settingsService;
        _appKillerService = appKillerService;
        _controllerService = controllerService;
        _updateService = updateService;
        _telemetryService = telemetryService;
        _displayService = displayService;
        _graphicsDriverService = graphicsDriverService;

        // Initialize 30 points for sparklines
        for (int i = 0; i < 30; i++)
        {
            CpuHistory.Add(0);
            GpuHistory.Add(0);
            MemoryHistory.Add(0);
            NetworkHistory.Add(0);
        }

        if (_telemetryService != null)
        {
            _telemetryService.TelemetryUpdated += OnTelemetryUpdated;
        }

        if (_controllerService != null)
        {
            IsControllerNavigationEnabled = _controllerService.IsRunning();
        }

        LoadSettings();
        RefreshHardwareSummary();
        InitializeNewFeatures();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (s, e) => await RefreshStatusAsync();
        _timer.Start();

        _ = RefreshStatusAsync();
    }

    private void OnTelemetryUpdated(SystemTelemetrySnapshot snapshot)
    {
        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            CpuDisplay = snapshot.CpuDisplay;
            GpuName = snapshot.GpuName;
            GpuDisplay = snapshot.GpuDisplay;
            MemoryDisplay = snapshot.MemoryDisplay;
            NetworkName = snapshot.NetworkName;
            NetworkDisplay = snapshot.NetworkDisplay;

            UpdateGpuSummary(snapshot.IsDedicatedGpuActive);

            PushHistoryPoint(CpuHistory, snapshot.CpuPercent);
            PushHistoryPoint(GpuHistory, snapshot.GpuPercent);
            PushHistoryPoint(MemoryHistory, snapshot.MemoryPercent);
            PushHistoryPoint(NetworkHistory, snapshot.NetworkPercent);

            // Sparkline Paths bind through a converter, which only re-runs on PropertyChanged
            OnPropertyChanged(nameof(CpuHistory));
            OnPropertyChanged(nameof(GpuHistory));
            OnPropertyChanged(nameof(MemoryHistory));
            OnPropertyChanged(nameof(NetworkHistory));

            UpdateDisks(snapshot.Disks);
        });
    }

    /// <summary>Re-reads the live display mode (resolution, refresh rate, HDR).</summary>
    private void RefreshDisplaySummary()
    {
        if (_displayService == null) return;

        try
        {
            var display = _displayService.GetDisplayInfo();
            var parts = new System.Collections.Generic.List<string> { display.ResolutionDisplay };
            if (!string.IsNullOrEmpty(display.RefreshDisplay))
                parts.Add(display.RefreshDisplay);

            DisplaySummary = string.Join("  ", parts);
            HdrStatus = display.HdrStatus;
        }
        catch
        {
            // Status bar is informational only — never let it break the dashboard
        }
    }

    /// <summary>Reads the real display mode and installed GPUs for the status bar.</summary>
    private void RefreshHardwareSummary()
    {
        try
        {
            RefreshDisplaySummary();

            if (_telemetryService != null)
                CpuSummary = _telemetryService.CpuName;

            if (_graphicsDriverService != null)
            {
                _graphicsAdapters = _graphicsDriverService.DetectAdapters();
                // Until the first telemetry tick says which GPU is busy, show the primary one
                UpdateGpuSummary(_graphicsAdapters.Any(a => a.IsDedicated));
            }
        }
        catch
        {
            // Status bar is informational only — never let it break the dashboard
        }
    }

    /// <summary>Shows the GPU currently in use, matching the GPU card above.</summary>
    private void UpdateGpuSummary(bool dedicatedActive)
    {
        if (_graphicsAdapters.Count == 0) return;

        var adapter = _graphicsAdapters.FirstOrDefault(a => a.IsDedicated == dedicatedActive)
                      ?? _graphicsAdapters[0];

        GpuSummary = adapter.NameWithMemory;
    }

    private void UpdateDisks(System.Collections.Generic.List<DiskDriveSnapshot> latest)
    {
        // Update in place keyed by drive letter so unchanged cards cause no UI invalidation
        for (int i = Disks.Count - 1; i >= 0; i--)
        {
            if (!latest.Exists(d => d.DriveLetter == Disks[i].DriveLetter))
                Disks.RemoveAt(i);
        }

        for (int i = 0; i < latest.Count; i++)
        {
            var disk = latest[i];
            int existing = -1;
            for (int j = 0; j < Disks.Count; j++)
            {
                if (Disks[j].DriveLetter == disk.DriveLetter) { existing = j; break; }
            }

            if (existing < 0)
            {
                Disks.Insert(Math.Min(i, Disks.Count), disk);
            }
            else if (Disks[existing].UsedGb != disk.UsedGb ||
                     Disks[existing].FreeGb != disk.FreeGb ||
                     Disks[existing].DisplayName != disk.DisplayName ||
                     Disks[existing].StatusColorHex != disk.StatusColorHex)
            {
                Disks[existing] = disk;
            }
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

    private static void PushHistoryPoint(ObservableCollection<double> collection, double value)
    {
        if (collection.Count >= 30)
        {
            collection.RemoveAt(0);
        }
        collection.Add(value);
    }

    // Set while LoadSettings copies values in. Each property's change handler saves every field,
    // so without this the first assignments saved the not-yet-loaded ones as defaults — e.g.
    // StartWithWindows = false, which deleted the startup task on every launch.
    private bool _loadingSettings;

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();
        _loadingSettings = true;
        try
        {
            AutoSwitchEnabled = settings.AutoSwitchEnabled;
            PluggedInMode = settings.PluggedInMode;
            PluggedOutMode = settings.UnpluggedMode;
            LowBatteryMode = settings.LowBatteryMode;
            ShowNotifications = settings.ShowNotifications;
            StartWithWindows = settings.StartWithWindows;
            IsFnLockEnabled = settings.FnLockEnabled;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    partial void OnAutoSwitchEnabledChanged(bool value) => SaveSettings();
    partial void OnPluggedInModeChanged(PowerMode value) => SaveSettings();
    partial void OnPluggedOutModeChanged(PowerMode value) => SaveSettings();
    partial void OnLowBatteryModeChanged(PowerMode value) => SaveSettings();
    partial void OnShowNotificationsChanged(bool value) => SaveSettings();
    partial void OnStartWithWindowsChanged(bool value) => SaveSettings();
    partial void OnIsFnLockEnabledChanged(bool value)
    {
        // Loading the saved value is not a user toggle: no hardware write, save or toast
        if (_loadingSettings) return;
        _batteryService.SetFnLock(value);
        SaveSettings();
        _notificationService.Show(
            value ? "Function lock enabled (F1–F12 special keys locked)." : "Function lock disabled.");
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;

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

        // The refresh rate changes with power state, so keep the status bar current
        RefreshDisplaySummary();

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
        // The graph Path binds through a converter, which only re-runs on PropertyChanged
        OnPropertyChanged(nameof(BatteryTrendPoints));
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
    public async Task ApplyModeDirectAsync(string modeStr)
    {
        if (Enum.TryParse<PowerMode>(modeStr, out var mode))
        {
            SelectedMode = mode;
            await ApplyModeAsync();
        }
    }

    private readonly GameModeGuard? _gameModeGuard;
    private readonly AxePower.App.Services.IGameModePrompts? _gameModePrompts;

    [RelayCommand]
    private async Task ApplyModeAsync()
    {
        // Picking another mode ends Game Mode, but not behind a running game's back
        if (_gameModeGuard != null && await _gameModeGuard.GetBlockingGameAsync(SelectedMode) is string game &&
            _gameModePrompts?.ConfirmLeaveGameMode(game) != true)
        {
            await RefreshStatusAsync(syncSelectedMode: true);
            _notificationService.Show($"Game Mode stays on while {game} is running.");
            return;
        }

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

    // ------------------------------------------------------------------
    // Compact widget mode: shrinks the whole app down to the monitor card
    // ------------------------------------------------------------------

    /// <summary>Render scale of the monitor card once shrunk: half size, plus 25% so the
    /// readouts stay readable.</summary>
    public const double CompactScale = 0.625;

    [ObservableProperty]
    private bool _isShrunk;

    [ObservableProperty]
    private bool _isPinnedOnTop;

    /// <summary>1.0 in the full app, <see cref="CompactScale"/> in widget mode.</summary>
    public double MonitorCardScale => IsShrunk ? CompactScale : 1.0;

    /// <summary>Pinning is a widget-only affordance, so the full window is never forced on top.</summary>
    public bool IsWindowTopmost => IsShrunk && IsPinnedOnTop;

    public string ShrinkGlyph => IsShrunk ? "\uE740" : "\uE73F";
    public string ShrinkTooltip => IsShrunk ? "Expand back to the full app" : "Shrink to the monitor widget";
    public string PinGlyph => IsPinnedOnTop ? "\uE77A" : "\uE718";
    public string PinTooltip => IsPinnedOnTop ? "Unpin from top" : "Pin to top";

    partial void OnIsShrunkChanged(bool value)
    {
        OnPropertyChanged(nameof(MonitorCardScale));
        OnPropertyChanged(nameof(IsWindowTopmost));
        OnPropertyChanged(nameof(ShrinkGlyph));
        OnPropertyChanged(nameof(ShrinkTooltip));
    }

    partial void OnIsPinnedOnTopChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWindowTopmost));
        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinTooltip));
    }

    [RelayCommand]
    private void ToggleShrink() => IsShrunk = !IsShrunk;

    [RelayCommand]
    private void TogglePinOnTop() => IsPinnedOnTop = !IsPinnedOnTop;

    private void InitializeNewFeatures()
    {
        NewFeatures.Clear();
        foreach (var feature in BuildNewFeatures()) NewFeatures.Add(feature);

        SelectedFeature = NewFeatures.FirstOrDefault();
    }

    /// <summary>
    /// The "What's New" entries, newest first — the Dashboard shows the first one on load.
    /// Static and public so the list can be checked without building a whole view model.
    /// </summary>
    public static IReadOnlyList<AppFeatureItem> BuildNewFeatures() => new List<AppFeatureItem>
    {
        // 2.0.8 first (owner's pick: AxePower Account only), then 2.0.7, 2.0.6, and the 2.0.2 list in its order.
        new()
        {
            Title = "AxePower Account",
            Badge = "NEW IN 2.0.8",
            BadgeColorHex = "#06B6D4",
            Glyph = "\uE77B",
            Description = "Optional sign-in with Google, Discord, Steam or email. Your name and picture sit at the bottom of the sidebar, and friends can find you by your @username.",
            TargetNavigation = "Profile"
        },
        new()
        {
            Title = "Tools",
            Badge = "NEW IN 2.0.7",
            BadgeColorHex = "#3B82F6",
            Glyph = "\uEC7A",
            Description = "MSI Afterburner, CPU-Z, GPU-Z and CrystalDiskInfo on one page. Open the ones you have, or install the rest straight from each publisher.",
            TargetNavigation = "Tools"
        },
        new()
        {
            Title = "30 New Skins",
            Badge = "NEW IN 2.0.7",
            BadgeColorHex = "#EC4899",
            Glyph = "\uE790",
            Description = "From Midnight Sakura and Synthwave Sunset to Matrix Terminal, High Contrast and the first light glass skin. Pick one in Settings, Appearance & Display.",
            TargetNavigation = "Settings"
        },
        new()
        {
            Title = "New Settings",
            Badge = "NEW IN 2.0.7",
            BadgeColorHex = "#8B5CF6",
            Glyph = "\uE713",
            Description = "Settings now has a category list like Friends, one page per category, and Taskbar preferences to choose what the tray menu shows.",
            TargetNavigation = "Settings"
        },
        new()
        {
            Title = "Power Timer",
            Badge = "NEW IN 2.0.6",
            BadgeColorHex = "#F59E0B",
            Glyph = "\uE916",
            Description = "Shut down, restart or sleep after the time you set, from the button in the bottom-right corner of every page. Two minutes before, the countdown turns red and a warning appears on screen.",
            TargetNavigation = "Dashboard"
        },
        new()
        {
            Title = "Ambient Mode",
            Badge = "NEW IN 2.0.6",
            BadgeColorHex = "#8B5CF6",
            Glyph = "\uE8D6",
            Description = "Album art and synced lyrics fill the whole screen. It can open by itself like a screensaver while music plays, and lyrics are now found for far more songs.",
            TargetNavigation = "Music"
        },
        new()
        {
            Title = "Battery Health",
            Badge = "NEW IN 2.0.6",
            BadgeColorHex = "#10B981",
            Glyph = "\uE83F",
            Description = "Real battery health from your battery's own design capacity, a chart of how its capacity has changed, and the full Windows battery report in one click.",
            TargetNavigation = "Battery"
        },
        new()
        {
            Title = "DLSS 5 Swapper",
            Badge = "NEW GAMING TOOL",
            BadgeColorHex = "#10B981",
            Glyph = "\uE7FC",
            Description = "1-Click portable downloader & runner. Swap DLSS, Streamline DLLs, RenoDX, and ReShade silently in games.",
            TargetNavigation = "UpdatePc:Apps:Gaming"
        },
        new()
        {
            Title = "Share Screen",
            Badge = "NEW IN 2.0",
            BadgeColorHex = "#8B5CF6",
            Glyph = "\uE72D",
            Description = "Share a display to anyone on your network, in their browser, with no install. Hardware encoding on NVIDIA, AMD and Intel, and 60 FPS held by dropping resolution instead of frames.",
            TargetNavigation = "ShareScreen"
        },
        new()
        {
            Title = "Skins",
            Badge = "NEW IN 2.0.2",
            BadgeColorHex = "#A855F7",
            Glyph = "\uE790",
            Description = "Every theme is now a skin you can pick in Settings \u2192 Theme: Dark, Light, black Glass and Deep Obsidian (Glass). Drop a new skin folder in and it appears, no restart.",
            TargetNavigation = "Settings"
        },
        new()
        {
            Title = "Game Mode",
            Badge = "NEW IN 2.0.2",
            BadgeColorHex = "#8B5CF6",
            Glyph = "\uE7FC",
            Description = "Its own GameMode power plan, the dedicated GPU for your games while it is on, and a toggle right on the Dashboard. Picking another mode ends it, but never while a game is still running.",
            TargetNavigation = "GameMode"
        },
        new()
        {
            Title = "Phone Control & LanDrop",
            Badge = "CROSS-DEVICE",
            BadgeColorHex = "#F59E0B",
            Glyph = "\uE8EA",
            Description = "Instant Wi-Fi file transfers between PC and smartphone without cables, plus remote quick actions.",
            TargetNavigation = "PhoneHub"
        },
        new()
        {
            Title = "Uninstaller Pro",
            Badge = "DEEP CLEAN",
            BadgeColorHex = "#EF4444",
            Glyph = "\uE74D",
            Description = "Remove applications and clean only verified leftovers, with confidence scoring, a safety backup before cleaning, and a fixer for broken uninstall entries.",
            TargetNavigation = "Uninstaller"
        },
        new()
        {
            Title = "Terminal",
            Badge = "BUILT-IN",
            BadgeColorHex = "#F59E0B",
            Glyph = "\uE756",
            Description = "A real CMD and PowerShell inside AxePower, where Software Hub installs and System Doctor repairs stream their output, with a Stop button.",
            TargetNavigation = "Terminal"
        },
    };
}

public class AppFeatureItem
{
    public string Title { get; set; } = string.Empty;
    public string Badge { get; set; } = "NEW";
    public string BadgeColorHex { get; set; } = "#3B82F6";
    public string Glyph { get; set; } = "\uE735";
    public string Description { get; set; } = string.Empty;
    public string TargetNavigation { get; set; } = "UpdatePc";
}
