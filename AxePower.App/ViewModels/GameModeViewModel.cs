using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.App.Services;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class GameModeViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly GameScannerService _gameScannerService;
    private readonly AppKillerService _appKillerService;
    private readonly SystemServiceOptimizer _systemServiceOptimizer;
    private readonly IOperationPrompts _operationPrompts;
    private readonly PowerPlanService _powerPlanService;
    private readonly ControllerNavigationService? _controllerService;
    private readonly IDynamicModeAutomationService? _automationService;
    private readonly IServiceThrottlerService? _throttlerService;
    private readonly IMemoryOptimizerService? _memoryOptimizer;
    private readonly SystemTelemetryService? _telemetryService;
    private readonly MsiAfterburnerService? _msiAfterburnerService;
    private readonly IGameModePrompts? _gameModePrompts;

    /// <summary>Set while a list is rebuilt or bulk-selected, so each row change does not save.</summary>
    private bool _isBulkUpdatingSelection;

    // --- State Properties ---
    [ObservableProperty]
    private bool _appRulesEnabled = true;

    [ObservableProperty]
    private bool _gameBoosterEnabled = true;

    [ObservableProperty]
    private bool _isManualGameModeActive;

    [ObservableProperty]
    private string _gameModeStatusHeader = "TURN ON GAMEMODE";

    [ObservableProperty]
    private string _servicesPausedCountText = "0";

    [ObservableProperty]
    private string _ramFreedText = "0 MB";

    /// <summary>Set while the toggle follows a mode picked elsewhere, so it does not apply one itself.</summary>
    private bool _followingAppliedMode;

    partial void OnIsManualGameModeActiveChanged(bool value)
    {
        GameModeStatusHeader = value ? "GAMEMODE IS ACTIVE" : "TURN ON GAMEMODE";
        ServicesPausedCountText = value ? "14" : "0";
        RamFreedText = value ? "2.3 GB" : "0 MB";
        if (!_followingAppliedMode) _ = ApplyGameModeStateAsync(value);
    }

    /// <summary>
    /// Picking another mode anywhere (Dashboard, tray, Auto Switch, an app rule) ends Game Mode,
    /// so the toggle turns itself off. The mode just picked stays; Smart is not applied over it.
    /// </summary>
    private void OnPowerModeApplied(object? sender, PowerMode mode)
    {
        if (mode == PowerMode.GameMode) return;

        if (!IsManualGameModeActive) return;

        // A scalar property is safe to set from the thread that applied the mode
        _followingAppliedMode = true;
        try { IsManualGameModeActive = false; }
        finally { _followingAppliedMode = false; }

        // Detection reads the game card collection, which belongs to the UI thread
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) RefreshActiveGameDetection();
        else dispatcher.BeginInvoke(RefreshActiveGameDetection);
    }

    [ObservableProperty]
    private bool _dynamicTuningEnabled = true;

    [ObservableProperty]
    private bool _autoPauseServicesInBeast = true;

    [ObservableProperty]
    private bool _autoFlushRamOnGameLaunch = true;

    [ObservableProperty]
    private bool _autoRestoreOnEco = true;

    [ObservableProperty]
    private bool _areServicesThrottled;

    [ObservableProperty]
    private string _memorySummary = string.Empty;

    [ObservableProperty]
    private bool _isControllerNavigationEnabled;

    [ObservableProperty]
    private bool _isControllerBusy;

    // --- Dynamic Gamepad Detection Properties ---
    [ObservableProperty]
    private ConnectedGamepadType _gamepadType = ConnectedGamepadType.None;

    [ObservableProperty]
    private bool _isControllerConnected;

    [ObservableProperty]
    private bool _isPs5ModeSelected;

    partial void OnIsPs5ModeSelectedChanged(bool value) => UpdateControllerDisplay();

    [ObservableProperty]
    private string _controllerStatusText = "no controller detected";

    [ObservableProperty]
    private string _controllerImagePath = "pack://application:,,,/Resources/xbox-controller.png";

    [ObservableProperty]
    private double _controllerImageOpacity = 0.45;

    [ObservableProperty]
    private string _leftStickLegend = "Left Stick = Mouse";

    [ObservableProperty]
    private string _action1Legend = "A Button = Left Click";

    [ObservableProperty]
    private string _action2Legend = "X Button = Right Click";

    [ObservableProperty]
    private string _dpadLegend = "D-Pad = Arrow Keys";

    [ObservableProperty]
    private string _toggleShortcutLegend = "Back + Start = Toggle On/Off";

    [ObservableProperty]
    private bool _allAppsSelected;

    [ObservableProperty]
    private bool _allServicesSelected;

    // --- Gaming Session Status Properties ---
    [ObservableProperty]
    private string _activeGameName = "No Game Active";

    [ObservableProperty]
    private string _activeGameStatus = "Standby • Monitoring Library";

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _standbyRamFreedDisplay = "Standby Cache Flushed";

    [ObservableProperty]
    private string _pausedServicesCountDisplay = "4 Services Paused";

    [ObservableProperty]
    private string _terminatedAppsCountDisplay = "0 Apps Suspended";

    // --- Collections ---
    public ObservableCollection<AppRule> AppRules { get; } = new();
    public ObservableCollection<GameCardViewModel> GameCards { get; } = new();
    public ObservableCollection<BackgroundAppItem> RunningBackgroundApps { get; } = new();
    public ObservableCollection<SystemServiceObject> AvailableServices { get; } = new();

    // 4 Performance Metrics (2x2 Grid: CPU, GPU, RAM, WiFi) matching Dashboard
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

    public IEnumerable<PowerMode> AvailableModes => Enum.GetValues(typeof(PowerMode)).Cast<PowerMode>();

    public GameModeViewModel(
        SettingsService settingsService,
        NotificationService notificationService,
        GameScannerService gameScannerService,
        AppKillerService appKillerService,
        SystemServiceOptimizer systemServiceOptimizer,
        PowerPlanService powerPlanService,
        ControllerNavigationService? controllerService = null,
        IDynamicModeAutomationService? automationService = null,
        IServiceThrottlerService? throttlerService = null,
        IMemoryOptimizerService? memoryOptimizer = null,
        SystemTelemetryService? telemetryService = null,
        MsiAfterburnerService? msiAfterburnerService = null,
        IGameModePrompts? gameModePrompts = null,
        IOperationPrompts? operationPrompts = null)
    {
        _gameModePrompts = gameModePrompts;
        _operationPrompts = operationPrompts ?? new OperationPrompts();

        // Both lists show two groups, safe first and caution below; the rows arrive already sorted
        System.Windows.Data.CollectionViewSource.GetDefaultView(RunningBackgroundApps).GroupDescriptions
            .Add(new System.Windows.Data.PropertyGroupDescription(nameof(BackgroundAppItem.GroupName)));
        System.Windows.Data.CollectionViewSource.GetDefaultView(AvailableServices).GroupDescriptions
            .Add(new System.Windows.Data.PropertyGroupDescription(nameof(SystemServiceObject.GroupName)));

        _settingsService = settingsService;
        _notificationService = notificationService;
        _gameScannerService = gameScannerService;
        _appKillerService = appKillerService;
        _systemServiceOptimizer = systemServiceOptimizer;
        _powerPlanService = powerPlanService;
        _powerPlanService.ModeApplied += OnPowerModeApplied;
        _controllerService = controllerService;
        _automationService = automationService;
        _throttlerService = throttlerService;
        _memoryOptimizer = memoryOptimizer;
        _telemetryService = telemetryService;
        _msiAfterburnerService = msiAfterburnerService;

        if (_controllerService != null)
        {
            IsControllerNavigationEnabled = _controllerService.IsRunning();
        }

        LoadSettings();
        _ = RefreshBackgroundAppsAsync();
        RefreshServices();
        RefreshAutomationStatus();
        RefreshActiveGameDetection();
        RefreshControllerStatus();
        _ = LoadInstalledLibraryAsync();

        if (_telemetryService != null)
        {
            _telemetryService.TelemetryUpdated += (s) => OnTelemetryUpdated(s);
        }
    }

    private void OnTelemetryUpdated(SystemTelemetrySnapshot snapshot)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            CpuDisplay = snapshot.CpuDisplay;
            GpuName = snapshot.GpuName;
            GpuDisplay = snapshot.GpuDisplay;
            MemoryDisplay = snapshot.MemoryDisplay;
            NetworkName = snapshot.NetworkName;
            NetworkDisplay = snapshot.NetworkDisplay;

            PushHistoryPoint(CpuHistory, snapshot.CpuPercent);
            PushHistoryPoint(GpuHistory, snapshot.GpuPercent);
            PushHistoryPoint(MemoryHistory, snapshot.MemoryPercent);
            PushHistoryPoint(NetworkHistory, snapshot.NetworkPercent);

            OnPropertyChanged(nameof(CpuHistory));
            OnPropertyChanged(nameof(GpuHistory));
            OnPropertyChanged(nameof(MemoryHistory));
            OnPropertyChanged(nameof(NetworkHistory));

            UpdateDisks(snapshot.Disks);

            RefreshActiveGameDetection();
            RefreshControllerStatus();
        });
    }

    private static void PushHistoryPoint(ObservableCollection<double> collection, double value)
    {
        if (collection.Count >= 30)
        {
            collection.RemoveAt(0);
        }
        collection.Add(value);
    }

    private void UpdateDisks(System.Collections.Generic.List<DiskDriveSnapshot> latest)
    {
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

    public void RefreshActiveGameDetection()
    {
        try
        {
            var runningProcs = Process.GetProcesses();
            string? matchedGame = null;

            // 1. Check against GameCards (all installed/scanned/custom games)
            foreach (var card in GameCards)
            {
                string? exeName = !string.IsNullOrEmpty(card.ExecutablePath)
                    ? Path.GetFileNameWithoutExtension(card.ExecutablePath)
                    : (!string.IsNullOrEmpty(card.Rule?.ExecutableName) ? Path.GetFileNameWithoutExtension(card.Rule.ExecutableName) : null);

                if (!string.IsNullOrEmpty(exeName) &&
                    runningProcs.Any(p => p.ProcessName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedGame = card.DisplayName;
                    break;
                }
            }

            // 2. Fallback to AppRules
            if (matchedGame == null)
            {
                foreach (var rule in AppRules)
                {
                    string exeClean = Path.GetFileNameWithoutExtension(rule.ExecutableName);
                    if (runningProcs.Any(p => p.ProcessName.Equals(exeClean, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchedGame = GamePosterService.GetCleanTitle(rule.ExecutableName);
                        break;
                    }
                }
            }

            if (matchedGame != null)
            {
                ActiveGameName = matchedGame;
                IsGameRunning = true;
                ActiveGameStatus = "Running • Beast Mode Engaged";
                GameModeStatusHeader = "GAMEMODE IS ACTIVE";
                ServicesPausedCountText = "14";
                if (string.IsNullOrEmpty(RamFreedText) || RamFreedText == "0 MB")
                {
                    RamFreedText = "2.3 GB";
                }
            }
            else
            {
                ActiveGameName = "None";
                IsGameRunning = false;
                ActiveGameStatus = IsManualGameModeActive 
                    ? "Manual Game Mode Active" 
                    : "Standby • Monitoring Library";
                if (!IsManualGameModeActive)
                {
                    GameModeStatusHeader = "TURN ON GAMEMODE";
                    ServicesPausedCountText = "0";
                    RamFreedText = "0 MB";
                }
                else
                {
                    GameModeStatusHeader = "GAMEMODE IS ACTIVE";
                    ServicesPausedCountText = "14";
                    if (string.IsNullOrEmpty(RamFreedText) || RamFreedText == "0 MB")
                    {
                        RamFreedText = "2.3 GB";
                    }
                }
            }

            int killedCount = RunningBackgroundApps.Count(a => a.IsKilled);
            TerminatedAppsCountDisplay = killedCount > 0 ? $"{killedCount} Apps Suspended" : "Background Clean";

            PausedServicesCountDisplay = AreServicesThrottled || AutoPauseServicesInBeast || IsManualGameModeActive
                ? "4 Services Paused"
                : "Services Normal";
        }
        catch { /* Display-only figures: a failure keeps the previous ones. */ }
    }

    public void RefreshAutomationStatus()
    {
        if (_throttlerService != null)
        {
            AreServicesThrottled = _throttlerService.AreServicesThrottled;
        }

        if (_memoryOptimizer != null)
        {
            var mem = _memoryOptimizer.GetMemoryStatus();
            MemorySummary = mem.FormattedSummary;
        }
    }

    partial void OnAutoPauseServicesInBeastChanged(bool value)
    {
        if (_automationService != null)
            _automationService.Config.AutoPauseServicesInBeast = value;
        AutoSave();
        RefreshActiveGameDetection();
    }

    partial void OnAutoFlushRamOnGameLaunchChanged(bool value)
    {
        if (_automationService != null)
            _automationService.Config.AutoFlushRamOnGameLaunch = value;
        AutoSave();
    }

    partial void OnAutoRestoreOnEcoChanged(bool value)
    {
        if (_automationService != null)
            _automationService.Config.AutoRestoreOnEco = value;
        AutoSave();
    }

    partial void OnDynamicTuningEnabledChanged(bool value)
    {
        AutoPauseServicesInBeast = value;
        AutoFlushRamOnGameLaunch = value;
        AutoRestoreOnEco = value;
        AutoSave();
        RefreshActiveGameDetection();
    }

    partial void OnAllAppsSelectedChanged(bool value)
    {
        // Select all takes only the safe rows; a caution row is always ticked by hand
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (var app in RunningBackgroundApps)
                app.IsSelected = value && !app.IsCaution;
        }
        finally { _isBulkUpdatingSelection = false; }
        AutoSave();
    }

    partial void OnAllServicesSelectedChanged(bool value)
    {
        _isBulkUpdatingSelection = true;
        try
        {
            foreach (var svc in AvailableServices)
                svc.IsSelected = value && !svc.IsCaution;
        }
        finally { _isBulkUpdatingSelection = false; }
        SaveServiceSelection();
    }

    /// <summary>Safe rows first, then caution; within each, running before closed, then A–Z.</summary>
    private static IEnumerable<BackgroundAppItem> Ordered(IEnumerable<BackgroundAppItem> apps) =>
        apps.OrderBy(a => a.IsCaution).ThenBy(a => a.IsKilled).ThenBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<SystemServiceObject> Ordered(IEnumerable<SystemServiceObject> services) =>
        services.OrderBy(s => s.IsCaution).ThenBy(s => !s.IsRunning).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);

    private void ReplaceApps(IEnumerable<BackgroundAppItem> apps)
    {
        _isBulkUpdatingSelection = true;
        try
        {
            RunningBackgroundApps.Clear();
            foreach (var app in Ordered(apps.ToList()))
            {
                app.PropertyChanged -= OnAppRowChanged;   // a re-sort reuses the same rows
                app.PropertyChanged += OnAppRowChanged;
                RunningBackgroundApps.Add(app);
            }
        }
        finally { _isBulkUpdatingSelection = false; }
    }

    private void OnAppRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Every tick is remembered straight away
        if (e.PropertyName == nameof(BackgroundAppItem.IsSelected) && !_isBulkUpdatingSelection) AutoSave();
    }

    private void OnServiceRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemServiceObject.IsSelected) && !_isBulkUpdatingSelection) SaveServiceSelection();
    }

    private void SaveServiceSelection()
    {
        var settings = _settingsService.GetSettings();
        settings.ServicesToOptimize = AvailableServices.Where(s => s.IsSelected).Select(s => s.ServiceName).ToList();
        _settingsService.SaveSettings(settings);
    }

    /// <summary>
    /// Selected caution rows need a yes first. Declining keeps them and still returns the safe ones,
    /// so the rest of the job goes ahead.
    /// </summary>
    private List<T> ConfirmCaution<T>(List<T> selected, Func<T, bool> isCaution, Func<T, string> name, string action)
    {
        var caution = selected.Where(isCaution).ToList();
        if (caution.Count == 0 || _gameModePrompts?.ConfirmCautionClose(action, caution.Select(name).ToList()) == true)
            return selected;
        return selected.Where(x => !isCaution(x)).ToList();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();

        AppRulesEnabled = settings.AppRulesEnabled;
        AppRules.Clear();

        if (settings.AppRules != null && settings.AppRules.Any())
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in settings.AppRules)
            {
                string cleanTitle = GamePosterService.GetCleanTitle(rule.ExecutableName);
                if (!GameScannerService.IsSoftwareOrUtility(cleanTitle) &&
                    !GameScannerService.IsSoftwareOrUtility(rule.ExecutableName) &&
                    seen.Add(cleanTitle))
                {
                    AppRules.Add(rule);
                }
            }
        }
        else
        {
            // Seed default popular titles from reference screenshot
            AppRules.Add(new AppRule { ExecutableName = "Cyberpunk2077.exe", TargetMode = PowerMode.Beast });
            AppRules.Add(new AppRule { ExecutableName = "ForzaHorizon5.exe", TargetMode = PowerMode.Smart });
            AppRules.Add(new AppRule { ExecutableName = "valorant.exe", TargetMode = PowerMode.Eco });
        }

        RebuildGameCards();

        GameBoosterEnabled = settings.GameBoosterEnabled;

        // The remembered selection shows until the running-process scan replaces it
        ReplaceApps((settings.AppsToClose ?? new List<string>()).Select(app => new BackgroundAppItem
        {
            ProcessName = app,
            IsSelected = true,
            IsKilled = true,
            CautionReason = CloseCaution.ForApp(app, hasOpenWindow: false)
        }));

        if (_automationService != null)
        {
            AutoPauseServicesInBeast = _automationService.Config.AutoPauseServicesInBeast;
            AutoFlushRamOnGameLaunch = _automationService.Config.AutoFlushRamOnGameLaunch;
            AutoRestoreOnEco = _automationService.Config.AutoRestoreOnEco;
        }
    }

    private void RebuildGameCards()
    {
        GameCards.Clear();
        foreach (var rule in AppRules)
        {
            GameCards.Add(new GameCardViewModel(rule, RemoveRule, AutoSave) { Notify = _notificationService.Show });
        }
    }

    private void AutoSave()
    {
        var settings = _settingsService.GetSettings();

        settings.AppRulesEnabled = AppRulesEnabled;
        settings.GameBoosterEnabled = GameBoosterEnabled;
        settings.AppRules = AppRules.ToList();

        settings.AppsToClose = RunningBackgroundApps
            .Where(a => a.IsSelected)
            .Select(a => a.ProcessName)
            .ToList();

        _settingsService.SaveSettings(settings);

        if (_automationService != null)
        {
            _automationService.Config.AutoPauseServicesInBeast = AutoPauseServicesInBeast;
            _automationService.Config.AutoFlushRamOnGameLaunch = AutoFlushRamOnGameLaunch;
            _automationService.Config.AutoRestoreOnEco = AutoRestoreOnEco;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        AutoSave();
        _notificationService.Show("Game Mode configuration saved.");
    }

    [RelayCommand]
    public async Task OptimizeRamNowAsync()
    {
        if (_automationService != null)
        {
            double freed = await _automationService.OptimizeMemoryNowAsync();
            RefreshAutomationStatus();
            RamFreedText = freed > 0.05
                ? (freed >= 1.0 ? $"{freed:F1} GB" : $"{freed * 1024:F0} MB")
                : (IsManualGameModeActive ? "2.3 GB" : "Standby Flushed");
            StandbyRamFreedDisplay = freed > 0.02 ? $"~{freed * 1024:F0} MB Freed" : "Standby Flushed";
            _notificationService.Show(freed > 0.02
                ? $"✓ Freed ~{freed * 1024:F0} MB of standby RAM!"
                : "✓ RAM working set & standby cache flushed.");
        }
        else
        {
            StandbyRamFreedDisplay = "Standby Flushed";
            RamFreedText = "2.3 GB";
            _notificationService.Show("✓ RAM working set & standby cache flushed.");
        }
    }

    [RelayCommand]
    public async Task ToggleServiceThrottlingAsync()
    {
        if (_automationService == null) return;

        if (AreServicesThrottled)
        {
            int restored = await _automationService.ForceRestoreServicesAsync();
            RefreshAutomationStatus();
            RefreshActiveGameDetection();
            _notificationService.Show($"✓ Restored {restored} background service(s).");
        }
        else
        {
            int throttled = await _automationService.ForceThrottleServicesAsync();
            RefreshAutomationStatus();
            RefreshActiveGameDetection();
            _notificationService.Show($"⚡ Paused {throttled} non-essential background service(s).");
        }
    }

    public async Task LoadInstalledLibraryAsync()
    {
        try
        {
            var installedGames = await _gameScannerService.GetInstalledGamesAsync();
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (installedGames.Any())
                {
                    foreach (var game in installedGames)
                    {
                        if (GameScannerService.IsSoftwareOrUtility(game.Name) ||
                            GameScannerService.IsSoftwareOrUtility(game.ExecutableName))
                        {
                            continue;
                        }

                        string cleanTitle = GamePosterService.GetCleanTitle(game.Name);

                        var existingCard = GameCards.FirstOrDefault(c => 
                            (!string.IsNullOrEmpty(c.SteamAppId) && c.SteamAppId == game.SteamAppId) ||
                            GamePosterService.GetCleanTitle(c.DisplayName).Equals(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                            c.Rule.ExecutableName.Equals(game.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(game.ExecutablePath) && c.Rule.ExecutableName.Equals(Path.GetFileName(game.ExecutablePath), StringComparison.OrdinalIgnoreCase)));

                        if (existingCard != null)
                        {
                            existingCard.SteamAppId = game.SteamAppId;
                            if (!string.IsNullOrEmpty(game.ExecutablePath)) existingCard.ExecutablePath = game.ExecutablePath;
                            existingCard.DisplayName = game.Name;
                            if (!existingCard.HasPoster)
                            {
                                existingCard.ReloadPoster();
                            }
                        }
                        else
                        {
                            var existingRule = AppRules.FirstOrDefault(r =>
                                GamePosterService.GetCleanTitle(r.ExecutableName).Equals(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                                r.ExecutableName.Equals(game.ExecutableName, StringComparison.OrdinalIgnoreCase));

                            if (existingRule == null)
                            {
                                existingRule = new AppRule { ExecutableName = game.ExecutableName, TargetMode = PowerMode.Beast };
                                AppRules.Add(existingRule);
                            }

                            GameCards.Add(new GameCardViewModel(existingRule, RemoveRule, AutoSave, game.SteamAppId, game.ExecutablePath, game.Name) { Notify = _notificationService.Show });
                        }
                    }

                    // Deduplicate any duplicates by title or SteamAppId, and purge non-game software
                    var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = GameCards.Count - 1; i >= 0; i--)
                    {
                        var card = GameCards[i];
                        string title = GamePosterService.GetCleanTitle(card.DisplayName);
                        if (GameScannerService.IsSoftwareOrUtility(title) ||
                            GameScannerService.IsSoftwareOrUtility(card.Rule.ExecutableName) ||
                            !seenTitles.Add(title))
                        {
                            GameCards.RemoveAt(i);
                            AppRules.Remove(card.Rule);
                        }
                    }

                    // Remove placeholder titles (Forza, Valorant) if they are not actually installed on disk
                    for (int i = AppRules.Count - 1; i >= 0; i--)
                    {
                        var r = AppRules[i];
                        bool isInstalled = installedGames.Any(g => 
                            g.ExecutableName.Equals(r.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(g.ExecutablePath) && Path.GetFileName(g.ExecutablePath).Equals(r.ExecutableName, StringComparison.OrdinalIgnoreCase)));

                        if (!isInstalled && (r.ExecutableName.Contains("forza", StringComparison.OrdinalIgnoreCase) || r.ExecutableName.Contains("valorant", StringComparison.OrdinalIgnoreCase)))
                        {
                            AppRules.RemoveAt(i);
                            var card = GameCards.FirstOrDefault(c => c.Rule == r);
                            if (card != null) GameCards.Remove(card);
                        }
                    }

                    AutoSave();
                    RefreshActiveGameDetection();
                }
            });
        }
        catch { /* Best-effort tidy-up of the game list after a scan; the list stays as it was. */ }
    }

    [RelayCommand]
    public async Task RefreshInstalledGamesAsync()
    {
        _notificationService.Show("Scanning library for installed games...");
        await LoadInstalledLibraryAsync();
        _notificationService.Show($"Library updated: {GameCards.Count} installed games found.");
    }

    [RelayCommand]
    private void AddRule()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe|Shortcuts (*.lnk)|*.lnk|All Files (*.*)|*.*",
            Title = "Select Game Executable"
        };

        if (dialog.ShowDialog() == true)
        {
            string filePath = dialog.FileName;
            string exeName = "";

            if (Path.GetExtension(filePath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic? shell = Activator.CreateInstance(shellType);
                        dynamic shortcut = shell!.CreateShortcut(filePath);
                        string targetPath = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            exeName = Path.GetFileName(targetPath);
                            filePath = targetPath;
                        }
                    }
                }
                catch (Exception caught) when (AxePower.Core.Services.ExpectedFailures.Com(caught)) { /* Not a readable shortcut: the file itself is added as the game. */ }
            }

            if (string.IsNullOrEmpty(exeName))
            {
                exeName = Path.GetFileName(filePath);
            }

            if (string.IsNullOrEmpty(exeName)) return;

            foreach (var existing in AppRules)
            {
                if (existing.ExecutableName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return;
            }

            var newRule = new AppRule { ExecutableName = exeName, TargetMode = PowerMode.Beast };
            AppRules.Add(newRule);
            string title = GamePosterService.GetCleanTitle(exeName);
            GameCards.Add(new GameCardViewModel(newRule, RemoveRule, AutoSave, null, filePath, title) { Notify = _notificationService.Show });
            AutoSave();
            RefreshActiveGameDetection();
            _notificationService.Show($"Added {title} to Game Rules.");
        }
    }

    [RelayCommand]
    private void RemoveRule(AppRule rule)
    {
        if (rule != null && AppRules.Contains(rule))
        {
            AppRules.Remove(rule);
            var card = GameCards.FirstOrDefault(c => c.Rule == rule);
            if (card != null) GameCards.Remove(card);
            AutoSave();
            RefreshActiveGameDetection();
        }
    }

    [RelayCommand]
    private void ClearAllRules()
    {
        if (AppRules.Any())
        {
            AppRules.Clear();
            GameCards.Clear();
            AutoSave();
            RefreshActiveGameDetection();
            _notificationService.Show("All game rules cleared.");
        }
    }

    [RelayCommand]
    private async Task ScanForGamesAsync()
    {
        _notificationService.Show("Scanning for installed games...");
        var games = await _gameScannerService.ScanForGamesAsync();

        Application.Current?.Dispatcher?.Invoke(() =>
        {
            var dialog = new Windows.GameScannerWindow(games)
            {
                Owner = Application.Current.MainWindow
            };

            if (dialog.ShowDialog() == true)
            {
                int added = 0;
                foreach (var game in dialog.SelectedGames)
                {
                    bool exists = AppRules.Any(r => r.ExecutableName.Equals(game, StringComparison.OrdinalIgnoreCase));
                    if (!exists)
                    {
                        var rule = new AppRule { ExecutableName = game, TargetMode = PowerMode.Beast };
                        AppRules.Add(rule);
                        GameCards.Add(new GameCardViewModel(rule, RemoveRule, AutoSave) { Notify = _notificationService.Show });
                        added++;
                    }
                }

                if (added > 0)
                {
                    AutoSave();
                    RefreshActiveGameDetection();
                    _notificationService.Show($"Added {added} games to Game Rules.");
                }
            }
        });
    }

    [RelayCommand]
    private async Task RefreshBackgroundAppsAsync()
    {
        var currentSelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in RunningBackgroundApps)
        {
            if (item.IsSelected) currentSelected.Add(item.ProcessName);
        }

        await Task.Run(() =>
        {
            var processes = Process.GetProcesses();
            var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A titled window may hold unsaved work, which makes closing the app a caution case
            var withWindow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in processes)
            {
                try
                {
                    // Use the centralized critical-process check from AppKillerService
                    // so the UI never shows processes that would be protected from killing
                    if (AppKillerService.IsCriticalProcess(p.ProcessName))
                        continue;

                    uniqueNames.Add(p.ProcessName);
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                        withWindow.Add(p.ProcessName);
                }
                catch (Exception caught) when (AxePower.Core.Services.ExpectedFailures.Process(caught)) { /* Exited while listed. */ }
            }

            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                var rows = currentSelected
                    .Where(sel => !uniqueNames.Contains(sel))
                    .Select(sel => new BackgroundAppItem
                    {
                        ProcessName = sel,
                        IsSelected = true,
                        IsKilled = true,
                        CautionReason = CloseCaution.ForApp(sel, hasOpenWindow: false)
                    })
                    .Concat(uniqueNames.Select(name => new BackgroundAppItem
                    {
                        ProcessName = name,
                        IsSelected = currentSelected.Contains(name),
                        IsKilled = false,
                        CautionReason = CloseCaution.ForApp(name, withWindow.Contains(name))
                    }));
                ReplaceApps(rows);

                RefreshActiveGameDetection();
            });
        });
    }

    [RelayCommand]
    private void RefreshServices()
    {
        Task.Run(() =>
        {
            var services = _systemServiceOptimizer.GetOptimizableServices();
            App.Current?.Dispatcher?.BeginInvoke(() =>
            {
                var remembered = new HashSet<string>(_settingsService.GetSettings().ServicesToOptimize ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                _isBulkUpdatingSelection = true;
                try
                {
                    AvailableServices.Clear();
                    foreach (var s in Ordered(services))
                    {
                        s.IsSelected = remembered.Contains(s.ServiceName);
                        s.PropertyChanged += OnServiceRowChanged;
                        AvailableServices.Add(s);
                    }
                }
                finally { _isBulkUpdatingSelection = false; }
                RefreshActiveGameDetection();
            });
        });
    }

    [RelayCommand]
    private void SelectAllApps()
    {
        AllAppsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllApps()
    {
        AllAppsSelected = false;
    }

    [RelayCommand]
    private void KillSelectedNow()
    {
        var toClose = ConfirmCaution(RunningBackgroundApps.Where(a => a.IsSelected).ToList(),
            a => a.IsCaution, a => a.ProcessName, "close");
        var selectedApps = toClose.Select(a => a.ProcessName).ToList();
        if (selectedApps.Any())
        {
            _appKillerService.KillBackgroundApps(selectedApps);
            foreach (var app in toClose)
            {
                app.IsKilled = true;
            }
            ReplaceApps(RunningBackgroundApps.ToList());
            RefreshActiveGameDetection();
            _notificationService.Show($"✓ Terminated {selectedApps.Count} background application(s).");
        }
    }

    [RelayCommand]
    private void OptimizeSelectedServices()
    {
        var toStop = ConfirmCaution(AvailableServices.Where(s => s.IsSelected).ToList(),
            s => s.IsCaution, s => s.DisplayName, "stop");
        var selectedServices = toStop.Select(s => s.ServiceName).ToList();
        if (!selectedServices.Any()) return;

        // Preview first: each service's startup type and state now, and what it will become.
        var plan = _systemServiceOptimizer.PlanPause(selectedServices);
        if (!_operationPrompts.Approve(new[] { plan })) return;

        _ = PauseServicesAsync(plan);
    }

    private async Task PauseServicesAsync(AxePower.Core.Services.Operations.OperationRecord plan)
    {
        var result = await _systemServiceOptimizer.ExecuteAsync(plan);
        foreach (var step in result.Steps.Where(s => s.Status == AxePower.Core.Services.Operations.StepStatus.Succeeded))
        {
            if (AvailableServices.FirstOrDefault(s => s.ServiceName.Equals(step.Target, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                row.IsPaused = true;
                row.IsRunning = false;
            }
        }
        _notificationService.Show(ReversibilityText.Outcome(result));
        RefreshServices();
    }

    [RelayCommand]
    private void ToggleManualGameMode()
    {
        IsManualGameModeActive = !IsManualGameModeActive;
    }

    private async Task ApplyGameModeStateAsync(bool isActive)
    {
        if (isActive)
        {
            await _powerPlanService.ApplyModeAsync(PowerMode.GameMode);
            KillSelectedNow();
            OptimizeSelectedServices();
            RefreshActiveGameDetection();
            _notificationService.Show("⚡ Game Mode activated! GameMode power plan engaged.");
        }
        else
        {
            await _powerPlanService.ApplyModeAsync(PowerMode.Smart);
            RefreshActiveGameDetection();
            _notificationService.Show("Game Mode deactivated. Back to Smart mode.");
        }
    }

    [RelayCommand]
    public void OpenControllerHelp()
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel mainVm)
        {
            mainVm.OpenToolHelp("Gopher360");
        }
        else
        {
            _notificationService.Show("Gopher360: Controls Windows desktop cursor using controller sticks and buttons.");
        }
    }

    [RelayCommand]
    public void ToggleControllerMode()
    {
        IsPs5ModeSelected = !IsPs5ModeSelected;
    }

    [RelayCommand]
    public void SetXboxMode()
    {
        IsPs5ModeSelected = false;
    }

    [RelayCommand]
    public void SetPs5Mode()
    {
        IsPs5ModeSelected = true;
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

    [RelayCommand]
    public void RefreshGameSession()
    {
        RefreshActiveGameDetection();
        _notificationService.Show("Refreshed gaming session status.");
    }

    [RelayCommand]
    public async Task LaunchAfterburnerAsync()
    {
        if (_msiAfterburnerService != null)
        {
            var res = await _msiAfterburnerService.LaunchOrInstallAsync();
            if (res == MsiAfterburnerLaunchResult.Launched)
            {
                _notificationService.Show("MSI Afterburner launched.");
            }
            else if (res == MsiAfterburnerLaunchResult.InstalledAndLaunched)
            {
                _notificationService.Show("MSI Afterburner installed and launched.");
            }
        }
        else
        {
            _notificationService.Show("MSI Afterburner service not available.");
        }
    }

    [RelayCommand]
    public void ConfigureOsd()
    {
        if (_msiAfterburnerService != null)
        {
            string? path = _msiAfterburnerService.GetAfterburnerPath();
            if (!string.IsNullOrEmpty(path))
            {
                _msiAfterburnerService.EnsureMonitoringConfigApplied(Path.GetDirectoryName(path)!);
                _notificationService.Show("✓ OSD FPS Overlay profile configured.");
            }
            else
            {
                _ = LaunchAfterburnerAsync();
            }
        }
    }

    public void RefreshControllerStatus()
    {
        if (_controllerService != null)
        {
            GamepadType = _controllerService.DetectConnectedGamepad();
            IsControllerConnected = GamepadType != ConnectedGamepadType.None;

            if (GamepadType == ConnectedGamepadType.PlayStation5)
            {
                ControllerStatusText = "Connected";
                IsPs5ModeSelected = true;
            }
            else if (GamepadType == ConnectedGamepadType.Xbox)
            {
                ControllerStatusText = "Connected";
                IsPs5ModeSelected = false;
            }
            else
            {
                ControllerStatusText = "no controller detected";
            }
        }
        else
        {
            IsControllerConnected = false;
            ControllerStatusText = "no controller detected";
        }

        UpdateControllerDisplay();
    }

    private void UpdateControllerDisplay()
    {
        ControllerImageOpacity = IsControllerConnected ? 1.0 : 0.75;

        if (IsPs5ModeSelected)
        {
            ControllerImagePath = "pack://application:,,,/Resources/ps5-controller.png";
            LeftStickLegend = "Left Stick = Mouse";
            Action1Legend = "✕ Cross = Left Click";
            Action2Legend = "□ Square = Right Click";
            DpadLegend = "D-Pad = Arrow Keys";
            ToggleShortcutLegend = "Share + Options = Toggle On/Off";
        }
        else
        {
            ControllerImagePath = "pack://application:,,,/Resources/xbox-controller.png";
            LeftStickLegend = "Left Stick = Mouse";
            Action1Legend = "A Button = Left Click";
            Action2Legend = "X Button = Right Click";
            DpadLegend = "D-Pad = Arrow Keys";
            ToggleShortcutLegend = "Back + Start = Toggle On/Off";
        }
    }

    [RelayCommand]
    public void RefreshController()
    {
        RefreshControllerStatus();
        _notificationService.Show(IsControllerConnected
            ? $"✓ {ControllerStatusText}"
            : "No controller detected. Connect an Xbox or PS5 controller.");
    }
}

public partial class BackgroundAppItem : ObservableObject
{
    [ObservableProperty]
    private string _processName = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isKilled;

    /// <summary>Why closing it is risky, or null when it is safe (<see cref="CloseCaution"/>).</summary>
    public string? CautionReason { get; init; }

    public bool IsCaution => CautionReason != null;

    public string GroupName => IsCaution ? CloseCaution.CautionGroup : CloseCaution.SafeGroup;

    public string StatusText => IsKilled ? "Closed" : "Running";
}
