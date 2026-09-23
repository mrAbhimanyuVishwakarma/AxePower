using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services.Uninstaller.Leftovers;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Who is cleaning, and what must not be touched.</summary>
public sealed class CleanupContext
{
    /// <summary>The session whose folder holds the backups and manifest.json.</summary>
    public string SessionId { get; init; } = Guid.NewGuid().ToString();

    public bool IsElevated { get; init; }

    /// <summary>Install folders of apps that are still installed (re-checked against every item).</summary>
    public IReadOnlyCollection<string> OtherInstallLocations { get; init; } = [];
}

public interface ILeftoverCleanupService
{
    /// <summary>
    /// Cleans the items one by one and reports exactly what happened to each. Nothing is
    /// counted as cleaned unless it was deleted and verified gone.
    /// </summary>
    Task<CleanupResult> CleanAsync(
        IReadOnlyList<LeftoverItem> items,
        UninstallerSettings settings,
        CleanupContext context,
        IProgress<CleanupItemResult>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deletes leftovers: protection re-check → needs-admin check → backup (fail closed) → delete →
/// verify. Registry keys are exported with <c>reg export</c>, values kept as typed JSON, tasks
/// as XML, and files and folders go to the Recycle Bin. Every item and its result is written
/// to the session's manifest.json.
/// </summary>
public sealed class LeftoverCleanupService : ILeftoverCleanupService
{
    private readonly IProtectionService _protection;
    private readonly IRegistryBackupService _backup;
    private readonly IFileCleanupService _files;
    private readonly BackupManifestStore _manifests;
    private readonly LogService? _log;

    public LeftoverCleanupService(
        IProtectionService protection,
        IRegistryBackupService backup,
        IFileCleanupService files,
        BackupManifestStore manifests,
        LogService? log = null)
    {
        _protection = protection;
        _backup = backup;
        _files = files;
        _manifests = manifests;
        _log = log;
    }

    public async Task<CleanupResult> CleanAsync(
        IReadOnlyList<LeftoverItem> items,
        UninstallerSettings settings,
        CleanupContext context,
        IProgress<CleanupItemResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new CleanupResult
        {
            WasDryRun = settings.DryRunMode,
            BackupFolder = _manifests.GetSessionFolder(context.SessionId),
        };

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sequence = settings.DryRunMode ? 0 : _manifests.NextSequence(context.SessionId);
            var itemResult = new CleanupItemResult
            {
                ItemId = item.ItemId,
                ApplicationName = item.ApplicationName,
                ItemType = item.ItemType,
                Path = item.Path,
                ValueName = item.ValueName,
                SizeBytes = item.SizeBytes,
            };

            BackupKind backupKind;
            RegistryBackupItem? valueBackup;
            try
            {
                (backupKind, valueBackup) = await CleanOneAsync(item, settings, context, sequence, itemResult, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                itemResult.Status = CleanupItemStatus.Failed;
                itemResult.Reason = ex.Message;
                (backupKind, valueBackup) = (BackupKind.None, null);
            }

            _log?.LogInfo($"[Uninstaller] Cleanup {itemResult.StatusLabel}: {itemResult.DisplayPath}" +
                          (itemResult.Reason.Length > 0 ? $" ({itemResult.Reason})" : string.Empty));

            if (!settings.DryRunMode)
            {
                _manifests.Record(context.SessionId, new BackupManifestEntry
                {
                    Sequence = sequence,
                    ApplicationName = item.ApplicationName,
                    ItemType = item.ItemType,
                    Path = item.Path,
                    ValueName = item.ValueName,
                    BackupKind = backupKind,
                    BackupFile = itemResult.BackupFile,
                    Value = valueBackup,
                    Status = itemResult.Status,
                    Reason = itemResult.Reason,
                });
            }

            result.Items.Add(itemResult);
            progress?.Report(itemResult);
        }

        return result;
    }

    /// <summary>Handles one item and returns how it was backed up (and the value, for value backups).</summary>
    private async Task<(BackupKind Kind, RegistryBackupItem? Value)> CleanOneAsync(LeftoverItem item, UninstallerSettings settings,
        CleanupContext context, int sequence, CleanupItemResult r, CancellationToken cancellationToken)
    {
        // ── 1. Protection, re-checked against the current state ──────────────
        var blocked = item.BlockedReason ?? _protection.GetBlockReason(item, context.OtherInstallLocations);
        if (blocked is null && item.ItemType == LeftoverItemType.RegistryKey &&
            item.Path.Contains(@"\Classes\CLSID\", StringComparison.OrdinalIgnoreCase) &&
            !item.HasEvidence(nameof(ConfidenceRecords.ExplicitConnection)))
            blocked = "COM registrations are only removed with explicit evidence.";
        if (blocked is not null)
            return Done(CleanupItemStatus.Blocked, blocked);

        // ── 2. Rights ─────────────────────────────────────────────────────────
        if ((item.RequiresElevation || _protection.RequiresElevation(item)) && !context.IsElevated)
            return Done(CleanupItemStatus.SkippedNeedsAdmin, "Needs administrator rights.");

        // ── 3. Still there? ───────────────────────────────────────────────────
        if (!Exists(item))
            return Done(CleanupItemStatus.AlreadyGone, string.Empty);

        if (settings.DryRunMode)
            return Done(CleanupItemStatus.DryRun, "Dry run: nothing was deleted.");

        var folder = _manifests.GetSessionFolder(context.SessionId);
        var isHkcu = item.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase);
        // HKLM is always backed up; the setting only lets HKCU skip it
        var backupRegistry = settings.BackupRegistryChanges || !isHkcu;

        switch (item.ItemType)
        {
            case LeftoverItemType.RegistryKey:
            {
                var kind = BackupKind.None;
                if (backupRegistry)
                {
                    var file = $"{sequence:000}.reg";
                    if (!await _backup.ExportKeyAsync(item.Path, Path.Combine(folder, file), cancellationToken).ConfigureAwait(false))
                        return Done(CleanupItemStatus.SkippedBackupFailed, "The registry backup failed, so the key was left in place.");
                    r.BackupFile = file;
                    kind = BackupKind.RegistryKey;
                }

                DeleteKey(item.Path);
                return Verify(!RegistryPath.KeyExists(item.Path), kind);
            }

            case LeftoverItemType.RegistryValue:
            case LeftoverItemType.StartupEntry:
            {
                RegistryBackupItem? value = null;
                if (backupRegistry)
                {
                    value = _backup.BackupValue(item.Path, item.ValueName!);
                    if (value is null)
                        return Done(CleanupItemStatus.SkippedBackupFailed, "The value could not be backed up, so it was left in place.");
                    value.SessionId = context.SessionId;
                    value.ApplicationId = item.ApplicationId;
                    value.ApplicationName = item.ApplicationName;
                }

                DeleteValue(item.Path, item.ValueName!);
                var removed = Verify(!RegistryPath.ValueExists(item.Path, item.ValueName!),
                    value is null ? BackupKind.None : BackupKind.RegistryValue);
                return (removed.Kind, value);
            }

            case LeftoverItemType.ScheduledTask:
            {
                var xml = ScheduledTaskService.GetXml(item.Path);
                if (xml is null)
                    return Done(CleanupItemStatus.SkippedBackupFailed, "The task definition could not be read, so it was left in place.");
                var file = Path.Combine("tasks", $"{sequence:000}.xml");
                Directory.CreateDirectory(Path.Combine(folder, "tasks"));
                await File.WriteAllTextAsync(Path.Combine(folder, file), xml, cancellationToken).ConfigureAwait(false);
                r.BackupFile = file;

                ScheduledTaskService.Delete(item.Path);
                return Verify(!ScheduledTaskService.Exists(item.Path), BackupKind.ScheduledTask);
            }

            case LeftoverItemType.Folder:
            {
                var ok = await _files.DeleteFolderAsync(item.Path, settings).ConfigureAwait(false);
                return Verify(ok && !Directory.Exists(item.Path), settings.MoveToRecycleBin ? BackupKind.RecycleBin : BackupKind.None,
                    "Could not delete the folder (in use or access denied).");
            }

            case LeftoverItemType.File:
            case LeftoverItemType.Shortcut:
            {
                var ok = await _files.DeleteFileAsync(item.Path, settings).ConfigureAwait(false);
                return Verify(ok && !File.Exists(item.Path), settings.MoveToRecycleBin ? BackupKind.RecycleBin : BackupKind.None,
                    "Could not delete the file (in use or access denied).");
            }

            default:
                return Done(CleanupItemStatus.Blocked, "This kind of item is shown for information only.");
        }

        (BackupKind, RegistryBackupItem?) Done(CleanupItemStatus status, string reason)
        {
            r.Status = status;
            r.Reason = reason;
            return (BackupKind.None, null);
        }

        (BackupKind Kind, RegistryBackupItem? Value) Verify(bool gone, BackupKind kind, string failure = "It is still there after deleting it.")
        {
            r.Status = gone ? CleanupItemStatus.Deleted : CleanupItemStatus.Failed;
            r.Reason = gone ? string.Empty : failure;
            return (kind, null);
        }
    }

    private static bool Exists(LeftoverItem item) => item.ItemType switch
    {
        LeftoverItemType.RegistryKey => RegistryPath.KeyExists(item.Path),
        LeftoverItemType.RegistryValue or LeftoverItemType.StartupEntry =>
            item.ValueName is not null && RegistryPath.ValueExists(item.Path, item.ValueName),
        LeftoverItemType.Folder => Directory.Exists(item.Path),
        LeftoverItemType.File or LeftoverItemType.Shortcut => File.Exists(item.Path),
        LeftoverItemType.ScheduledTask => ScheduledTaskService.Exists(item.Path),
        _ => false,
    };

    private static void DeleteKey(string fullPath)
    {
        if (!RegistryPath.TryParse(fullPath, out var hive, out var sub) || sub.Split('\\').Length < 2) return;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Reported by the verification that follows
        }
    }

    private static void DeleteValue(string fullPath, string valueName)
    {
        try
        {
            using var key = RegistryPath.OpenKey(fullPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Reported by the verification that follows
        }
    }
}
