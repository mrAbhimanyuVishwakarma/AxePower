using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Manages sequential batch uninstallation of multiple applications.</summary>
public interface IBatchUninstallService
{
    /// <summary>
    /// Executes uninstall for all applications in sequence.
    /// Progress is reported via the <paramref name="progress"/> callback.
    /// Failures are recorded but do not abort the remaining queue.
    /// </summary>
    Task<UninstallSession> ExecuteBatchAsync(
        IReadOnlyList<InstalledApplication> applications,
        UninstallerSettings settings,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
