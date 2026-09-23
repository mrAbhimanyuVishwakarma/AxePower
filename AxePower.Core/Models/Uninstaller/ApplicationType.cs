namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// The UI filter category of an installed application, derived from its
/// <see cref="UninstallerKind"/>. Saved history stores these as numbers, so values are never
/// removed or reordered.
/// </summary>
public enum ApplicationType
{
    /// <summary>Classic Win32 installer (NSIS, Inno Setup, InstallShield, Burn, Squirrel…).</summary>
    Win32,

    /// <summary>Windows Installer (MSI) package with a ProductCode GUID.</summary>
    MSI,

    /// <summary>AppX / MSIX Store package.</summary>
    AppX,

    /// <summary>No longer produced: MSIX packages are reported as <see cref="AppX"/>. Kept for saved history.</summary>
    MSIX,

    /// <summary>No longer produced: WinGet discovery was removed. Kept for saved history.</summary>
    WinGet,

    /// <summary>Broken entry: the uninstaller is missing or the MSI product isn't registered. Shown as "Broken".</summary>
    Orphaned,
}
