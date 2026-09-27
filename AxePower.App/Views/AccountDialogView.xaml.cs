using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

public partial class AccountDialogView : UserControl
{
    private AccountViewModel? _vm;

    public AccountDialogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.DialogClosed -= OnDialogClosed;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as AccountViewModel;
        if (_vm is not null)
        {
            _vm.DialogClosed += OnDialogClosed;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    // A PasswordBox cannot be data-bound (by design, so the password never sits in a bindable
    // string), so its value is handed to the view model as it changes.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.Password = PasswordInput.Password;
    }

    private void OnDialogClosed(object? sender, EventArgs e) => PasswordInput.Clear();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Put the cursor in the first field whenever a form appears.
        if (e.PropertyName is nameof(AccountViewModel.IsDialogOpen) or nameof(AccountViewModel.Mode) && _vm is { IsDialogOpen: true })
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (EmailBox.IsVisible) EmailBox.Focus();
                else MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
            }));
        }
    }
}
