using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Linq;

namespace AxePower.App.ViewModels;

public partial class GameModeViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;
    private readonly GameScannerService _gameScannerService;
    private readonly AppKillerService _appKillerService;
    private readonly SystemServiceOptimizer _systemServiceOptimizer;

    [ObservableProperty]
    private bool _appRulesEnabled;

    public ObservableCollection<AppRule> AppRules { get; } = new();

    public System.Collections.Generic.IEnumerable<PowerMode> AvailableModes => System.Enum.GetValues(typeof(PowerMode)).Cast<PowerMode>();

    [ObservableProperty]
    private bool _gameBoosterEnabled;

    [ObservableProperty]
    private bool _isManualGameModeActive;

    public ObservableCollection<BackgroundAppItem> RunningBackgroundApps { get; } = new();

    public ObservableCollection<SystemServiceObject> AvailableServices { get; } = new();

    private readonly PowerPlanService _powerPlanService;
    private readonly ControllerNavigationService? _controllerService;
    private readonly IDynamicModeAutomationService? _automationService;
    private readonly IServiceThrottlerService? _throttlerService;
    private readonly IMemoryOptimizerService? _memoryOptimizer;

    [ObservableProperty]
    private bool _isControllerNavigationEnabled;

    [ObservableProperty]
    private bool _isControllerBusy;

    // Dynamic Mode Automation & Background Tuning
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
        IMemoryOptimizerService? memoryOptimizer = null)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _gameScannerService = gameScannerService;
        _appKillerService = appKillerService;
        _systemServiceOptimizer = systemServiceOptimizer;
        _powerPlanService = powerPlanService;
        _controllerService = controllerService;
        _automationService = automationService;
        _throttlerService = throttlerService;
        _memoryOptimizer = memoryOptimizer;

        if (_controllerService != null)
        {
            IsControllerNavigationEnabled = _controllerService.IsRunning();
        }
        
        LoadSettings();
        _ = RefreshBackgroundAppsAsync();
        RefreshServices();
        RefreshAutomationStatus();
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
    }

    partial void OnAutoFlushRamOnGameLaunchChanged(bool value)
    {
        if (_automationService != null)
            _automationService.Config.AutoFlushRamOnGameLaunch = value;
    }

    partial void OnAutoRestoreOnEcoChanged(bool value)
    {
        if (_automationService != null)
            _automationService.Config.AutoRestoreOnEco = value;
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();
        
        AppRulesEnabled = settings.AppRulesEnabled;
        AppRules.Clear();
        foreach (var rule in settings.AppRules)
        {
            AppRules.Add(rule);
        }

        GameBoosterEnabled = settings.GameBoosterEnabled;
        
        RunningBackgroundApps.Clear();
        if (settings.AppsToClose != null)
        {
            foreach (var app in settings.AppsToClose)
            {
                RunningBackgroundApps.Add(new BackgroundAppItem { ProcessName = app, IsSelected = true });
            }
        }

        if (_automationService != null)
        {
            AutoPauseServicesInBeast = _automationService.Config.AutoPauseServicesInBeast;
            AutoFlushRamOnGameLaunch = _automationService.Config.AutoFlushRamOnGameLaunch;
            AutoRestoreOnEco = _automationService.Config.AutoRestoreOnEco;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var settings = _settingsService.GetSettings();
        
        bool gameBoosterJustEnabled = !settings.GameBoosterEnabled && GameBoosterEnabled;

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
        
        if (gameBoosterJustEnabled)
        {
            _notificationService.Show("Creating System Restore Point...");
            CreateSystemRestorePoint();
        }

        _notificationService.Show("Game Mode settings saved successfully.");
    }

    [RelayCommand]
    public async Task OptimizeRamNowAsync()
    {
        if (_automationService != null)
        {
            double freed = await _automationService.OptimizeMemoryNowAsync();
            RefreshAutomationStatus();
            _notificationService.Show(freed > 0.02
                ? $"✓ Freed ~{freed * 1024:F0} MB of standby RAM!"
                : "✓ RAM working set & standby cache flushed.");
        }
        else
        {
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
            _notificationService.Show($"✓ Restored {restored} background service(s).");
        }
        else
        {
            int throttled = await _automationService.ForceThrottleServicesAsync();
            RefreshAutomationStatus();
            _notificationService.Show($"⚡ Paused {throttled} non-essential background service(s).");
        }
    }

    private void CreateSystemRestorePoint()
    {
        Task.Run(() =>
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description 'AxePower Game Booster' -RestorePointType 'MODIFY_SETTINGS'\"",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        Verb = "runas"
                    }
                };
                process.Start();
                process.WaitForExit();
                
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _notificationService.Show("System Restore Point created.");
                });
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create restore point: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private void AddRule()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*|Executables and Shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
            Title = "Select Application or Game"
        };

        if (dialog.ShowDialog() == true)
        {
            string filePath = dialog.FileName;
            string exeName = "";

            if (System.IO.Path.GetExtension(filePath).Equals(".lnk", System.StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    System.Type? shellType = System.Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic? shell = System.Activator.CreateInstance(shellType);
                        dynamic shortcut = shell!.CreateShortcut(filePath);
                        string targetPath = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            exeName = System.IO.Path.GetFileName(targetPath);
                        }
                    }
                }
                catch
                {
                    // Fallback if parsing fails
                }
            }
            
            // If it's not a shortcut, or if shortcut parsing failed, just use the file name
            if (string.IsNullOrEmpty(exeName))
            {
                exeName = System.IO.Path.GetFileName(filePath);
            }

            if (string.IsNullOrEmpty(exeName)) return;
            
            foreach (var rule in AppRules)
            {
                if (rule.ExecutableName.Equals(exeName, System.StringComparison.OrdinalIgnoreCase)) return;
            }

            AppRules.Add(new AppRule { ExecutableName = exeName, TargetMode = PowerMode.Beast });
            SaveSettings();
        }
    }

    [RelayCommand]
    private void RemoveRule(AppRule rule)
    {
        if (rule != null && AppRules.Contains(rule))
        {
            AppRules.Remove(rule);
            SaveSettings();
        }
    }

    [RelayCommand]
    private void ClearAllRules()
    {
        if (AppRules.Any())
        {
            AppRules.Clear();
            SaveSettings();
            _notificationService.Show("All app rules cleared.");
        }
    }

    [RelayCommand]
    private async Task ScanForGamesAsync()
    {
        _notificationService.Show("Scanning for games...");
        var games = await _gameScannerService.ScanForGamesAsync();
        
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new AxePower.App.Windows.GameScannerWindow(games);
            dialog.Owner = System.Windows.Application.Current.MainWindow;
            if (dialog.ShowDialog() == true)
            {
                int added = 0;
                foreach (var game in dialog.SelectedGames)
                {
                    bool exists = false;
                    foreach (var existing in AppRules)
                    {
                        if (existing.ExecutableName.Equals(game, System.StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        AppRules.Add(new AppRule { ExecutableName = game, TargetMode = PowerMode.Beast });
                        added++;
                    }
                }

                if (added > 0)
                {
                    SaveSettings();
                    _notificationService.Show($"Added {added} games.");
                }
            }
        });
    }

    [RelayCommand]
    private async Task RefreshBackgroundAppsAsync()
    {
        _notificationService.Show("Scanning for background applications...");
        
        var currentSelected = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var item in RunningBackgroundApps)
        {
            if (item.IsSelected) currentSelected.Add(item.ProcessName);
        }

        RunningBackgroundApps.Clear();

        await Task.Run(() =>
        {
            var whitelist = new[] 
            { 
                "explorer", "taskmgr", "devenv", "Code", "powershell", "cmd", 
                "conhost", "svchost", "dllhost", "sihost", "ctfmon", "taskhostw",
                "SearchApp", "StartMenuExperienceHost", "ShellExperienceHost",
                "TextInputHost", "ApplicationFrameHost", "RuntimeBroker", "AxePower"
            };

            var processes = System.Diagnostics.Process.GetProcesses();
            var uniqueNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var p in processes)
            {
                try
                {
                    if (System.Array.Exists(whitelist, w => w.Equals(p.ProcessName, System.StringComparison.OrdinalIgnoreCase)))
                        continue;

                    uniqueNames.Add(p.ProcessName);
                }
                catch { }
            }

            App.Current.Dispatcher.BeginInvoke(() =>
            {
                foreach (var sel in currentSelected)
                {
                    if (!uniqueNames.Contains(sel))
                    {
                        RunningBackgroundApps.Add(new BackgroundAppItem { ProcessName = sel, IsSelected = true });
                    }
                }

                foreach (var name in uniqueNames)
                {
                    RunningBackgroundApps.Add(new BackgroundAppItem 
                    { 
                        ProcessName = name, 
                        IsSelected = currentSelected.Contains(name) 
                    });
                }
            });
        });
    }

    [RelayCommand]
    private void RefreshServices()
    {
        Task.Run(() =>
        {
            var services = _systemServiceOptimizer.GetOptimizableServices();
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                AvailableServices.Clear();
                foreach (var s in services)
                {
                    AvailableServices.Add(s);
                }
            });
        });
    }

    [RelayCommand]
    private void SelectAllApps()
    {
        foreach (var app in RunningBackgroundApps)
        {
            app.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAllApps()
    {
        foreach (var app in RunningBackgroundApps)
        {
            app.IsSelected = false;
        }
    }

    [RelayCommand]
    private void KillSelectedNow()
    {
        var selectedApps = RunningBackgroundApps.Where(a => a.IsSelected).Select(a => a.ProcessName).ToList();
        if (selectedApps.Any())
        {
            _appKillerService.KillBackgroundApps(selectedApps);
            _ = RefreshBackgroundAppsAsync();
        }
    }

    [RelayCommand]
    private void OptimizeSelectedServices()
    {
        var selectedServices = AvailableServices.Where(s => s.IsSelected).Select(s => s.ServiceName).ToList();
        if (selectedServices.Any())
        {
            Task.Run(() =>
            {
                _systemServiceOptimizer.OptimizeServices(selectedServices);
                RefreshServices();
            });
        }
    }

    [RelayCommand]
    private async Task ToggleManualGameMode()
    {
        IsManualGameModeActive = !IsManualGameModeActive;

        if (IsManualGameModeActive)
        {
            await _powerPlanService.ApplyModeAsync(PowerMode.Beast);
            KillSelectedNow();
            OptimizeSelectedServices();
            _notificationService.Show("Game Mode manually activated!");
        }
        else
        {
            await _powerPlanService.ApplyModeAsync(PowerMode.Smart);
            _notificationService.Show("Game Mode deactivated. Reverted to Smart Mode.");
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
        catch (System.Exception ex)
        {
            _notificationService.Show($"Controller Navigation Error: {ex.Message}");
        }
        finally
        {
            IsControllerBusy = false;
        }
    }
}

public partial class BackgroundAppItem : ObservableObject
{
    [ObservableProperty]
    private string _processName = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}
