using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NotificationService _notificationService;
    private readonly DriverUpdateService _driverUpdateService;
    private readonly MsiAfterburnerService _msiAfterburnerService;
    private readonly DebloatService _debloatService;
    private readonly AndroidControlService _androidControlService;
    private readonly UpdateService _updateService;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isNotificationVisible;

    [ObservableProperty]
    private bool _isToolHelpModalOpen;

    [ObservableProperty]
    private AxePower.App.Models.ToolInstructionItem? _selectedToolHelp;

    public System.Collections.ObjectModel.ObservableCollection<AxePower.App.Models.ToolInstructionItem> AllToolGuides { get; } = new();

    public DashboardViewModel DashboardVm { get; }
    public AutoSwitchViewModel AutoSwitchVm { get; }
    public GameModeViewModel GameModeVm { get; }
    public BatteryViewModel BatteryVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public LogsViewModel LogsVm { get; }
    public AboutViewModel AboutVm { get; }
    public UninstallerViewModel UninstallerVm { get; }

    public Style? DashboardActiveStyle => GetStyleForView(DashboardVm);
    public Style? AutoSwitchActiveStyle => GetStyleForView(AutoSwitchVm);
    public Style? GameModeActiveStyle => GetStyleForView(GameModeVm);
    public Style? BatteryActiveStyle => GetStyleForView(BatteryVm);
    public Style? LogsActiveStyle => GetStyleForView(LogsVm);
    public Style? SettingsActiveStyle => GetStyleForView(SettingsVm);
    public Style? AboutActiveStyle => GetStyleForView(AboutVm);
    public Style? UninstallerActiveStyle => GetStyleForView(UninstallerVm);

    public MainViewModel(
        DashboardViewModel dashboardVm, 
        AutoSwitchViewModel autoSwitchVm,
        GameModeViewModel gameModeVm,
        BatteryViewModel batteryVm,
        SettingsViewModel settingsVm, 
        LogsViewModel logsVm,
        AboutViewModel aboutVm,
        UninstallerViewModel uninstallerVm,
        NotificationService notificationService,
        DriverUpdateService driverUpdateService,
        MsiAfterburnerService msiAfterburnerService,
        DebloatService debloatService,
        AndroidControlService androidControlService,
        UpdateService updateService)
    {
        DashboardVm = dashboardVm;
        AutoSwitchVm = autoSwitchVm;
        GameModeVm = gameModeVm;
        BatteryVm = batteryVm;
        SettingsVm = settingsVm;
        LogsVm = logsVm;
        AboutVm = aboutVm;
        UninstallerVm = uninstallerVm;
        _notificationService = notificationService;
        _driverUpdateService = driverUpdateService;
        _msiAfterburnerService = msiAfterburnerService;
        _debloatService = debloatService;
        _androidControlService = androidControlService;
        _updateService = updateService;

        _currentView = DashboardVm;

        InitializeToolGuides();

        _notificationService.OnShowNotification += (s, msg) => ShowNotification(msg);

        // Silent background update check after startup
        _ = CheckUpdateInBackgroundAsync();
    }

    private async System.Threading.Tasks.Task CheckUpdateInBackgroundAsync()
    {
        await System.Threading.Tasks.Task.Delay(3500);
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (result.HasUpdate)
            {
                AboutVm.IsUpdateAvailable = true;
                AboutVm.LatestVersionTag = result.LatestVersion;
                AboutVm.ReleaseTitle = result.ReleaseTitle;
                AboutVm.ReleaseNotes = result.ReleaseNotes;
                AboutVm.DownloadUrl = result.DownloadUrl;
                AboutVm.AssetName = result.AssetName;
                AboutVm.ReleaseUrl = result.ReleaseUrl;
                _notificationService.Show($"🚀 AxePower v{result.LatestVersion} update is available!");
            }
        }
        catch
        {
            // Ignore background check failures
        }
    }

    private Style? GetStyleForView(object viewVm)
    {
        if (Application.Current == null) return null;
        var styleName = CurrentView == viewVm ? "SidebarActiveButtonStyle" : "SidebarButtonStyle";
        return Application.Current.TryFindResource(styleName) as Style;
    }

    public bool ShowBatteryPill => CurrentView != AboutVm;

    partial void OnCurrentViewChanged(object? value)
    {
        OnPropertyChanged(nameof(DashboardActiveStyle));
        OnPropertyChanged(nameof(AutoSwitchActiveStyle));
        OnPropertyChanged(nameof(GameModeActiveStyle));
        OnPropertyChanged(nameof(BatteryActiveStyle));
        OnPropertyChanged(nameof(LogsActiveStyle));
        OnPropertyChanged(nameof(SettingsActiveStyle));
        OnPropertyChanged(nameof(AboutActiveStyle));
        OnPropertyChanged(nameof(UninstallerActiveStyle));
        OnPropertyChanged(nameof(ShowBatteryPill));
    }

    [RelayCommand]
    private void Navigate(string viewName)
    {
        CurrentView = viewName switch
        {
            "Dashboard"   => DashboardVm,
            "AutoSwitch"  => AutoSwitchVm,
            "GameMode"    => GameModeVm,
            "Battery"     => BatteryVm,
            "Settings"    => SettingsVm,
            "Logs"        => LogsVm,
            "About"       => AboutVm,
            "Uninstaller" => UninstallerVm,
            _             => DashboardVm
        };
    }

    [RelayCommand]
    private async Task OpenMsiAfterburner()
    {
        _notificationService.Show("Opening MSI Afterburner (minimized)...");
        var result = await _msiAfterburnerService.LaunchOrInstallAsync();
        switch (result)
        {
            case MsiAfterburnerLaunchResult.Launched:
                _notificationService.Show("MSI Afterburner launched in background (minimized).");
                break;
            case MsiAfterburnerLaunchResult.AlreadyRunning:
                _notificationService.Show("MSI Afterburner is already running.");
                break;
            case MsiAfterburnerLaunchResult.InstalledAndLaunched:
                _notificationService.Show("MSI Afterburner installed & configured with monitoring preset.");
                break;
            case MsiAfterburnerLaunchResult.Failed:
                _notificationService.Show("Could not launch or install MSI Afterburner.");
                break;
        }
    }

    [RelayCommand]
    private void UpdateDrivers()
    {
        var result = _driverUpdateService.LaunchDriverAndSystemUpdate();
        switch (result)
        {
            case DriverUpdateLaunchResult.StartedNew:
                _notificationService.Show("Update PC started in background (minimized).");
                break;
            case DriverUpdateLaunchResult.FocusedExisting:
                _notificationService.Show("Update PC is already running. Showing active window.");
                break;
            case DriverUpdateLaunchResult.CancelledByUser:
                _notificationService.Show("Update PC administrator request was cancelled.");
                break;
            case DriverUpdateLaunchResult.Failed:
                _notificationService.Show("Failed to launch Update PC.");
                break;
        }
    }

    [RelayCommand]
    private void Debloat()
    {
        _notificationService.Show("Opening Win11Debloat by Raphire...");
        bool launched = _debloatService.LaunchDebloatUtility();
        if (!launched)
        {
            _notificationService.Show("Debloat utility was cancelled or failed to start.");
        }
    }

    [RelayCommand]
    private void OpenDebloatCredit()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/Raphire/Win11Debloat.git",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open credit link: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenMsiCredit()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.msi.com/Landing/afterburner/graphics-cards",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open MSI site: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenAndroidControl()
    {
        _notificationService.Show("Opening Android Control (scrcpy)...");
        var result = await _androidControlService.LaunchOrInstallAsync();
        switch (result)
        {
            case AndroidControlLaunchResult.Launched:
                _notificationService.Show("Android Control (scrcpy) launched successfully.");
                break;
            case AndroidControlLaunchResult.AlreadyRunning:
                _notificationService.Show("Android Control is already running. Showing window.");
                break;
            case AndroidControlLaunchResult.DownloadedAndLaunched:
                _notificationService.Show("Latest scrcpy downloaded & launched successfully.");
                break;
            case AndroidControlLaunchResult.Failed:
                _notificationService.Show("Could not launch Android Control. Ensure your Android device is connected with USB debugging enabled.");
                break;
        }
    }

    [RelayCommand]
    private void OpenAndroidControlCredit()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/Genymobile/scrcpy.git",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open scrcpy repository: " + ex.Message);
        }
    }

    [RelayCommand]
    public void OpenToolHelp(string? toolId = null)
    {
        var found = !string.IsNullOrWhiteSpace(toolId)
            ? AllToolGuides.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedToolHelp = found ?? AllToolGuides.FirstOrDefault();
        IsToolHelpModalOpen = true;
    }

    [RelayCommand]
    public void CloseToolHelp()
    {
        IsToolHelpModalOpen = false;
    }

    [RelayCommand]
    public void OpenReadmeHelp()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open Readme: " + ex.Message);
        }
    }

    [RelayCommand]
    public void OpenSelectedToolCredit()
    {
        if (SelectedToolHelp == null || string.IsNullOrWhiteSpace(SelectedToolHelp.CreditUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = SelectedToolHelp.CreditUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open tool link: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task LaunchSelectedToolAsync()
    {
        if (SelectedToolHelp == null) return;
        IsToolHelpModalOpen = false;

        switch (SelectedToolHelp.Id)
        {
            case "AndroidControl":
                await OpenAndroidControl();
                break;
            case "MsiAfterburner":
                await OpenMsiAfterburner();
                break;
            case "Uninstaller":
                Navigate("Uninstaller");
                break;
            case "UpdatePc":
                UpdateDrivers();
                break;
            case "Debloat":
                Debloat();
                break;
        }
    }

    private void InitializeToolGuides()
    {
        AllToolGuides.Clear();

        // 1. Android Control
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "AndroidControl",
            Title = "Android Control",
            IconGlyph = "\uE8EA",
            Subtitle = "Instant Android Screen Mirroring & Low-Latency Control",
            Credit = "Powered by Genymobile (scrcpy)",
            CreditUrl = "https://github.com/Genymobile/scrcpy.git",
            Overview = "Displays and controls your Android devices (phones, tablets, Android TV) directly from this PC with ultra-low latency, full keyboard/mouse passthrough, and zero apps required on the phone.",
            Steps = new System.Collections.Generic.List<string>
            {
                "On your Android phone, open Settings > About Phone and tap 'Build Number' 7 times to enable Developer Options.",
                "Go to Settings > Developer Options and turn ON 'USB Debugging'.",
                "Connect your phone to your PC via USB cable (or enable Wireless ADB).",
                "Click 'Android Control' (or 'Launch Tool' below). AxePower will automatically fetch the latest scrcpy binary and open the device mirror window.",
                "Accept the 'Allow USB Debugging' prompt on your Android screen if prompted."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "Shortcut: Press Ctrl + F to toggle fullscreen mode.",
                "Shortcut: Press Ctrl + H to press the Home button, or Ctrl + B for Back.",
                "Drag & Drop APK files directly onto the mirror window to install them instantly.",
                "Drag & Drop files or photos onto the mirror window to transfer them to your phone."
            },
            LaunchButtonText = "Launch Android Control"
        });

        // 2. MSI Afterburner
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "MsiAfterburner",
            Title = "MSI Afterburner",
            ImageIconSource = "/Resources/icons/MSI Afterburner.png",
            Subtitle = "GPU Telemetry, In-Game Overlay & Thermal Fan Profiling",
            Credit = "Powered by Guru3D / MSI",
            CreditUrl = "https://www.msi.com/Landing/afterburner/graphics-cards",
            Overview = "The industry-standard graphics card utility for real-time in-game telemetry, FPS counter overlay, GPU temperature tracking, and customizable cooling fan curves.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Click 'MSI Afterburner' in the sidebar or 'Launch Tool' below.",
                "AxePower launches Afterburner in the background (minimized) with hardware monitoring enabled.",
                "Open Afterburner Settings > Monitoring tab to choose which metrics (FPS, GPU Temp, VRAM Usage, CPU Clock) display in the on-screen display (OSD).",
                "Launch your game — the real-time overlay will display your thermal and performance metrics."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "Keep MSI Afterburner running in the background to enforce custom cooling fan profiles during gaming sessions.",
                "Use the RivaTuner Statistics Server (RTSS) tray icon to customize overlay colors, font size, and screen placement."
            },
            LaunchButtonText = "Launch MSI Afterburner"
        });

        // 3. Uninstaller Pro
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "Uninstaller",
            Title = "Uninstaller Pro",
            IconGlyph = "\uE74D",
            Subtitle = "Deep Application Removal & Verified Leftover Cleaner",
            Credit = "Built into AxePower Pro",
            CreditUrl = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme",
            Overview = "A powerful, unified uninstaller that searches and completely removes Win32 desktop software, MSI packages, Windows Store (UWP/AppX) apps, and Winget packages while cleaning orphaned registry entries and leftover file junk.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Click 'Uninstaller Pro' from the left sidebar.",
                "Browse or search through all detected software across Win32, Windows Store, and Winget formats.",
                "Select one or more applications you wish to remove.",
                "Click 'Uninstall Selected' to run the uninstaller and perform deep leftover analysis.",
                "Review discovered leftover registry keys and files, then confirm cleanup with automatic registry backup safety."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "System-critical components are automatically protected and highlighted to prevent accidental system breakage.",
                "Automatic registry snapshots are preserved in case you ever want to restore uninstalled configurations."
            },
            LaunchButtonText = "Open Uninstaller Pro"
        });

        // 4. Update PC
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "UpdatePc",
            Title = "Update PC",
            IconGlyph = "\uE777",
            Subtitle = "Hardware Drivers, Windows Packages & GPU Updater",
            Credit = "Powered by Windows Driver & Winget Engine",
            CreditUrl = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme",
            Overview = "Scans Windows Update catalog and winget software repositories to find and install the latest stable device drivers, audio/chipset updates, and gaming runtimes.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Click 'Update PC' in the sidebar or 'Launch Tool' below.",
                "AxePower initiates a background scan for outdated drivers and core packages.",
                "Review pending device and driver updates.",
                "Allow the updates to download and install. Restart your PC if prompted to complete driver installation."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "Keep GPU and motherboard chipset drivers updated to minimize micro-stutters and maximize frame rates in modern titles."
            },
            LaunchButtonText = "Run Update PC"
        });

        // 5. Debloat
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "Debloat",
            Title = "Debloat Utility",
            IconGlyph = "\uE756",
            Subtitle = "Windows 11 Telemetry & Sponsored App Removal",
            Credit = "Powered by Win11Debloat (Raphire)",
            CreditUrl = "https://github.com/Raphire/Win11Debloat.git",
            Overview = "Clean, trusted utility that safely removes pre-installed sponsored Windows 11 bloatware, disables intrusive background telemetry, removes Bing search from the Start menu, and unregisters unnecessary background services.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Click 'Debloat' in the sidebar or 'Launch Tool' below.",
                "Click 'Yes' when the Windows Administrator (UAC) prompt appears.",
                "In the Win11Debloat interface, select 'Quick / Recommended Debloat' or customize individual toggles.",
                "Let the optimization complete, then restart your PC to apply all privacy and performance tweaks."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "You can restore default Windows apps (like Calculator or Microsoft Store) anytime using the utility's Restore tab."
            },
            LaunchButtonText = "Launch Debloat Utility"
        });
    }

    private async void ShowNotification(string message)
    {
        NotificationMessage = message;
        IsNotificationVisible = true;
        await System.Threading.Tasks.Task.Delay(3000);
        IsNotificationVisible = false;
    }
}
