using System.Windows;
using System.Windows.Interop;
using System;
using AxePower.App.ViewModels;
using AxePower.App.Services;
using AxePower.Core.Services;

namespace AxePower.App;

public partial class MainWindow : Window
{
    private readonly ThemeService _themeService;
    private readonly SettingsService _settingsService;
    private readonly SystemTelemetryService? _telemetryService;

    public MainWindow(MainViewModel viewModel, ThemeService themeService, SettingsService settingsService, SystemTelemetryService? telemetryService = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _themeService = themeService;
        _settingsService = settingsService;
        _telemetryService = telemetryService;

        // No per-second WMI/disk/network sampling while hidden in the tray
        IsVisibleChanged += (s, e) =>
        {
            if (_telemetryService == null) return;
            if (IsVisible) _telemetryService.Resume();
            else _telemetryService.Pause();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _themeService.ApplyWindowEffects(this, _settingsService.GetSettings().Theme);
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        this.Show();
        if (this.WindowState == WindowState.Minimized)
        {
            this.WindowState = WindowState.Normal;
        }
        this.Activate();
        this.Focus();
    }

    private void OpenAppAndNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string target)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.NavigateCommand.Execute(target);
            }
        }
        OpenApp_Click(sender, e);
    }

    private void TraySecondMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.NavigateCommand.Execute("PhoneControl");
            _ = vm.PhoneHubVm.SpaceDeskActionAsync();
        }
        OpenApp_Click(sender, e);
    }

    private void PatchNotesScrollUp_Click(object sender, RoutedEventArgs e)
    {
        PatchNotesScrollViewer?.LineUp();
        PatchNotesScrollViewer?.LineUp();
        PatchNotesScrollViewer?.LineUp();
        PatchNotesScrollViewer?.LineUp();
    }

    private void PatchNotesScrollDown_Click(object sender, RoutedEventArgs e)
    {
        PatchNotesScrollViewer?.LineDown();
        PatchNotesScrollViewer?.LineDown();
        PatchNotesScrollViewer?.LineDown();
        PatchNotesScrollViewer?.LineDown();
    }

    private bool _isExplicitShutdown;

    public void ForceShutdown()
    {
        _isExplicitShutdown = true;
        Application.Current.Shutdown();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        _isExplicitShutdown = true;
        Application.Current.Shutdown();
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }

    private void AppTrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        OpenApp_Click(sender, e);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized)
            this.WindowState = WindowState.Normal;
        else
            this.WindowState = WindowState.Maximized;
    }

    /// <summary>Lets the OS close the window without being vetoed (logoff/shutdown).</summary>
    public void AllowClose()
    {
        _isExplicitShutdown = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExplicitShutdown)
        {
            base.OnClosing(e);
            return;
        }

        var behaviour = _settingsService.GetSettings().CloseBehaviour;

        if (behaviour == Core.Models.CloseBehaviour.AskEveryTime)
        {
            var result = MessageBox.Show(
                "Minimize AxePower to the system tray?\n\nYes — keep running in the tray\nNo — exit AxePower",
                "Close AxePower",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
                behaviour = Core.Models.CloseBehaviour.ExitAxePower;
            else if (result == MessageBoxResult.Yes)
                behaviour = Core.Models.CloseBehaviour.MinimiseToTray;
            else
            {
                e.Cancel = true;
                return;
            }
        }

        if (behaviour == Core.Models.CloseBehaviour.ExitAxePower)
        {
            _isExplicitShutdown = true;
            base.OnClosing(e);
            return;
        }

        // Minimize to tray instead of closing process
        e.Cancel = true;
        this.Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
