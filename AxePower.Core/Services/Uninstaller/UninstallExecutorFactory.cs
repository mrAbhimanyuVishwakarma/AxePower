using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Picks the executor for an application's <see cref="UninstallerKind"/>: MSI products go to
/// msiexec, Store apps to Remove-AppxPackage, broken entries to "remove the listing", and every
/// exe uninstaller to the Win32 executor.
/// </summary>
public sealed class UninstallExecutorFactory
{
    private readonly IReadOnlyList<IUninstallExecutor> _executors;

    public UninstallExecutorFactory(
        Win32UninstallExecutor win32,
        MsiUninstallExecutor msi,
        AppxUninstallExecutor appx,
        BrokenEntryExecutor brokenEntry)
        : this([win32, msi, appx, brokenEntry])
    {
    }

    public UninstallExecutorFactory(IReadOnlyList<IUninstallExecutor> executors)
    {
        _executors = executors;
    }

    public IUninstallExecutor GetExecutor(InstalledApplication application) =>
        _executors.FirstOrDefault(e => e.SupportedKinds.Contains(application.UninstallerKind))
        ?? throw new InvalidOperationException($"No executor handles {application.UninstallerKind}.");
}
