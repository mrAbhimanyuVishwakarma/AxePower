using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Executes the uninstall of a single application using the appropriate native method.
/// </summary>
public interface IUninstallExecutor
{
    /// <summary>The application type this executor handles.</summary>
    ApplicationType SupportedType { get; }

    /// <summary>
    /// Executes the uninstall for the given application.
    /// Implementations must respect DryRunMode in settings.
    /// </summary>
    Task<UninstallResult> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        CancellationToken cancellationToken = default);
}
