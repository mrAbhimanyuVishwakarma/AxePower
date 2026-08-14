using CommunityToolkit.Mvvm.ComponentModel;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.App.ViewModels;

/// <summary>Wraps an InstalledApplication with observable selection state.</summary>
public sealed partial class InstalledApplicationViewModel : ObservableObject
{
    public InstalledApplication Application { get; }

    [ObservableProperty]
    private bool _isSelected;

    public InstalledApplicationViewModel(InstalledApplication application)
    {
        Application = application;
    }

    // Forwarded properties for DataGrid binding
    public string DisplayName    => Application.DisplayName;
    public string Publisher      => Application.Publisher;
    public string Version        => Application.Version;
    public string DisplaySize    => Application.DisplaySize;
    public string DisplayInstallDate => Application.DisplayInstallDate;
    public string ApplicationTypeLabel => Application.ApplicationType.ToString();
    public string SourceLabel
    {
        get
        {
            var parts = new List<string>();
            if (Application.DiscoverySources.HasFlag(DiscoverySource.RegistryHKLM) ||
                Application.DiscoverySources.HasFlag(DiscoverySource.RegistryHKCU) ||
                Application.DiscoverySources.HasFlag(DiscoverySource.RegistryWOW6432))
                parts.Add("Registry");
            if (Application.DiscoverySources.HasFlag(DiscoverySource.AppX))
                parts.Add("Store");
            if (Application.DiscoverySources.HasFlag(DiscoverySource.WinGet))
                parts.Add("WinGet");
            return string.Join(", ", parts);
        }
    }

    public bool IsProtected    => Application.IsProtected;
    public bool IsOrphaned     => Application.ApplicationType == ApplicationType.Orphaned;
    public bool CanUninstall   => Application.CanUninstall && !Application.IsProtected;
    public string StatusLabel
    {
        get
        {
            if (Application.IsProtected) return "Protected";
            if (Application.ApplicationType == ApplicationType.Orphaned) return "Orphaned";
            if (Application.IsRunning) return "Running";
            return string.Empty;
        }
    }
}
