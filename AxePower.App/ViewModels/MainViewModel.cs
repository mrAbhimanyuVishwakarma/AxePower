using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NotificationService _notificationService;

    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isNotificationVisible;

    public DashboardViewModel DashboardVm { get; }
    public AutoSwitchViewModel AutoSwitchVm { get; }
    public BatteryViewModel BatteryVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public LogsViewModel LogsVm { get; }
    public AboutViewModel AboutVm { get; }

    public Style? DashboardActiveStyle => GetStyleForView(DashboardVm);
    public Style? AutoSwitchActiveStyle => GetStyleForView(AutoSwitchVm);
    public Style? BatteryActiveStyle => GetStyleForView(BatteryVm);
    public Style? LogsActiveStyle => GetStyleForView(LogsVm);
    public Style? SettingsActiveStyle => GetStyleForView(SettingsVm);
    public Style? AboutActiveStyle => GetStyleForView(AboutVm);

    public MainViewModel(
        DashboardViewModel dashboardVm, 
        AutoSwitchViewModel autoSwitchVm,
        BatteryViewModel batteryVm,
        SettingsViewModel settingsVm, 
        LogsViewModel logsVm,
        AboutViewModel aboutVm,
        NotificationService notificationService)
    {
        DashboardVm = dashboardVm;
        AutoSwitchVm = autoSwitchVm;
        BatteryVm = batteryVm;
        SettingsVm = settingsVm;
        LogsVm = logsVm;
        AboutVm = aboutVm;
        _notificationService = notificationService;

        _currentView = DashboardVm;

        _notificationService.OnShowNotification += (s, msg) => ShowNotification(msg);
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
            "Battery" => BatteryVm,
            "Settings" => SettingsVm,
            "Logs" => LogsVm,
            "About" => AboutVm,
            _ => DashboardVm
        };
    }

    private async void ShowNotification(string message)
    {
        NotificationMessage = message;
        IsNotificationVisible = true;
        await System.Threading.Tasks.Task.Delay(3000);
        IsNotificationVisible = false;
    }
}
