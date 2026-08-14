using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Handles cleanup of leftover files and folders after an application is removed.</summary>
public interface IFileCleanupService
{
    /// <summary>
    /// Deletes a file, preferring Recycle Bin when enabled in settings.
    /// Returns true on success.
    /// </summary>
    Task<bool> DeleteFileAsync(string path, UninstallerSettings settings);

    /// <summary>
    /// Deletes a folder, preferring Recycle Bin when enabled in settings.
    /// Returns true on success.
    /// </summary>
    Task<bool> DeleteFolderAsync(string path, UninstallerSettings settings);
}
