using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Runtime.InteropServices;
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

        // Shrink/expand the window to match the dashboard's compact monitor widget
        _expandedMinWidth = MinWidth;
        _expandedMinHeight = MinHeight;
        viewModel.DashboardVm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.IsShrunk))
                ApplyShrinkState(viewModel.DashboardVm);
        };

        // The title bar collapses to just the window buttons on pages without the battery pill,
        // so the drag region has to follow or it swallows clicks on the top of the page
        viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowBatteryPill))
            {
                ApplyCaptionHeight();
                PositionHeaderPlayer();
            }
        };
        ApplyCaptionHeight();

        // The title-bar music player follows the window controls and the available width
        WindowControls.SizeChanged += (s, e) => PositionHeaderPlayer();
        TitleBar.SizeChanged += (s, e) => PositionHeaderPlayer();
        BatteryPill.SizeChanged += (s, e) => PositionHeaderPlayer();
        HeaderPlayerHost.SizeChanged += (s, e) => PositionHeaderPlayer();
    }

    /// <summary>
    /// In the tall Dashboard header the player sits beside the window controls, as in the
    /// design; when the header is short or there is no room beside the battery pill it drops
    /// down below the controls instead.
    /// </summary>
    private void PositionHeaderPlayer()
    {
        if (DataContext is not MainViewModel vm) return;

        const double gap = 10;
        double controls = WindowControls.ActualWidth;
        double card = HeaderPlayerHost.ActualWidth > 0 ? HeaderPlayerHost.ActualWidth : HeaderPlayerHost.Width;
        double pillRight = BatteryPill.IsVisible ? BatteryPill.Margin.Left + BatteryPill.ActualWidth : 0;
        double room = TitleBar.ActualWidth - controls - pillRight - 2 * gap;

        HeaderPlayerHost.Margin = vm.ShowBatteryPill && room >= card
            ? new Thickness(0, 4, controls + gap, 0)
            : new Thickness(0, CompactCaptionHeight, 12, 0);
    }

    // Logical size of the monitor card; the window ends up at this times the card's render scale.
    private const double CompactLogicalWidth = 344;
    private const double CompactLogicalHeight = 704;
    // Must match the title bar heights in MainWindow.xaml
    private const double PillCaptionHeight = 112;
    private const double CompactCaptionHeight = 44;

    /// <summary>Keeps the drag region the same height as the title bar, which is only tall while the battery pill shows.</summary>
    private void ApplyCaptionHeight()
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome == null || DataContext is not MainViewModel vm || vm.DashboardVm.IsShrunk) return;
        chrome.CaptionHeight = vm.ShowBatteryPill ? PillCaptionHeight : CompactCaptionHeight;
    }

    private Rect _expandedBounds;
    private WindowState _expandedState = WindowState.Normal;
    private double _expandedMinWidth;
    private double _expandedMinHeight;
    private bool _hasExpandedBounds;

    /// <summary>
    /// Collapses the window down to just the hardware monitor card, or restores the size and
    /// position it had before shrinking. The card scales itself, so the window follows that scale.
    /// </summary>
    private void ApplyShrinkState(DashboardViewModel dashboardVm)
    {
        var chrome = WindowChrome.GetWindowChrome(this);

        if (dashboardVm.IsShrunk)
        {
            _expandedState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _expandedBounds = new Rect(Left, Top, Width, Height);
            _hasExpandedBounds = true;

            // Relax the full-app minimums first, otherwise the widget size gets clamped away
            MinWidth = 120;
            MinHeight = 160;

            var scale = dashboardVm.MonitorCardScale;
            Width = CompactLogicalWidth * scale;
            Height = CompactLogicalHeight * scale;
            // Zero caption: the widget is dragged by its own strip instead, which keeps
            // Windows from offering Snap / split view mid-drag
            if (chrome != null) chrome.CaptionHeight = 0;

            SetWindowCornerPreference(DWMWCP_ROUND);

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 24;
            Top = workArea.Top + 24;

            // Trim the leftover space under the card once the shrunk layout has been measured
            Dispatcher.BeginInvoke(new Action(FitHeightToWidget), DispatcherPriority.Loaded);
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            SetWindowCornerPreference(DWMWCP_DEFAULT);
            MinWidth = _expandedMinWidth;
            MinHeight = _expandedMinHeight;
            ApplyCaptionHeight();

            if (_hasExpandedBounds)
            {
                Width = _expandedBounds.Width;
                Height = _expandedBounds.Height;
                Left = _expandedBounds.Left;
                Top = _expandedBounds.Top;
            }

            WindowState = _expandedState;
        }
    }

    /// <summary>Snaps the widget's height to the card so no empty gap is left below it.</summary>
    private void FitHeightToWidget()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = workArea.Height - 48;
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        SizeToContent = SizeToContent.Manual;
        MaxHeight = double.PositiveInfinity;

        Top = Math.Min(Top, workArea.Bottom - Height - 24);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Rounds the widget's own window corners so the card is not clipped by a square frame.
    /// This is a DWM hint rather than a window-style change, which would make Windows redraw
    /// the native caption buttons over the widget.
    /// </summary>
    private void SetWindowCornerPreference(int preference)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // Ignored on Windows 10, which has no corner preference to set
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _themeService.ApplyWindowEffects(this, _settingsService.GetSettings().Theme);
        UpdateTrayIcon();
        UpdateWindowIcon();

        _themeService.ThemeChanged += OnThemeChanged;
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch { }
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

    private void OnThemeChanged(AxePower.Core.Models.AppTheme actualTheme)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateTrayIcon();
            UpdateWindowIcon();
        });
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General || e.Category == Microsoft.Win32.UserPreferenceCategory.Color)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateTrayIcon();
                UpdateWindowIcon();
                if (_settingsService.GetSettings().Theme == AxePower.Core.Models.AppTheme.System)
                {
                    _themeService.ApplyTheme(AxePower.Core.Models.AppTheme.System);
                }
            });
        }
    }

    public void UpdateTrayIcon()
    {
        try
        {
            var settings = _settingsService.GetSettings();
            bool isLight = _themeService.IsTaskbarLight(settings.Theme);
            var uri = new Uri(isLight
                ? "pack://application:,,,/Resources/AxePower_black.ico"
                : "pack://application:,,,/Resources/AxePower_white.ico", UriKind.Absolute);

            AppTrayIcon.IconSource = new System.Windows.Media.Imaging.BitmapImage(uri);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateTrayIcon] {ex.Message}");
        }
    }

    public void UpdateWindowIcon()
    {
        try
        {
            var actual = _themeService.ResolveActualTheme(_settingsService.GetSettings().Theme);
            var uri = new Uri(actual == AxePower.Core.Models.AppTheme.Light
                ? "pack://application:,,,/Resources/AxePower_black.ico"
                : "pack://application:,,,/Resources/AxePower_white.ico", UriKind.Absolute);
            this.Icon = new System.Windows.Media.Imaging.BitmapImage(uri);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateWindowIcon] {ex.Message}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch { }
        base.OnClosed(e);
    }
}
