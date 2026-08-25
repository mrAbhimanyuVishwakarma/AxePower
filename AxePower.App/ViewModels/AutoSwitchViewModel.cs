using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace AxePower.App.ViewModels;

public partial class AutoSwitchViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly IDynamicModeAutomationService _automationService;
    private readonly IServiceThrottlerService _throttlerService;
    private readonly IMemoryOptimizerService _memoryOptimizer;
    private readonly UpdateService? _updateService;

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

    public ObservableCollection<PowerMode> AvailableModes { get; } = new(new[] { PowerMode.Eco, PowerMode.Smart, PowerMode.Beast, PowerMode.GameMode });

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

    // Phase 4: Dynamic Mode Automation
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

    public ObservableCollection<ManagedServiceItem> ManagedServices { get; } = new();

    public AutoSwitchViewModel(
        SettingsService settingsService, 
        NotificationService notificationService,
        IDynamicModeAutomationService automationService,
        IServiceThrottlerService throttlerService,
        IMemoryOptimizerService memoryOptimizer,
        UpdateService? updateService = null)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _automationService = automationService;
        _throttlerService = throttlerService;
        _memoryOptimizer = memoryOptimizer;
        _updateService = updateService;

        LoadSettings();
        RefreshAutomationStatus();
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

        AutoPauseServicesInBeast = _automationService.Config.AutoPauseServicesInBeast;
        AutoFlushRamOnGameLaunch = _automationService.Config.AutoFlushRamOnGameLaunch;
        AutoRestoreOnEco = _automationService.Config.AutoRestoreOnEco;
    }

    public void RefreshAutomationStatus()
    {
        AreServicesThrottled = _throttlerService.AreServicesThrottled;
        var mem = _memoryOptimizer.GetMemoryStatus();
        MemorySummary = mem.FormattedSummary;

        ManagedServices.Clear();
        foreach (var svc in _throttlerService.GetManagedServices())
        {
            ManagedServices.Add(svc);
        }
    }

    partial void OnAutoPauseServicesInBeastChanged(bool value)
    {
        _automationService.Config.AutoPauseServicesInBeast = value;
    }

    partial void OnAutoFlushRamOnGameLaunchChanged(bool value)
    {
        _automationService.Config.AutoFlushRamOnGameLaunch = value;
    }

    partial void OnAutoRestoreOnEcoChanged(bool value)
    {
        _automationService.Config.AutoRestoreOnEco = value;
    }

    [RelayCommand]
    public async Task OptimizeRamNowAsync()
    {
        double freed = await _automationService.OptimizeMemoryNowAsync();
        RefreshAutomationStatus();
        _notificationService.Show(freed > 0.02
            ? $"✓ Freed ~{freed * 1024:F0} MB of standby RAM!"
            : "✓ RAM working set & standby cache flushed.");
    }

    [RelayCommand]
    public async Task ToggleServiceThrottlingAsync()
    {
        if (AreServicesThrottled)
        {
            int restored = await _automationService.ForceRestoreServicesAsync();
            RefreshAutomationStatus();
            _notificationService.Show($"✓ Restored {restored} background service(s).");
        }
        else
        {
            int throttled = await _automationService.ForceThrottleServicesAsync();
            RefreshAutomationStatus();
            _notificationService.Show($"⚡ Paused {throttled} non-essential background service(s).");
        }
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
        _notificationService.Show("Auto Switch & Dynamic Mode settings saved.");
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
    private void Cancel()
    {
        LoadSettings();
        RefreshAutomationStatus();
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
}
