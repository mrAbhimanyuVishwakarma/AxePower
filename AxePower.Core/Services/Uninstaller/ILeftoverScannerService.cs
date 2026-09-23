using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>What the scanner needs to know beyond the app itself.</summary>
public sealed class LeftoverScanOptions
{
    /// <summary>
    /// A fresh snapshot of installed apps. Their folders are never offered, and names or
    /// publishers they still use lower an item's confidence. Entries of the scanned app itself
    /// are ignored unless <see cref="ApplicationStillInstalled"/>.
    /// </summary>
    public IReadOnlyList<InstalledApplication> InstalledApplications { get; init; } = [];

    /// <summary>False when nothing may be pre-ticked (broken entries, apps that are still installed).</summary>
    public bool AllowPreselection { get; init; } = true;

    /// <summary>The app is still installed (a scan from the app list): its own install folder is blocked too.</summary>
    public bool ApplicationStillInstalled { get; init; }

    /// <summary>AxePower runs elevated, so admin-only items may be pre-ticked.</summary>
    public bool IsElevated { get; init; }
}

/// <summary>
/// Scans for application-specific leftovers (registry keys, files, folders)
/// after an application has been uninstalled.
/// </summary>
public interface ILeftoverScannerService
{
    /// <summary>
    /// Scans for leftovers associated with the given application at the specified scan depth.
    /// Every returned item carries evidence and passes the exclusion filter.
    /// </summary>
    Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledApplication application,
        ScanMode scanMode,
        UninstallerSettings settings,
        LeftoverScanOptions? options = null,
        CancellationToken cancellationToken = default);
}
