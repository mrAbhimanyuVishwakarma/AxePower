using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.Reflection;

namespace AxePower.App.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    [ObservableProperty]
    private string _version = "1.0.0";

    [ObservableProperty]
    private string _runtime = ".NET 8.0";

    public AboutViewModel()
    {
        var assembly = Assembly.GetExecutingAssembly();
        Version = assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    [RelayCommand]
    private void OpenHelp()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/mrAbhimanyuVishwakarma/AxePower/blob/main/README.md",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void CheckForUpdates()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases",
            UseShellExecute = true
        });
    }
}
