using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Scans for application-specific leftovers (registry keys, files, folders)
/// after an application has been uninstalled.
/// </summary>
public interface ILeftoverScannerService
{
    /// <summary>
    /// Scans for leftovers associated with the given application at the specified scan depth.
    /// Every returned item passes the exclusion filter.
    /// </summary>
    Task<IReadOnlyList<LeftoverItem>> ScanAsync(
        InstalledApplication application,
        ScanMode scanMode,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default);
}
