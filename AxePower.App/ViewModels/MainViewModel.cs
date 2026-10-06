using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.App.ViewModels.Music;
using AxePower.App.ViewModels.ShareScreen;

namespace AxePower.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NotificationService _notificationService;
    private readonly DriverUpdateService _driverUpdateService;
    private readonly MsiAfterburnerService _msiAfterburnerService;
    private readonly DebloatService _debloatService;
    private readonly AndroidControlService _androidControlService;
    private readonly UpdateService _updateService;
    private readonly SettingsService _settingsService;
    private readonly TerminalService _terminalService;

    [ObservableProperty]
    private object? _currentView;

    /// <summary>True when AxePower is already running elevated; hides the "Relaunch with Admin" button.</summary>
    public bool IsElevated { get; }
    public bool ShowRelaunchAdmin => !IsElevated;

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
    public AutoSwitchViewModel? AutoSwitchVm { get; }
    public GameModeViewModel GameModeVm { get; }
    public BatteryViewModel BatteryVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public LogsViewModel LogsVm { get; }
    public AboutViewModel AboutVm { get; }

    /// <summary>The optional AxePower account: sidebar item and the sign-in dialog over the window.</summary>
    public AccountViewModel? AccountVm { get; }
    public UninstallerViewModel UninstallerVm { get; }
    public SystemCareViewModel SystemCareVm { get; }
    public SystemOptimizerViewModel OptimizerVm => SystemCareVm.OptimizerVm;
    public SystemDoctorViewModel DoctorVm => SystemCareVm.DoctorVm;
    public DebloatViewModel DebloatVm { get; }
    public SoftwareHubViewModel SoftwareHubVm { get; }
    public PhoneHubViewModel PhoneHubVm { get; }
    public TerminalViewModel TerminalVm { get; }
    public ToolsViewModel? ToolsVm { get; }
    public RoutinesViewModel? RoutinesVm { get; }
    public MusicViewModel? MusicVm { get; }
    public ShareScreenViewModel? ShareScreenVm { get; }
    public ShareScreenViewModel? FriendsVm => ShareScreenVm;

    /// <summary>Shared Now Playing state for the title-bar player (null only in minimal test setups).</summary>
    public NowPlayingViewModel? NowPlaying { get; }

    /// <summary>
    /// The title-bar player shows when opened with "Music ▾" — except on the Music page, which
    /// has the full player, and in the shrunk monitor-widget window.
    /// </summary>
    public bool ShowHeaderPlayer =>
        NowPlaying?.IsHeaderPlayerOpen == true && !DashboardVm.IsShrunk && (MusicVm == null || CurrentView != MusicVm);

    /// <summary>
    /// The "Music ▾" toggle button in the titlebar is hidden when viewing the full Music page.
    /// </summary>
    public bool ShowMusicHeaderToggle =>
        NowPlaying != null && (MusicVm == null || CurrentView != MusicVm);

    [ObservableProperty]
    private bool _isMaintenanceExpanded;

    [ObservableProperty]
    private bool _isUtilitiesExpanded;

    private string _currentNavSection = "Dashboard";

    public Style? DashboardActiveStyle => GetStyleForView(DashboardVm);
    public Style? AutoSwitchActiveStyle => AutoSwitchVm != null ? GetStyleForView(AutoSwitchVm) : null;
    public Style? GameModeActiveStyle => GetStyleForView(GameModeVm);
    public Style? BatteryActiveStyle => GetStyleForView(BatteryVm);
    public Style? MusicActiveStyle => MusicVm != null ? GetStyleForView(MusicVm) : GetStyleForView(new object());
    public Style? LogsActiveStyle => GetStyleForView(LogsVm);
    public Style? SettingsActiveStyle => GetStyleForView(SettingsVm);
    public Style? AboutActiveStyle => GetStyleForView(AboutVm);
    public Style? UninstallerActiveStyle => GetSubItemStyleForView(UninstallerVm);
    public Style? SystemCareActiveStyle => GetSubItemStyleForView(SystemCareVm);
    public Style? DebloatActiveStyle => GetSubItemStyleForView(DebloatVm);
    public Style? TerminalActiveStyle => GetSubItemStyleForView(TerminalVm);
    public Style? ToolsActiveStyle => GetSubItemStyleForView((object?)ToolsVm ?? new object());
    public Style? SoftwareHubActiveStyle => GetStyleForView(SoftwareHubVm);
    public Style? UpdatePcActiveStyle => GetSubItemStyleForView(SoftwareHubVm);
    public Style? PhoneHubActiveStyle => (_currentNavSection is "PhoneHub" or "PhoneControl") && CurrentView == PhoneHubVm ? (Application.Current?.TryFindResource("SidebarActiveButtonStyle") as Style) : (Application.Current?.TryFindResource("SidebarButtonStyle") as Style);
    public Style? SecondMonitorActiveStyle => _currentNavSection == "SecondMonitor" && CurrentView == PhoneHubVm ? (Application.Current?.TryFindResource("SidebarActiveButtonStyle") as Style) : (Application.Current?.TryFindResource("SidebarButtonStyle") as Style);
    public Style? LanDropActiveStyle => GetStyleForView(PhoneHubVm);
    public Style? ShareScreenActiveStyle => ShareScreenVm != null ? GetStyleForView(ShareScreenVm) : GetStyleForView(new object());
    public Style? FriendsActiveStyle => ShareScreenActiveStyle;

    public string MaintenanceChevronGlyph => IsMaintenanceExpanded ? "\uE70D" : "\uE76C";
    public string UtilitiesChevronGlyph => IsUtilitiesExpanded ? "\uE70D" : "\uE76C";

    /// <summary>Below this window width the sidebar collapses to its icon rail by itself.</summary>
    public const double SidebarAutoCollapseWidth = 1200;

    /// <summary>The sidebar is a 72 px icon rail instead of the full 240 px list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMaintenanceItems), nameof(ShowUtilitiesItems))]
    private bool _isSidebarCollapsed;

    // The rail has no group headers to open, so it always shows every tool
    public bool ShowMaintenanceItems => IsMaintenanceExpanded || IsSidebarCollapsed;
    public bool ShowUtilitiesItems => IsUtilitiesExpanded || IsSidebarCollapsed;

    private bool? _windowWasNarrow;

    /// <summary>
    /// Auto-collapse acts only when the window crosses the width line, so a click always wins
    /// until the next crossing. Going wide restores the last manual choice.
    /// </summary>
    public void OnWindowWidthChanged(double width)
    {
        bool narrow = width < SidebarAutoCollapseWidth;
        if (_windowWasNarrow == narrow) return;
        _windowWasNarrow = narrow;
        IsSidebarCollapsed = narrow || _settingsService.GetSettings().SidebarCollapsed;
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;

        var settings = _settingsService.GetSettings();
        settings.SidebarCollapsed = IsSidebarCollapsed;
        _settingsService.SaveSettings(settings, notifyListeners: false);
    }

    public MainViewModel(
        DashboardViewModel dashboardVm, 
        GameModeViewModel gameModeVm,
        BatteryViewModel batteryVm,
        SettingsViewModel settingsVm, 
        LogsViewModel logsVm,
        AboutViewModel aboutVm,
        UninstallerViewModel uninstallerVm,
        SystemCareViewModel systemCareVm,
        DebloatViewModel debloatVm,
        SoftwareHubViewModel softwareHubVm,
        PhoneHubViewModel phoneHubVm,
        TerminalViewModel terminalVm,
        NotificationService notificationService,
        DriverUpdateService driverUpdateService,
        MsiAfterburnerService msiAfterburnerService,
        DebloatService debloatService,
        AndroidControlService androidControlService,
        UpdateService updateService,
        SettingsService settingsService,
        TerminalService terminalService,
        AutoSwitchViewModel? autoSwitchVm = null,
        MusicViewModel? musicVm = null,
        NowPlayingViewModel? nowPlaying = null,
        ShareScreenViewModel? shareScreenVm = null,
        ToolsViewModel? toolsVm = null,
        AccountViewModel? accountVm = null,
        RoutinesViewModel? routinesVm = null)
    {
        ToolsVm = toolsVm;
        if (ToolsVm != null) ToolsVm.NavigateTo = target => Navigate(target);
        RoutinesVm = routinesVm;
        if (RoutinesVm != null) RoutinesVm.NavigateTo = target => Navigate(target);
        AccountVm = accountVm;
        MusicVm = musicVm;
        ShareScreenVm = shareScreenVm;
        if (ShareScreenVm != null) ShareScreenVm.NavigateTo = target => Navigate(target);
        NowPlaying = nowPlaying;
        if (NowPlaying != null)
        {
            NowPlaying.NavigateToMusic = () => Navigate("Music");
            NowPlaying.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NowPlayingViewModel.IsHeaderPlayerOpen)) OnPropertyChanged(nameof(ShowHeaderPlayer));
            };
        }
        dashboardVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.IsShrunk)) OnPropertyChanged(nameof(ShowHeaderPlayer));
        };

        DashboardVm = dashboardVm;
        AutoSwitchVm = autoSwitchVm;
        GameModeVm = gameModeVm;
        BatteryVm = batteryVm;
        SettingsVm = settingsVm;
        LogsVm = logsVm;
        AboutVm = aboutVm;
        UninstallerVm = uninstallerVm;
        SystemCareVm = systemCareVm;
        DebloatVm = debloatVm;
        SoftwareHubVm = softwareHubVm;
        PhoneHubVm = phoneHubVm;
        TerminalVm = terminalVm;
        _terminalService = terminalService;
        IsElevated = TerminalService.IsProcessElevated();

        // Background features (Software Hub upgrades, Doctor diagnostics) ask to surface the Terminal.
        _terminalService.NavigateToTerminalRequested += () =>
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() => Navigate("Terminal")));

        _notificationService = notificationService;
        _driverUpdateService = driverUpdateService;
        _msiAfterburnerService = msiAfterburnerService;
        _debloatService = debloatService;
        _androidControlService = androidControlService;
        _updateService = updateService;
        _settingsService = settingsService;

        var startupSettings = _settingsService.GetSettings();
        IsMaintenanceExpanded = startupSettings.SidebarMaintenanceExpanded;
        IsUtilitiesExpanded = startupSettings.SidebarUtilitiesExpanded;
        IsSidebarCollapsed = startupSettings.SidebarCollapsed;

        _currentView = DashboardVm;
        NowPlaying?.SetHeaderPlayerPage(isDashboard: true);

        InitializeToolGuides();

        _notificationService.OnShowNotification += (s, msg) => ShowNotification(msg);

        // Silent background update check after startup and recurring hourly
        _ = CheckUpdateInBackgroundAsync();

        var updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1)
        };
        updateTimer.Tick += async (s, e) => await CheckUpdateInBackgroundAsync();
        updateTimer.Start();
    }

    private async System.Threading.Tasks.Task CheckUpdateInBackgroundAsync()
    {
        try
        {
            var settings = _settingsService.GetSettings();
            if (!settings.AutoCheckUpdates) return;

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

                bool downloaded = false;
                if (settings.AutoDownloadUpdates && !string.IsNullOrWhiteSpace(result.DownloadUrl) &&
                    (result.DownloadUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || result.DownloadUrl.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        // Silently download the update in background
                        var installerPath = await _updateService.DownloadUpdateAsync(result.DownloadUrl, new Progress<double>(), manifest: result.Manifest);
                        AboutVm.DownloadedInstallerPath = installerPath;
                        AboutVm.IsUpdateDownloaded = true;
                        AboutVm.SteamUpdateBannerMessage = $"AxePower v{result.LatestVersion} is downloaded and ready to install - ";
                        AboutVm.IsSteamUpdateBannerOpen = true;
                        downloaded = true;

                        if (settings.NotifyOnNewRelease)
                        {
                            _notificationService.Show($"🚀 AxePower v{result.LatestVersion} is downloaded and ready to install!");
                        }
                    }
                    catch
                    {
                        // Download failed — still tell the user an update exists (below)
                    }
                }

                if (!downloaded && settings.NotifyOnNewRelease)
                {
                    _notificationService.Show($"🚀 AxePower v{result.LatestVersion} update is available!");
                }
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

    private Style? GetSubItemStyleForView(object viewVm)
    {
        if (Application.Current == null) return null;
        var styleName = CurrentView == viewVm ? "SidebarSubItemActiveButtonStyle" : "SidebarSubItemButtonStyle";
        return Application.Current.TryFindResource(styleName) as Style;
    }

    // Battery status lives on the Dashboard only; other pages get the title bar space back
    public bool ShowBatteryPill => CurrentView == DashboardVm;

    partial void OnIsMaintenanceExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(MaintenanceChevronGlyph));
        OnPropertyChanged(nameof(ShowMaintenanceItems));
    }

    partial void OnIsUtilitiesExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(UtilitiesChevronGlyph));
        OnPropertyChanged(nameof(ShowUtilitiesItems));
    }

    [RelayCommand]
    private void ToggleMaintenanceGroup()
    {
        IsMaintenanceExpanded = !IsMaintenanceExpanded;

        var settings = _settingsService.GetSettings();
        settings.SidebarMaintenanceExpanded = IsMaintenanceExpanded;
        _settingsService.SaveSettings(settings, notifyListeners: false);
    }

    [RelayCommand]
    private void ToggleUtilitiesGroup()
    {
        IsUtilitiesExpanded = !IsUtilitiesExpanded;

        var settings = _settingsService.GetSettings();
        settings.SidebarUtilitiesExpanded = IsUtilitiesExpanded;
        _settingsService.SaveSettings(settings, notifyListeners: false);
    }

    partial void OnCurrentViewChanged(object? value)
    {
        OnPropertyChanged(nameof(DashboardActiveStyle));
        OnPropertyChanged(nameof(AutoSwitchActiveStyle));
        OnPropertyChanged(nameof(GameModeActiveStyle));
        OnPropertyChanged(nameof(BatteryActiveStyle));
        OnPropertyChanged(nameof(MusicActiveStyle));
        NowPlaying?.SetHeaderPlayerPage(isDashboard: value == DashboardVm);
        OnPropertyChanged(nameof(ShowHeaderPlayer));
        OnPropertyChanged(nameof(ShowMusicHeaderToggle));
        OnPropertyChanged(nameof(LogsActiveStyle));
        OnPropertyChanged(nameof(SettingsActiveStyle));
        OnPropertyChanged(nameof(AboutActiveStyle));
        OnPropertyChanged(nameof(UninstallerActiveStyle));
        OnPropertyChanged(nameof(SystemCareActiveStyle));
        OnPropertyChanged(nameof(DebloatActiveStyle));
        OnPropertyChanged(nameof(TerminalActiveStyle));
        OnPropertyChanged(nameof(ToolsActiveStyle));
        OnPropertyChanged(nameof(SoftwareHubActiveStyle));
        OnPropertyChanged(nameof(UpdatePcActiveStyle));
        OnPropertyChanged(nameof(PhoneHubActiveStyle));
        OnPropertyChanged(nameof(LanDropActiveStyle));
        OnPropertyChanged(nameof(ShareScreenActiveStyle));
        OnPropertyChanged(nameof(FriendsActiveStyle));
        OnPropertyChanged(nameof(ShowBatteryPill));
    }

    [RelayCommand]
    public async Task ApplyPowerModeAsync(string modeStr)
    {
        if (modeStr == "GameMode")
        {
            Navigate("GameMode");
        }
        await DashboardVm.ApplyModeDirectAsync(modeStr);
    }

    [RelayCommand]
    public void Navigate(string viewName)
    {
        // A target may carry deep-link arguments, e.g. "UpdatePc:Apps:Gaming" jumps to the
        // Software Hub's Gaming Runtimes tab with the Gaming category pre-selected.
        var parts = (viewName ?? string.Empty).Split(':');
        var baseKey = parts[0];
        var arg1 = parts.Length > 1 ? parts[1] : null;
        var arg2 = parts.Length > 2 ? parts[2] : null;

        _currentNavSection = baseKey;

        if (baseKey is "SystemCare" or "Optimizer" or "Doctor" or "Maintenance" or "Uninstaller" or "UpdatePc" or "SoftwareHub" or "Debloat")
        {
            IsMaintenanceExpanded = true;
        }
        else if (baseKey is "Utilities" or "Terminal" or "Tools" or "Routines")
        {
            IsUtilitiesExpanded = true;
        }

        CurrentView = baseKey switch
        {
            "Dashboard"   => DashboardVm,
            "AutoSwitch"  => DashboardVm,
            "GameMode"    => GameModeVm,
            "Battery"     => BatteryVm,
            "Music"       => (object?)MusicVm ?? DashboardVm,
            "Settings"    => ShowSettingsSection(arg1),
            "Logs"        => LogsVm,
            "About"       => AboutVm,   // older links; the page is Profile now
            "Profile"     => AboutVm,
            "Uninstaller" => UninstallerVm,
            "SystemCare"  => OpenSystemCare("Optimizer"),
            "Optimizer"   => OpenSystemCare("Optimizer"),
            "Doctor"      => OpenSystemCare("Doctor"),
            "Maintenance" => SwitchSoftwareHubTab(arg1, arg2),
            "Utilities"   => TerminalVm,
            "Debloat"     => DebloatVm,
            "Terminal"    => TerminalVm,
            "Tools"       => (object?)ToolsVm ?? DashboardVm,
            "Routines"    => (object?)RoutinesVm ?? DashboardVm,
            "SoftwareHub" => SwitchSoftwareHubTab(arg1, arg2),
            "UpdatePc"    => SwitchSoftwareHubTab(arg1, arg2),
            "ShareScreen" => (object?)ShareScreenVm ?? DashboardVm,
            "Friends"     => (object?)ShareScreenVm ?? DashboardVm,
            "PhoneControl"=> SwitchPhoneHubTab("ScreenMirror"),
            "PhoneHub"    => SwitchPhoneHubTab("ScreenMirror"),
            "SecondMonitor"=> SwitchPhoneHubTab("SecondMonitor"),
            "PhoneLink"   => SwitchPhoneHubTab("PhoneLink"),
            "LanDrop"     => SwitchPhoneHubTab("LanDrop"),
            "AndroidControl" => SwitchPhoneHubTab("ScreenMirror"),
            _             => DashboardVm
        };

        OnPropertyChanged(nameof(PhoneHubActiveStyle));
        OnPropertyChanged(nameof(SecondMonitorActiveStyle));

        // The sidebar's "Sign in" item: land on Profile with the sign-in dialog already open.
        if (baseKey == "Profile" && AccountVm is { IsSignedOut: true, IsDialogOpen: false }) AccountVm.OpenSignIn();
    }

    private object OpenSystemCare(string section)
    {
        SystemCareVm.SelectSection(section);
        return SystemCareVm;
    }

    /// <summary>
    /// Opens Settings, optionally asking the page to bring one card into view. Used by the Share
    /// Screen stage, whose gear sends the user here rather than duplicating the pickers.
    /// </summary>
    private object ShowSettingsSection(string? section)
    {
        SettingsVm.RequestedSection = section;
        // The page lives for the whole session, so pick up skins added since it was built
        SettingsVm.RefreshThemeChoices();
        return SettingsVm;
    }

    /// <summary>Relaunch AxePower elevated (one UAC prompt), then close this instance.</summary>
    [RelayCommand]
    private void RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current?.Shutdown();
        }
        catch (Win32Exception)
        {
            // User dismissed the UAC prompt — stay running as standard user.
            ShowNotification("Admin relaunch cancelled.");
        }
        catch (Exception ex)
        {
            ShowNotification($"Could not relaunch as admin: {ex.Message}");
        }
    }

    private object SwitchSoftwareHubTab(string? tab, string? category)
    {
        if (!string.IsNullOrWhiteSpace(tab)) SoftwareHubVm.SetTab(tab);
        if (!string.IsNullOrWhiteSpace(category)) SoftwareHubVm.SetCategory(category);
        return SoftwareHubVm;
    }

    private object SwitchPhoneHubTab(string tab)
    {
        PhoneHubVm.SetTab(tab);
        return PhoneHubVm;
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
        Navigate("Debloat");
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
    public void OpenToolHelp(string? toolId = null)
    {
        var found = !string.IsNullOrWhiteSpace(toolId)
            ? AllToolGuides.FirstOrDefault(t => t.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedToolHelp = found ?? AllToolGuides.FirstOrDefault();
        foreach (var guide in AllToolGuides) guide.IsSelected = ReferenceEquals(guide, SelectedToolHelp);
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
            string toolSection = SelectedToolHelp != null && !string.IsNullOrWhiteSpace(SelectedToolHelp.Id)
                ? SelectedToolHelp.Id.ToLowerInvariant()
                : "help";

            string helpUrl = $"https://axepower.vercel.app/help.html#{toolSection}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = helpUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _notificationService.Show("Failed to open Help Center: " + ex.Message);
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
            case "PhoneControl":
            case "PhoneHub":
            case "AndroidControl":
                Navigate("PhoneHub");
                break;
            case "LanDrop":
                Navigate("LanDrop");
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

        // 1. Phone Control
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "PhoneControl",
            Title = "Phone Control",
            IconGlyph = "\uE8EA",
            Subtitle = "Wi-Fi LAN Drop, Screen Mirroring & Remote Companion",
            Credit = "Powered by Genymobile & LAN-DropSpot",
            CreditUrl = "https://github.com/Genymobile/scrcpy.git",
            Overview = "An all-in-one mobile companion suite: transfer files and clipboard text instantly over local Wi-Fi without apps, mirror and control your Android screen with ultra-low latency, and control PC volume and lock state remotely.",
            Steps = new System.Collections.Generic.List<string>
            {
                "LAN Drop: Click 'Start Server' and scan the QR code with your phone camera to share files & clipboard.",
                "Android Mirroring: Enable USB Debugging on your phone and connect via USB or Wireless ADB to mirror your screen.",
                "Remote Controls: Use the mobile web portal to control PC media, audio volume, and lock the computer remotely."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "Shortcut: Press Ctrl + F on the mirror window to toggle fullscreen.",
                "Drag & Drop APK files directly onto the mirror window or Phone Control to install them.",
                "LAN Drop transfers occur at full local Wi-Fi bandwidth without using mobile data."
            },
            LaunchButtonText = "Open Phone Control"
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
                "Open Tools in the sidebar and click Open on MSI Afterburner, or use 'Launch Tool' below.",
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

        // 3. Uninstaller
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "Uninstaller",
            Title = "Uninstaller",
            IconGlyph = "\uE74D",
            Subtitle = "Deep Application Removal & Verified Leftover Cleaner",
            Credit = "Built into AxePower Pro",
            CreditUrl = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme",
            Overview = "Removes desktop programs, MSI packages and Store apps with their own uninstallers, checks that each one is really gone, and then offers only the leftovers it can tie to that app.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Click 'Uninstaller' from the left sidebar.",
                "Browse or search the installed apps; the Broken filter lists entries whose uninstaller is missing.",
                "Select one or more apps, or open one in the details panel.",
                "Click 'Uninstall', check the list of commands that will run, and confirm. Each app is verified afterwards.",
                "Review the leftovers found for removed apps; hover an item to see the evidence, then clean the ones you want."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "System components, AxePower itself and folders other apps still use are protected and can't be selected.",
                "Registry keys are exported to .reg backups before deletion and files go to the Recycle Bin; restore registry backups from History."
            },
            LaunchButtonText = "Open Uninstaller"
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
            Title = "Debloat & Windows Tools Hub",
            IconGlyph = "\uE756",
            Subtitle = "Win11Debloat (Raphire) & WinUtil (Chris Titus Tech)",
            Credit = "Powered by Win11Debloat (Raphire) & WinUtil (Chris Titus Tech)",
            CreditUrl = "https://github.com/Raphire/Win11Debloat.git",
            Overview = "A dedicated hub for trusted Windows debloat and optimization engines. Easily run Win11Debloat to strip telemetry and preinstalled apps, or WinUtil by Chris Titus Tech for all-in-one system tweaks and package management.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Click 'Debloat' in the sidebar to open the Debloat Hub page.",
                "Choose your preferred utility: Win11Debloat by Raphire or WinUtil by Chris Titus Tech.",
                "Hover over the info (i) icon to check author credit or click to view the source repository.",
                "Click the Launch button to run the tool elevated in PowerShell.",
                "Optionally click 'Create Restore Point' first as a safety checkpoint."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "Always create a System Restore point before applying deep system optimizations.",
                "You can restore default Windows apps anytime using Win11Debloat's Restore tab."
            },
            LaunchButtonText = "Open Debloat Hub"
        });

        // 6. Controller Navigation (Gopher360)
        AllToolGuides.Add(new AxePower.App.Models.ToolInstructionItem
        {
            Id = "Gopher360",
            Title = "Controller Navigation",
            IconGlyph = "\uE7FC",
            Subtitle = "Use Gamepad / Xbox Controller as Windows Mouse & Keyboard",
            Credit = "Powered by wishIwasabear (Gopher360)",
            CreditUrl = "https://github.com/wishIwasabear/Gopher360.git",
            Overview = "Turns your Xbox 360, Xbox One, or standard XInput gamepad controller into a mouse and keyboard on Windows, allowing effortless couch and handheld control.",
            Steps = new System.Collections.Generic.List<string>
            {
                "Connect your Xbox / XInput controller via Bluetooth or USB cable.",
                "Turn ON the 'Controller Navigation' toggle switch on the Dashboard.",
                "Left Thumbstick: Move the mouse cursor across your monitors.",
                "A Button: Left Mouse Click. | X Button: Right Mouse Click.",
                "Right Thumbstick: Scroll Up / Down and Left / Right.",
                "Y Button: Opens the Windows On-Screen Keyboard for text input.",
                "D-Pad: Arrow keys for directional navigation."
            },
            Tips = new System.Collections.Generic.List<string>
            {
                "Press 'Back + Start' buttons together on your controller anytime to toggle navigation ON or OFF.",
                "Works seamlessly across Windows desktop, browsers, media players, and games."
            },
            LaunchButtonText = "Toggle Controller Navigation"
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
