using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notificationService;

    [ObservableProperty]
    private AppSettings _currentSettings;

    public IEnumerable<PowerMode> AvailableModes => Enum.GetValues(typeof(PowerMode)).Cast<PowerMode>();
    public IEnumerable<AppTheme> AvailableThemes => Enum.GetValues(typeof(AppTheme)).Cast<AppTheme>();
    public IEnumerable<CloseBehaviour> AvailableCloseBehaviours => Enum.GetValues(typeof(CloseBehaviour)).Cast<CloseBehaviour>();

    public SettingsViewModel(SettingsService settingsService, NotificationService notificationService)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _currentSettings = _settingsService.GetSettings();
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.SaveSettings(CurrentSettings);
        _notificationService.Show("Settings saved.");
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        CurrentSettings = new AppSettings();
        _notificationService.Show("Defaults restored. Click Save to apply.");
    }
}
