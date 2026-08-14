using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Selects the appropriate IUninstallExecutor for an application type.
/// For Orphaned applications, returns null — the batch service handles those
/// by going directly to the leftover scanner (Forced Uninstall flow).
/// </summary>
public sealed class UninstallExecutorFactory
{
    private readonly IReadOnlyList<IUninstallExecutor> _executors;

    public UninstallExecutorFactory(
        Win32UninstallExecutor win32,
        MsiUninstallExecutor msi,
        WingetUninstallExecutor winget,
        AppxUninstallExecutor appx)
    {
        _executors = [win32, msi, winget, appx];
    }

    /// <summary>Returns the executor for the given application type, or null for Orphaned.</summary>
    public IUninstallExecutor? GetExecutor(InstalledApplication application)
    {
        var type = application.ApplicationType;

        // Orphaned apps — skip native uninstaller, go directly to leftover scanner
        if (type == ApplicationType.Orphaned) return null;

        // MSI detected even if marked Win32 (ProductCode present)
        if (type == ApplicationType.Win32 &&
            MsiUninstallExecutor.IsValidProductCode(application.ProductCode))
            type = ApplicationType.MSI;

        return _executors.FirstOrDefault(e => e.SupportedType == type)
               ?? _executors.First(e => e.SupportedType == ApplicationType.Win32);
    }
}
