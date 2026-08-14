using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Discovers all installed applications on the machine from multiple sources:
/// registry (HKLM/HKCU/WOW6432), AppX/MSIX packages, and WinGet.
/// </summary>
public interface IApplicationDiscoveryService
{
    /// <summary>
    /// Discovers all installed applications, deduplicates across sources, and returns them.
    /// </summary>
    Task<IReadOnlyList<InstalledApplication>> DiscoverApplicationsAsync(
        CancellationToken cancellationToken = default);
}
