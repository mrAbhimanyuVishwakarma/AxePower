using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Discovers installed applications from the uninstall registry keys (HKLM 64-bit, HKLM
/// WOW6432Node, HKCU) and Store packages.
/// </summary>
public interface IApplicationDiscoveryService
{
    /// <summary>
    /// Every uninstall entry and Store package, deduplicated. Hidden entries (system components,
    /// updates) are included with their flags set; the list decides what to show.
    /// </summary>
    Task<IReadOnlyList<InstalledApplication>> DiscoverApplicationsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Registry entries only (fast; no PowerShell). Used as the "other installed apps" snapshot.</summary>
    Task<IReadOnlyList<InstalledApplication>> DiscoverRegistryApplicationsAsync(
        CancellationToken cancellationToken = default);
}
