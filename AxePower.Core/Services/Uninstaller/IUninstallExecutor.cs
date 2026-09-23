using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Runs the uninstall of a single application for the uninstaller kinds it supports and reports
/// what it observed. It never decides success: the batch verifies.
/// </summary>
public interface IUninstallExecutor
{
    /// <summary>The uninstaller kinds this executor runs.</summary>
    IReadOnlyCollection<UninstallerKind> SupportedKinds { get; }

    /// <summary>
    /// Executes the uninstall for the given application.
    /// Implementations must respect DryRunMode in settings. Cancelling
    /// <paramref name="cancellationToken"/> stops waiting; it never kills the uninstaller.
    /// </summary>
    Task<UninstallExecution> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        UninstallRunContext context,
        CancellationToken cancellationToken = default);
}
