using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ScrollToRequestedSection();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = e.NewValue as SettingsViewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ScrollToRequestedSection();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.RequestedSection)) ScrollToRequestedSection();
    }

    /// <summary>
    /// Brings the card a deep link asked for into view — the Share Screen stage's gear sends the
    /// user here, and landing at the top of a long page would leave them hunting for it.
    /// </summary>
    private void ScrollToRequestedSection()
    {
        var section = _viewModel?.RequestedSection;
        if (string.IsNullOrEmpty(section)) return;

        // Scrolling only works once the page has been measured, so wait for layout to settle.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new System.Action(() =>
        {
            if (section == "ShareScreen") ShareScreenCard?.BringIntoView();

            if (_viewModel != null) _viewModel.RequestedSection = null;
        }));
    }
}
