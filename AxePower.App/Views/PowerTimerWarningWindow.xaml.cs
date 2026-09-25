using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AxePower.App.Views;

/// <summary>
/// The two-minute warning for the power timer: shown above everything in the bottom-right of the
/// screen for a few seconds, whatever app is in front. Clicks pass straight through the card and
/// it never takes the focus — except its "Extend time in AxePower" line, which is a separate tiny
/// window (one window cannot be partly click-through) that opens AxePower's timer.
/// </summary>
public partial class PowerTimerWarningWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static PowerTimerWarningWindow? _current;

    private readonly Action _onExtend;
    private Window? _link;

    public PowerTimerWarningWindow(string message, Action onExtend)
    {
        InitializeComponent();
        MessageText.Text = message;
        _onExtend = onExtend;
        SourceInitialized += (_, _) => SetExtendedStyle(this, WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        Loaded += (_, _) =>
        {
            PlaceInCorner();
            // Positions settle after the move; lay the link over its slot once they have
            Dispatcher.BeginInvoke(ShowLink, DispatcherPriority.Loaded);
        };
        Closed += (_, _) => _link?.Close();
    }

    /// <summary>Shows the warning, replacing one already on screen, and closes it after <paramref name="duration"/>.</summary>
    public static void ShowFor(string message, TimeSpan duration, Action onExtend)
    {
        _current?.Close();
        var window = _current = new PowerTimerWarningWindow(message, onExtend);
        var close = new DispatcherTimer { Interval = duration };
        close.Tick += (_, _) =>
        {
            close.Stop();
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            close.Stop();
            if (ReferenceEquals(_current, window)) _current = null;
        };
        window.Show();
        close.Start();
    }

    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }

    private void ShowLink()
    {
        if (!IsLoaded || PresentationSource.FromVisual(LinkSlot) is not { CompositionTarget: { } target }) return;

        // Screen pixels back to WPF units, so the link lands on its slot at any display scaling
        var topLeft = target.TransformFromDevice.Transform(LinkSlot.PointToScreen(new Point(0, 0)));

        var accent = (Brush?)TryFindResource("AccentBrush") ?? Brushes.DodgerBlue;
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        var text = new TextBlock
        {
            Text = "Extend time in AxePower",
            FontSize = 12,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.Children.Add(text);

        // A near-invisible fill makes the whole line clickable, not just the letters
        var hit = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Cursor = Cursors.Hand,
            Child = label
        };
        hit.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
        hit.MouseLeave += (_, _) => text.TextDecorations = null;
        hit.MouseLeftButtonUp += (_, _) =>
        {
            Close();
            _onExtend();
        };

        _link = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = topLeft.X,
            Top = topLeft.Y,
            Width = LinkSlot.ActualWidth,
            Height = LinkSlot.ActualHeight,
            Content = hit,
            ToolTip = "Open the power timer to add time or cancel"
        };
        AutomationProperties.SetName(_link, "Extend time in AxePower");
        _link.SourceInitialized += (_, _) => SetExtendedStyle(_link, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _link.Show();
    }

    private static void SetExtendedStyle(Window window, int flags)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | flags);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
