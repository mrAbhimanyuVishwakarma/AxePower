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

    public MainWindow(MainViewModel viewModel, ThemeService themeService, SettingsService settingsService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _themeService = themeService;
        _settingsService = settingsService;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _themeService.ApplyWindowEffects(this, _settingsService.GetSettings().Theme);
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        this.Show();
        this.WindowState = WindowState.Normal;
        this.Activate();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
