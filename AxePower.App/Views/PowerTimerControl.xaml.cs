using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AxePower.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AxePower.App.Views;

/// <summary>
/// The power timer in the window's bottom-right corner, on every page. Its options panel closes
/// on Esc or a click anywhere else in the window.
/// </summary>
public partial class PowerTimerControl : UserControl
{
    private Window? _window;

    public PowerTimerControl()
    {
        InitializeComponent();
        DataContext = (Application.Current as App)?.Services?.GetService<PowerTimerViewModel>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private PowerTimerViewModel? ViewModel => DataContext as PowerTimerViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window != null) _window.PreviewMouseDown += OnWindowMouseDown;
        if (ViewModel != null) ViewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.PreviewMouseDown -= OnWindowMouseDown;
        if (ViewModel != null) ViewModel.PropertyChanged -= OnViewModelChanged;
        _window = null;
    }

    /// <summary>Opening the panel puts the cursor in the hours box, ready to type.</summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PowerTimerViewModel.IsFlyoutOpen) && ViewModel?.IsFlyoutOpen == true)
            Dispatcher.BeginInvoke(() => { HoursBox.Focus(); HoursBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { IsFlyoutOpen: true } vm) return;
        if (e.OriginalSource is DependencyObject source && IsInside(source)) return;
        vm.IsFlyoutOpen = false;
    }

    private bool IsInside(DependencyObject element)
    {
        for (var node = element; node != null; node = GetParentNode(node))
            if (ReferenceEquals(node, this)) return true;
        return false;
    }

    // Text runs are not visuals, so they only have a logical parent
    private static DependencyObject? GetParentNode(DependencyObject node) =>
        node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { IsFlyoutOpen: true } vm) return;
        vm.IsFlyoutOpen = false;
        TimerButton.Focus();
        e.Handled = true;
    }

    private void NumberBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private void NumberBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !text.Trim().All(char.IsDigit))
            e.CancelCommand();
    }
}
