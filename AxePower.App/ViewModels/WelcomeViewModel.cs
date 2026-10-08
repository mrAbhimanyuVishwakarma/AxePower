using AxePower.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AxePower.App.ViewModels;

/// <summary>
/// The first-run welcome (Prompt 31). Each optional capability is explained where it is asked
/// for, pre-set to the recommended choice, and applied only on Continue. Closing the window
/// applies nothing: AxePower runs with nothing started, elevated or switched, and asks again
/// next time.
/// </summary>
public partial class WelcomeViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public WelcomeViewModel(SettingsService settings) => _settings = settings;

    /// <summary>Off: a startup entry is something the user should ask for.</summary>
    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>On: power plans, sensors, Game Mode's service changes and cleaning need it.</summary>
    [ObservableProperty]
    private bool _runAsAdministrator = true;

    /// <summary>On: AxePower's core feature. Explains that it creates and removes power plans.</summary>
    [ObservableProperty]
    private bool _automaticPowerModes = true;

    /// <summary>On: one request to GitHub at start-up; nothing is installed without asking.</summary>
    [ObservableProperty]
    private bool _checkForUpdates = true;

    /// <summary>Off: downloading an installer is left to the user.</summary>
    [ObservableProperty]
    private bool _downloadUpdatesAutomatically;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!value) DownloadUpdatesAutomatically = false;
    }

    /// <summary>True once Continue was chosen.</summary>
    public bool Completed { get; private set; }

    /// <summary>Saves the choices and marks the welcome as done.</summary>
    public void Continue()
    {
        var s = _settings.GetSettings();
        s.StartWithWindows = StartWithWindows;
        s.RunAsAdministrator = RunAsAdministrator;
        s.AutoSwitchEnabled = AutomaticPowerModes;
        s.ApplyExpectedModeOnStartup = AutomaticPowerModes;
        s.AutoCheckUpdates = CheckForUpdates;
        s.AutoDownloadUpdates = CheckForUpdates && DownloadUpdatesAutomatically;
        s.FirstRunCompleted = true;
        _settings.SaveSettings(s);
        Completed = true;
    }
}
