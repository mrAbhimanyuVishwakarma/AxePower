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
}
