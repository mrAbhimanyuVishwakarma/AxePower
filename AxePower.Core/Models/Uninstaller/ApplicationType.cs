namespace AxePower.Core.Models.Uninstaller;

/// <summary>The packaging/deployment type of an installed application.</summary>
public enum ApplicationType
{
    /// <summary>Classic Win32 installer (NSIS, Inno Setup, InstallShield, etc.).</summary>
    Win32,

    /// <summary>Windows Installer (MSI) package with a ProductCode GUID.</summary>
    MSI,

    /// <summary>AppX package (Windows 8/10 era Store package).</summary>
    AppX,

    /// <summary>MSIX package (Windows 10/11 Store package).</summary>
    MSIX,

    /// <summary>Package discovered via Windows Package Manager (winget).</summary>
    WinGet,

    /// <summary>Registry entry exists but the uninstaller is missing or broken.</summary>
    Orphaned,
}
