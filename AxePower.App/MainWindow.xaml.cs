using System.Windows;
using AxePower.App.ViewModels;

namespace AxePower.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
