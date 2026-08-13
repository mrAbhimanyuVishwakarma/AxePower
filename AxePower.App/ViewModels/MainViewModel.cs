using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NotificationService _notificationService;
    private readonly DriverUpdateService _driverUpdateService;
    private readonly MsiAfterburnerService _msiAfterburnerService;
    private readonly DebloatService _debloatService;
    private readonly UpdateService _updateService;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isNotificationVisible;

    public DashboardViewModel DashboardVm { get; }
    public AutoSwitchViewModel AutoSwitchVm { get; }
    public GameModeViewModel GameModeVm { get; }
    public BatteryViewModel BatteryVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public LogsViewModel LogsVm { get; }
    public AboutViewModel AboutVm { get; }

    public Style? DashboardActiveStyle => GetStyleForView(DashboardVm);
    public Style? AutoSwitchActiveStyle => GetStyleForView(AutoSwitchVm);
    public Style? GameModeActiveStyle => GetStyleForView(GameModeVm);
    public Style? BatteryActiveStyle => GetStyleForView(BatteryVm);
    public Style? LogsActiveStyle => GetStyleForView(LogsVm);
    public Style? SettingsActiveStyle => GetStyleForView(SettingsVm);
    public Style? AboutActiveStyle => GetStyleForView(AboutVm);

    public MainViewModel(
        DashboardViewModel dashboardVm, 
        AutoSwitchViewModel autoSwitchVm,
        GameModeViewModel gameModeVm,
        BatteryViewModel batteryVm,
        SettingsViewModel settingsVm, 
        LogsViewModel logsVm,
        AboutViewModel aboutVm,
        NotificationService notificationService,
        DriverUpdateService driverUpdateService,
        MsiAfterburnerService msiAfterburnerService,
        DebloatService debloatService,
        UpdateService updateService)
    {
        DashboardVm = dashboardVm;
        AutoSwitchVm = autoSwitchVm;
        GameModeVm = gameModeVm;
        BatteryVm = batteryVm;
        SettingsVm = settingsVm;
        LogsVm = logsVm;
        AboutVm = aboutVm;
        _notificationService = notificationService;
        _driverUpdateService = driverUpdateService;
        _msiAfterburnerService = msiAfterburnerService;
        _debloatService = debloatService;
        _updateService = updateService;

        _currentView = DashboardVm;

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
        OnPropertyChanged(nameof(ShowBatteryPill));
    }

    [RelayCommand]
    private void Navigate(string viewName)
    {
        CurrentView = viewName switch
        {
            "Dashboard" => DashboardVm,
            "AutoSwitch" => AutoSwitchVm,
            "GameMode" => GameModeVm,
            "Battery" => BatteryVm,
            "Settings" => SettingsVm,
            "Logs" => LogsVm,
            "About" => AboutVm,
            _ => DashboardVm
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

    private async void ShowNotification(string message)
    {
        NotificationMessage = message;
        IsNotificationVisible = true;
        await System.Threading.Tasks.Task.Delay(3000);
        IsNotificationVisible = false;
    }
}
