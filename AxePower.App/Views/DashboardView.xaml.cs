using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _viewModel;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as DashboardViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyShrinkLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsShrunk)) ApplyShrinkLayout();
    }

    /// <summary>
    /// Hands the whole view over to the monitor card while shrunk. Star/fixed column widths
    /// can't be data-bound, so the swap happens here instead of in a trigger.
    /// </summary>
    private void ApplyShrinkLayout()
    {
        var shrunk = _viewModel?.IsShrunk == true;
        MainColumn.Width = shrunk ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        RailColumn.Width = shrunk ? new GridLength(1, GridUnitType.Star) : new GridLength(320);
    }

    /// <summary>
    /// "What's New" entries navigate on click rather than through a separate jump button.
    /// The event isn't handled, so the ComboBox still commits the selection as usual; the
    /// navigation is deferred because it swaps out the view hosting this very dropdown.
    /// </summary>
    private void FeatureItem_Clicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem { DataContext: AppFeatureItem feature }) return;
        if (Window.GetWindow(this)?.DataContext is not MainViewModel main) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => main.Navigate(feature.TargetNavigation)));
    }

    // --- Widget dragging -------------------------------------------------
    // The widget has no system caption (see MainWindow.ApplyShrinkState), so it is moved from
    // here. Windows only offers Snap / split view during its own move loop, which this avoids.

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private Window? _dragWindow;
    private Point _dragCursorOrigin;
    private Point _dragWindowOrigin;
    private bool _isDragging;

    private void WidgetDragStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel?.IsShrunk != true) return;

        _dragWindow = Window.GetWindow(this);
        if (_dragWindow == null) return;

        _dragCursorOrigin = GetCursorPositionInDips(_dragWindow);
        _dragWindowOrigin = new Point(_dragWindow.Left, _dragWindow.Top);
        _isDragging = WidgetDragStrip.CaptureMouse();
    }

    private void WidgetDragStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dragWindow == null) return;

        var cursor = GetCursorPositionInDips(_dragWindow);
        _dragWindow.Left = _dragWindowOrigin.X + (cursor.X - _dragCursorOrigin.X);
        _dragWindow.Top = _dragWindowOrigin.Y + (cursor.Y - _dragCursorOrigin.Y);
    }

    private void WidgetDragStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        WidgetDragStrip.ReleaseMouseCapture();
    }

    /// <summary>Cursor position in the window's own units, so dragging tracks 1:1 at any DPI.</summary>
    private static Point GetCursorPositionInDips(Window window)
    {
        GetCursorPos(out var point);
        var device = new Point(point.X, point.Y);

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        return transform.HasValue ? transform.Value.Transform(device) : device;
    }
}
