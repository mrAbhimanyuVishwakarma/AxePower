using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// The values of one uninstall registry key that classification needs, as a plain record, so the
/// rules can be tested with fixtures instead of the live registry.
/// </summary>
public sealed record UninstallEntryValues
{
    public string DisplayName { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string DisplayVersion { get; init; } = string.Empty;
    public string InstallDate { get; init; } = string.Empty;
    public string InstallLocation { get; init; } = string.Empty;
    public string DisplayIcon { get; init; } = string.Empty;
    public string UninstallString { get; init; } = string.Empty;
    public string QuietUninstallString { get; init; } = string.Empty;
    public string ModifyPath { get; init; } = string.Empty;
    public string ParentKeyName { get; init; } = string.Empty;
    public string ReleaseType { get; init; } = string.Empty;
    public string BundleCachePath { get; init; } = string.Empty;
    public string BundleProviderKey { get; init; } = string.Empty;

    /// <summary>"Inno Setup: App Path", written by Inno Setup installers.</summary>
    public string InnoAppPath { get; init; } = string.Empty;

    public int? WindowsInstaller { get; init; }
    public int? SystemComponent { get; init; }
    public int? NoRemove { get; init; }

    /// <summary>EstimatedSize in KB.</summary>
    public long EstimatedSize { get; init; }

    public static UninstallEntryValues FromRegistryKey(RegistryKey key) => new()
    {
        DisplayName = RegistryPath.GetString(key, "DisplayName").Trim(),
        Publisher = RegistryPath.GetString(key, "Publisher").Trim(),
        DisplayVersion = RegistryPath.GetString(key, "DisplayVersion").Trim(),
        InstallDate = RegistryPath.GetString(key, "InstallDate").Trim(),
        InstallLocation = RegistryPath.GetString(key, "InstallLocation"),
        DisplayIcon = RegistryPath.GetString(key, "DisplayIcon"),
        UninstallString = RegistryPath.GetString(key, "UninstallString"),
        QuietUninstallString = RegistryPath.GetString(key, "QuietUninstallString"),
        ModifyPath = RegistryPath.GetString(key, "ModifyPath"),
        ParentKeyName = RegistryPath.GetString(key, "ParentKeyName"),
        ReleaseType = RegistryPath.GetString(key, "ReleaseType"),
        BundleCachePath = RegistryPath.GetString(key, "BundleCachePath"),
        BundleProviderKey = RegistryPath.GetString(key, "BundleProviderKey"),
        InnoAppPath = RegistryPath.GetString(key, "Inno Setup: App Path"),
        WindowsInstaller = RegistryPath.GetInt(key, "WindowsInstaller"),
        SystemComponent = RegistryPath.GetInt(key, "SystemComponent"),
        NoRemove = RegistryPath.GetInt(key, "NoRemove"),
        EstimatedSize = Math.Max(0, RegistryPath.GetInt(key, "EstimatedSize") ?? 0),
    };
}
