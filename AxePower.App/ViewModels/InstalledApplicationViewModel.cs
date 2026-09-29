using CommunityToolkit.Mvvm.ComponentModel;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.App.ViewModels;

/// <summary>Wraps an InstalledApplication with observable selection state.</summary>
public sealed partial class InstalledApplicationViewModel : ObservableObject
{
    public InstalledApplication Application { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What will run, e.g. "NSIS, interactive" (set by the page when settings change).</summary>
    [ObservableProperty]
    private string _planDescription = string.Empty;

    public InstalledApplicationViewModel(InstalledApplication application)
    {
        Application = application;
        _planDescription = UninstallCommandBuilder.KindName(application.UninstallerKind);
    }

    // Forwarded properties for DataGrid binding
    public string DisplayName    => Application.DisplayName;
    public string Publisher      => Application.Publisher;
    public string Version        => Application.Version;
    public string DisplaySize    => Application.DisplaySize;
    public string DisplayInstallDate => Application.DisplayInstallDate;
    public long EstimatedSizeKb => Application.EstimatedSizeKb;
    public string InstallDateSortKey => Application.InstallDate ?? string.Empty;
    public string ArchitectureSortKey => Application.Architecture ?? string.Empty;

    /// <summary>Friendly architecture text for the table's Type column.</summary>
    public string ArchitectureDisplayLabel => Application.Architecture.Trim().ToLowerInvariant() switch
    {
        "x64" => "64-bit",
        "x86" => "32-bit",
        "arm64" => "ARM64",
        "neutral" => "Neutral",
        _ => string.Empty,
    };

    /// <summary>Filter-chip category: Win32, MSI, AppX or Broken.</summary>
    public string ApplicationTypeLabel => Application.UninstallerKind == UninstallerKind.BrokenEntry
        ? "Broken"
        : Application.ApplicationType switch
        {
            ApplicationType.AppX or ApplicationType.MSIX => "AppX",
            ApplicationType.MSI => "MSI",
            _ => "Win32",
        };

    /// <summary>User-facing type shown beside the app name; AppX/MSIX is labelled Store.</summary>
    public string ApplicationTypeDisplayLabel => ApplicationTypeLabel == "AppX"
        ? "Store"
        : ApplicationTypeLabel;

    /// <summary>The uninstaller technology, e.g. "Inno Setup" or "Windows Installer (msiexec)".</summary>
    public string KindLabel => UninstallCommandBuilder.KindName(Application.UninstallerKind);

    public string SourceLabel
    {
        get
        {
            var parts = new List<string>();
            if (Application.DiscoverySources.HasFlag(DiscoverySource.RegistryHKLM)) parts.Add("Registry (machine)");
            if (Application.DiscoverySources.HasFlag(DiscoverySource.RegistryWOW6432)) parts.Add("Registry (32-bit)");
            if (Application.DiscoverySources.HasFlag(DiscoverySource.RegistryHKCU)) parts.Add("Registry (user)");
            if (Application.DiscoverySources.HasFlag(DiscoverySource.AppX)) parts.Add("Store");
            return string.Join(", ", parts);
        }
    }

    public string InstallLocationLabel => string.IsNullOrEmpty(Application.InstallLocation)
        ? "Unknown"
        : Application.IsInstallLocationDerived
            ? $"{Application.InstallLocation} (from the uninstaller's location)"
            : Application.InstallLocation;

    public bool IsProtected    => Application.IsProtected;
    public bool IsBroken       => Application.UninstallerKind == UninstallerKind.BrokenEntry;

    /// <summary>Kept for the row style trigger: broken entries were called "orphaned".</summary>
    public bool IsOrphaned     => IsBroken;

    public bool IsHidden       => Application.IsSystemComponent || Application.IsUpdate;
    public bool CanUninstall   => Application.CanUninstall && !Application.IsProtected;

    /// <summary>The details panel's action: broken entries can only have their listing removed.</summary>
    public string ActionLabel  => IsBroken ? "Remove broken entry" : "Uninstall";

    public string StatusLabel
    {
        get
        {
            if (Application.IsProtected) return "Protected";
            if (IsBroken) return "Broken";
            if (Application.IsRunning) return "Running";
            if (Application.IsUpdate) return "Update";
            if (Application.IsSystemComponent) return "Hidden";
            return string.Empty;
        }
    }
}
