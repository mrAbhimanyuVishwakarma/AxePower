using System.Text.RegularExpressions;
using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// The one set of protection rules in Uninstaller Pro: which applications may not be removed and
/// which leftovers may never be deleted. Discovery, the batch, the leftover scanner and cleanup
/// all ask this service; nothing keeps its own copy of the rules.
/// </summary>
public sealed partial class ProtectionService : IProtectionService
{
    // Application name prefixes that are always protected
    private static readonly string[] ProtectedAppNamePrefixes =
    [
        "AxePower",
        "Windows ",
        "Microsoft Visual C++",
        "Microsoft .NET",
        "Microsoft DirectX",
        "Visual C++ ",
        "DirectX ",
    ];

    /// <summary>Top-level keys under SOFTWARE that belong to Windows or are shared by every app.</summary>
    private static readonly HashSet<string> SharedSoftwareKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Policies", "Clients", "RegisteredApplications", "ODBC", "WOW6432Node", "Windows",
        "Khronos", "Classes", "Microsoft",
    };

    // ── Applications ─────────────────────────────────────────────────────────

    public bool IsApplicationProtected(InstalledApplication application) =>
        GetProtectionReason(application) is not null;

    public string? GetProtectionReason(InstalledApplication application)
    {
        if (application.DisplayName.Contains("AxePower", StringComparison.OrdinalIgnoreCase))
            return "AxePower cannot be removed from inside Uninstaller. " +
                   "Use the AxePower installer or Windows Settings to remove it.";

        if (IsStorePackage(application) && !application.CanUninstall)
            return "This Windows package is marked as non-removable by the operating system.";

        if (application.IsSystemComponent)
            return "This is a Windows system component and cannot be removed safely.";

        foreach (var prefix in ProtectedAppNamePrefixes)
        {
            if (application.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"'{application.DisplayName}' is a protected system component.";
        }

        return null;
    }

    private static bool IsStorePackage(InstalledApplication app) =>
        app.UninstallerKind == UninstallerKind.StoreApp ||
        app.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX;

    // ── Leftover paths ───────────────────────────────────────────────────────

    public bool IsPathProtected(string path) => GetBlockReason(path, []) is not null;

    public string? GetBlockReason(string path, IReadOnlyCollection<string> otherInstallLocations) =>
        RegistryPath.IsRegistryPath(path)
            ? GetRegistryBlockReason(path, valueName: null)
            : GetFileSystemBlockReason(path, otherInstallLocations);

    public string? GetBlockReason(LeftoverItem item, IReadOnlyCollection<string> otherInstallLocations)
    {
        switch (item.ItemType)
        {
            case LeftoverItemType.Service:
                return "Services are shown for information only.";
            case LeftoverItemType.FirewallRule:
                return "Firewall rules are shown for information only.";
            case LeftoverItemType.ScheduledTask:
                return GetTaskBlockReason(item.Path);
            case LeftoverItemType.RegistryKey:
                return GetRegistryBlockReason(item.Path, valueName: null);
            case LeftoverItemType.RegistryValue:
            case LeftoverItemType.StartupEntry:
                if (!RegistryPath.IsRegistryPath(item.Path))
                    return GetFileSystemBlockReason(item.Path, otherInstallLocations);
                if (string.IsNullOrEmpty(item.ValueName))
                    return "A registry value item without a value name.";
                return GetRegistryBlockReason(item.Path, item.ValueName);
            default:
                return GetFileSystemBlockReason(item.Path, otherInstallLocations);
        }
    }

    /// <summary>
    /// Deleting the item needs administrator rights: anything in HKLM, Program Files or
    /// ProgramData, the Windows folder, and machine-wide tasks.
    /// </summary>
    public bool RequiresElevation(LeftoverItem item)
    {
        if (item.ItemType == LeftoverItemType.ScheduledTask) return item.RequiresElevation;

        if (RegistryPath.TryParse(item.Path, out var hive, out _))
            return hive != RegistryHive.CurrentUser;

        foreach (var folder in AdminFolders())
            if (ProtectedPaths.IsSameOrUnder(item.Path, folder))
                return true;
        return false;
    }

    private static IEnumerable<string> AdminFolders()
    {
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonDesktopDirectory,
                 })
        {
            var path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    // ── File system ──────────────────────────────────────────────────────────

    private static string? GetFileSystemBlockReason(string path, IReadOnlyCollection<string> otherInstallLocations)
    {
        var reason = ProtectedPaths.GetBlockReason(path);
        if (reason is not null) return reason;

        foreach (var other in otherInstallLocations)
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            // The path is (or contains) a folder another installed app lives in
            if (ProtectedPaths.IsSameOrUnder(other, path))
                return $"Used by another installed app ({other})";
        }
        return null;
    }

    // ── Registry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Registry rules. Outside HKLM\SOFTWARE and HKCU\Software nothing is deletable. Inside,
    /// Windows' own subtrees (Microsoft, Classes, Policies…) are an allow-list: only uninstall
    /// entries, App Paths entries, COM CLSID keys and a few named values (Run, compatibility
    /// flags, MUI cache) may go. Everything else under SOFTWARE is a vendor key and is allowed
    /// below the hive root.
    /// </summary>
    public static string? GetRegistryBlockReason(string path, string? valueName)
    {
        if (!RegistryPath.TryParse(path, out var hive, out var subKey))
            return "Not a registry path.";

        if (hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser))
            return $"{RegistryPath.HiveName(hive)} is never cleaned.";

        var segments = subKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return "Too close to the registry root.";

        if (!segments[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
            return hive == RegistryHive.LocalMachine
                ? "Part of the Windows system registry."
                : "Only HKCU\\Software is cleaned.";

        var rest = segments.Skip(1).ToArray();
        if (rest[0].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
            rest = rest.Skip(1).ToArray();
        if (rest.Length == 0)
            return "A registry root.";

        var top = rest[0];
        if (top.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            return MicrosoftSubtreeRule(rest, valueName);
        if (top.Equals("Classes", StringComparison.OrdinalIgnoreCase))
            return ClassesSubtreeRule(hive, rest, valueName);
        if (SharedSoftwareKeys.Contains(top))
            return "A registry area shared by Windows and other apps.";

        return null;
    }

    private static string? MicrosoftSubtreeRule(string[] rest, string? valueName)
    {
        // rest = Microsoft\...
        var path = string.Join('\\', rest);

        if (valueName is null)
        {
            // One uninstall entry or one App Paths entry: exactly one level below the container
            if (IsDirectChildOf(path, @"Microsoft\Windows\CurrentVersion\Uninstall") ||
                IsDirectChildOf(path, @"Microsoft\Windows\CurrentVersion\App Paths"))
                return null;
            return "Part of the Windows / Microsoft registry.";
        }

        if (IsOneOf(path,
                @"Microsoft\Windows\CurrentVersion\Run",
                @"Microsoft\Windows\CurrentVersion\RunOnce",
                @"Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
                @"Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"))
            return null;
        return "Part of the Windows / Microsoft registry.";
    }

    private static string? ClassesSubtreeRule(RegistryHive hive, string[] rest, string? valueName)
    {
        // rest = Classes\...
        if (valueName is null)
        {
            // COM class registrations only: Classes\CLSID\{guid}
            if (rest.Length == 3 &&
                rest[1].Equals("CLSID", StringComparison.OrdinalIgnoreCase) &&
                GuidRegex().IsMatch(rest[2]))
                return null;
            return "Shared file-association and COM registration area.";
        }

        if (hive == RegistryHive.CurrentUser &&
            IsOneOf(string.Join('\\', rest), @"Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"))
            return null;
        return "Shared file-association and COM registration area.";
    }

    private static bool IsDirectChildOf(string path, string container) =>
        path.StartsWith(container + "\\", StringComparison.OrdinalIgnoreCase) &&
        path.IndexOf('\\', container.Length + 1) < 0;

    private static bool IsOneOf(string path, params string[] keys) =>
        keys.Any(k => path.Equals(k, StringComparison.OrdinalIgnoreCase));

    // ── Scheduled tasks ──────────────────────────────────────────────────────

    private static string? GetTaskBlockReason(string taskPath)
    {
        if (string.IsNullOrWhiteSpace(taskPath) || !taskPath.StartsWith('\\'))
            return "Not a scheduled task path.";
        if (taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
            return "A Windows scheduled task.";
        return null;
    }

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidRegex();
}
