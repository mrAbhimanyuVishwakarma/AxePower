using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Backs up and restores registry keys before leftover cleanup.</summary>
public interface IRegistryBackupService
{
    /// <summary>Backs up a registry key or value before deletion.</summary>
    Task<RegistryBackupItem?> BackupAsync(
        string sessionId,
        string applicationId,
        string applicationName,
        string hive,
        string keyPath,
        string? valueName = null);

    /// <summary>Restores all backed-up registry items for a given session.</summary>
    Task<int> RestoreSessionAsync(string sessionId);

    /// <summary>Saves all backup items for a session to disk.</summary>
    Task SaveSessionBackupsAsync(string sessionId, IReadOnlyList<RegistryBackupItem> items);

    /// <summary>Loads backed-up items for a session from disk.</summary>
    Task<IReadOnlyList<RegistryBackupItem>> LoadSessionBackupsAsync(string sessionId);
}
