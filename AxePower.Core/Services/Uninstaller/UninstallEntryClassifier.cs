using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Turns one uninstall registry key into an <see cref="InstalledApplication"/>. It is pure apart
/// from the injected file-system and MSI probes, so every discovery rule is testable with fixtures.
/// Visibility rules (SystemComponent, updates) only set flags; the list decides what to show.
/// </summary>
public static class UninstallEntryClassifier
{
    private static readonly string[] MicrosoftPublisherPrefixes = ["Microsoft", "Windows"];

    /// <summary>Folder names installers use for a sub-folder that only holds the uninstaller.</summary>
    private static readonly HashSet<string> UninstallerSubfolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninst", "uninstall", "uninstaller", "_uninst", "uninstall files",
    };

    public static InstalledApplication? Classify(
        UninstallEntryValues values,
        string keyName,
        string registryKeyPath,
        IFileSystemProbe fs,
        IMsiProductStateReader msi)
    {
        // Rule 1: no DisplayName, not an app
        if (string.IsNullOrWhiteSpace(values.DisplayName)) return null;

        UninstallCommandParser.TryParse(values.UninstallString, fs, out var command);
        var detection = UninstallerKindDetector.Detect(command, values, keyName, fs, msi);

        var installLocation = NormalizeInstallLocation(values.InstallLocation, fs);
        var derived = false;
        if (installLocation.Length == 0)
        {
            installLocation = DeriveInstallLocation(command, values);
            derived = installLocation.Length > 0;
        }

        var isHkcu = registryKeyPath.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase);
        var isWow = registryKeyPath.Contains(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase);

        return new InstalledApplication
        {
            Id = registryKeyPath,
            DisplayName = values.DisplayName.Trim(),
            Publisher = values.Publisher.Trim(),
            Version = values.DisplayVersion.Trim(),
            InstallDate = values.InstallDate,
            InstallLocation = installLocation,
            IsInstallLocationDerived = derived,
            EstimatedSizeKb = values.EstimatedSize,
            RegistryKeyPath = registryKeyPath,
            RegistryKeyName = keyName,
            ParentKeyName = values.ParentKeyName.Trim(),
            UninstallString = values.UninstallString,
            QuietUninstallString = values.QuietUninstallString,
            ModifyPath = values.ModifyPath,
            UninstallerKind = detection.Kind,
            UninstallerPath = command?.FileName ?? string.Empty,
            UninstallerArguments = command?.Arguments ?? string.Empty,
            // Rule 5: the ProductCode is only kept for MSI entries
            ProductCode = detection.Kind == UninstallerKind.Msi ? detection.ProductCode : string.Empty,
            MsiProductState = detection.MsiState,
            ApplicationType = InstalledApplication.ApplicationTypeFor(detection.Kind),
            DiscoverySources = isHkcu ? DiscoverySource.RegistryHKCU
                             : isWow ? DiscoverySource.RegistryWOW6432
                             : DiscoverySource.RegistryHKLM,
            // Rule 2: hidden unless "Show system components"
            IsSystemComponent = values.SystemComponent == 1,
            // Rule 3: updates, hotfixes and child components
            IsUpdate = IsUpdateEntry(values),
            // Rule 4
            CanUninstall = values.NoRemove != 1,
            IsMicrosoftComponent = MicrosoftPublisherPrefixes.Any(p =>
                values.Publisher.StartsWith(p, StringComparison.OrdinalIgnoreCase)),
            Architecture = isHkcu ? string.Empty : isWow ? "x86" : "x64",
            Scope = isHkcu ? "User" : "Machine",
            IconPath = ParseIconPath(values.DisplayIcon),
        };
    }

    public static bool IsUpdateEntry(UninstallEntryValues values) =>
        !string.IsNullOrWhiteSpace(values.ParentKeyName) ||
        values.ReleaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
        values.ReleaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A usable folder from the InstallLocation value: quotes and trailing slashes trimmed, and a
    /// value that names a file (Google Drive stores its exe) reduced to that file's folder.
    /// </summary>
    public static string NormalizeInstallLocation(string raw, IFileSystemProbe fs)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"').Trim());
        if (!Path.IsPathFullyQualified(path)) return string.Empty;

        try { path = Path.GetFullPath(path); }
        catch { return string.Empty; }

        if (!fs.DirectoryExists(path) &&
            (fs.FileExists(path) || Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".ico"))
            path = Path.GetDirectoryName(path) ?? string.Empty;

        return TrimFolder(path);
    }

    /// <summary>
    /// BCU's InstallLocationGenerator idea: when the key has no InstallLocation, use Inno Setup's
    /// app path, else the uninstaller's folder, else the icon's folder — never a system folder,
    /// a Package Cache, a shared root or the InstallShield cache.
    /// </summary>
    public static string DeriveInstallLocation(UninstallCommand? command, UninstallEntryValues values)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(values.InnoAppPath))
            candidates.Add(values.InnoAppPath.Trim().Trim('"'));
        if (command is not null && Path.IsPathFullyQualified(command.FileName))
            candidates.Add(Path.GetDirectoryName(command.FileName) ?? string.Empty);
        var icon = ParseIconPath(values.DisplayIcon);
        if (icon.Length > 0)
            candidates.Add(Path.GetDirectoryName(icon) ?? string.Empty);

        foreach (var candidate in candidates)
        {
            var folder = TrimFolder(candidate);
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;

            if (UninstallerSubfolderNames.Contains(Path.GetFileName(folder)))
                folder = Path.GetDirectoryName(folder) ?? string.Empty;

            if (IsAcceptableDerivedFolder(folder)) return folder;
        }
        return string.Empty;
    }

    public static bool IsAcceptableDerivedFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (folder.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)) return false;
        if (folder.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase)) return false;
        return ProtectedPaths.GetBlockReason(folder) is null;
    }

    /// <summary>"C:\App\app.exe",0 → C:\App\app.exe</summary>
    public static string ParseIconPath(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return string.Empty;
        var icon = displayIcon.Trim();

        if (icon.StartsWith('"'))
        {
            var end = icon.IndexOf('"', 1);
            icon = end > 0 ? icon[1..end] : icon.Trim('"');
        }
        else
        {
            var comma = icon.LastIndexOf(',');
            if (comma > 0 && int.TryParse(icon[(comma + 1)..].Trim(), out _))
                icon = icon[..comma];
        }

        icon = Environment.ExpandEnvironmentVariables(icon.Trim());
        return Path.IsPathFullyQualified(icon) ? icon : string.Empty;
    }

    private static string TrimFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.TrimEnd('\\', '/');
        // Keep "C:\" rather than turning it into the drive-relative "C:"
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }
}
