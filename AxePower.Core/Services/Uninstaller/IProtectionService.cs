using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Determines whether an application or leftover item is protected.</summary>
public interface IProtectionService
{
    /// <summary>Returns true if the application is protected and cannot be uninstalled via Uninstaller Pro.</summary>
    bool IsApplicationProtected(InstalledApplication application);

    /// <summary>Returns the reason the application is protected, or null if not protected.</summary>
    string? GetProtectionReason(InstalledApplication application);

    /// <summary>Returns true if the file/registry path is protected from deletion.</summary>
    bool IsPathProtected(string path);

    /// <summary>
    /// Why the file or registry path may not be deleted, or null when it may.
    /// A folder that is (or contains) another installed app's install folder is blocked.
    /// </summary>
    string? GetBlockReason(string path, IReadOnlyCollection<string> otherInstallLocations);

    /// <summary>Why the leftover may not be deleted (type-aware: values, tasks, informational items), or null.</summary>
    string? GetBlockReason(LeftoverItem item, IReadOnlyCollection<string> otherInstallLocations);

    /// <summary>Deleting the leftover needs administrator rights.</summary>
    bool RequiresElevation(LeftoverItem item);
}
