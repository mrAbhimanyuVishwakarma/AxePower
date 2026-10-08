using System.Windows;
using AxePower.App.ViewModels;

namespace AxePower.App.Windows;

/// <summary>The first-run welcome (Prompt 31). Continue saves the choices; Not now or closing changes nothing.</summary>
public partial class WelcomeWindow : Window
{
    private readonly WelcomeViewModel _viewModel;

    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ContinueButton.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Continue();
        DialogResult = true;
    }

    private void NotNow_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
