using System;
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
        Loaded += (_, _) =>
        {
            ScrollToRequestedSection();
            StartTouchpadSync();
            _viewModel?.LinkUpdateStateToAbout();
        };
        Unloaded += (_, _) => _touchpadSync?.Stop();
    }

    private DispatcherTimer? _touchpadSync;

    /// <summary>
    /// Keeps the Touchpad switch matching Windows while this page is open, so turning it off in
    /// Windows Settings or with a laptop Fn key shows up here within a couple of seconds.
    /// </summary>
    private void StartTouchpadSync()
    {
        _viewModel?.RefreshTouchpadState();
        _touchpadSync ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _touchpadSync.Tick -= OnTouchpadSyncTick;
        _touchpadSync.Tick += OnTouchpadSyncTick;
        _touchpadSync.Start();
    }

    private void OnTouchpadSyncTick(object? sender, EventArgs e) => _viewModel?.RefreshTouchpadState();

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
        // Each category opens at its top
        else if (e.PropertyName == nameof(SettingsViewModel.SelectedCategory)) CategoryScroller.ScrollToTop();
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
