namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// What an application's uninstaller really is, decided by <c>UninstallerKindDetector</c> from the
/// parsed uninstall command and the uninstall key's values. It picks the executor, the command
/// that runs, and how that command's exit code is read.
/// </summary>
public enum UninstallerKind
{
    /// <summary>An exe we can't identify. Its registry command runs as-is, never with added switches.</summary>
    Unknown,

    /// <summary>Windows Installer product: <c>msiexec /x {ProductCode}</c>.</summary>
    Msi,

    /// <summary>WiX Burn bundle cached in <c>Package Cache</c>: <c>&lt;exe&gt; /uninstall</c>.</summary>
    BurnBundle,

    /// <summary>Inno Setup: <c>unins###.exe</c> with its <c>.dat</c> next to it.</summary>
    InnoSetup,

    /// <summary>Nullsoft installer: the exe contains the text "Nullsoft".</summary>
    Nsis,

    /// <summary>InstallShield: runs from <c>InstallShield Installation Information\{…}</c>.</summary>
    InstallShield,

    /// <summary>Squirrel updater (Discord, Slack…): <c>Update.exe --uninstall</c>.</summary>
    Squirrel,

    /// <summary>A PowerShell script uninstaller.</summary>
    PowerShell,

    /// <summary>AppX / MSIX package, removed with <c>Remove-AppxPackage</c>.</summary>
    StoreApp,

    /// <summary>The uninstaller is missing, or the MSI product is no longer registered.</summary>
    BrokenEntry,
}
