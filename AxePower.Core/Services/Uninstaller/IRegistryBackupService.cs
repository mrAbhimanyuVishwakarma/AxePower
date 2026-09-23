using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Backs up registry keys and values before deletion and restores them.</summary>
public interface IRegistryBackupService
{
    /// <summary>
    /// Exports a key (with all its values and sub-keys) to a .reg file with <c>reg export</c>.
    /// True only when reg.exe succeeded and the file exists and isn't empty.
    /// </summary>
    Task<bool> ExportKeyAsync(string fullKeyPath, string targetFile, CancellationToken cancellationToken = default);

    /// <summary>Imports a .reg file with <c>reg import</c> (HKLM content needs administrator rights).</summary>
    Task<bool> ImportAsync(string regFile, CancellationToken cancellationToken = default);

    /// <summary>Reads one value with its kind; null when the key or value doesn't exist.</summary>
    RegistryBackupItem? BackupValue(string fullKeyPath, string valueName);

    /// <summary>Writes a value back with its original kind. True on success.</summary>
    bool RestoreValue(RegistryBackupItem item);

    /// <summary>
    /// Restores a session's backups in reverse order: .reg files, then values, then scheduled
    /// tasks. Items already restored are skipped.
    /// </summary>
    Task<RestoreReport> RestoreSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>What a restore did, item by item.</summary>
public sealed class RestoreReport
{
    public int Restored { get; set; }
    public int Failed { get; set; }
    public int NotRestorable { get; set; }
    public List<string> Messages { get; set; } = new();

    public string Summary =>
        $"{Restored} restored" +
        (Failed > 0 ? $", {Failed} failed" : string.Empty) +
        (NotRestorable > 0 ? $", {NotRestorable} in the Recycle Bin" : string.Empty);
}
