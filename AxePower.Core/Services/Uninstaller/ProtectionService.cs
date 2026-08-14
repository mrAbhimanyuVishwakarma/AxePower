using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Determines whether an application or leftover path is protected.
/// Protected items are never presented as removable in Uninstaller Pro.
/// </summary>
public sealed class ProtectionService : IProtectionService
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly string SystemDir =
        Environment.GetFolderPath(Environment.SpecialFolder.System);

    private static readonly string ProgramFilesWindowsNt =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Windows NT");

    private static readonly string WindowsAppsDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");

    // Registry paths that must never be deleted by the leftover cleaner
    private static readonly string[] ProtectedRegistryPrefixes =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows NT",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",   // global startup — informational only
        @"HKLM\SYSTEM\CurrentControlSet",
        @"HKLM\SOFTWARE\Classes",
        @"HKLM\SAM",
        @"HKLM\SECURITY",
    ];

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

    public bool IsApplicationProtected(InstalledApplication application)
    {
        // AxePower itself — always protected
        if (application.DisplayName.Contains("AxePower", StringComparison.OrdinalIgnoreCase))
            return true;

        // Non-removable AppX/MSIX
        if (application.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX
            && !application.CanUninstall)
            return true;

        // Windows system components
        if (application.IsSystemComponent)
            return true;

        // Protected name prefixes
        foreach (var prefix in ProtectedAppNamePrefixes)
        {
            if (application.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string? GetProtectionReason(InstalledApplication application)
    {
        if (application.DisplayName.Contains("AxePower", StringComparison.OrdinalIgnoreCase))
            return "AxePower cannot be removed from inside Uninstaller Pro. " +
                   "Use the AxePower installer or Windows Settings to remove it.";

        if (application.ApplicationType is ApplicationType.AppX or ApplicationType.MSIX
            && !application.CanUninstall)
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

    public bool IsPathProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var normalized = path.TrimEnd('\\', '/');

        // Windows system directories — never touch
        if (IsSubPathOf(normalized, WindowsDir)) return true;
        if (IsSubPathOf(normalized, SystemDir))  return true;
        if (IsSubPathOf(normalized, WindowsAppsDir)) return true;
        if (IsSubPathOf(normalized, ProgramFilesWindowsNt)) return true;

        // Protected registry prefixes
        foreach (var prefix in ProtectedRegistryPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsSubPathOf(string path, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return false;
        return path.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }
}
