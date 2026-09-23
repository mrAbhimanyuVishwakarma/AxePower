using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Manages sequential batch uninstallation of multiple applications.</summary>
public interface IBatchUninstallService
{
    /// <summary>
    /// Uninstalls the applications one at a time. Each result is verified; failures are recorded
    /// and don't stop the queue. Stopping (through <paramref name="control"/> or the token)
    /// ends the queue after the current app; the session is saved and returned either way.
    /// </summary>
    Task<UninstallSession> ExecuteBatchAsync(
        IReadOnlyList<InstalledApplication> applications,
        UninstallerSettings settings,
        IProgress<UninstallProgress>? progress = null,
        IUninstallInteraction? interaction = null,
        UninstallBatchControl? control = null,
        CancellationToken cancellationToken = default);
}
