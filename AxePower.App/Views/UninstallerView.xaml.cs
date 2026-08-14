using System.Windows.Controls;
using AxePower.App.ViewModels;

namespace AxePower.App.Views;

/// <summary>Code-behind for UninstallerView.xaml</summary>
public partial class UninstallerView : UserControl
{
    public UninstallerView()
    {
        InitializeComponent();
        Loaded += UninstallerView_Loaded;
    }

    private void UninstallerView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UninstallerViewModel vm && vm.AppCount == 0 && !vm.IsLoading)
        {
            _ = vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}

