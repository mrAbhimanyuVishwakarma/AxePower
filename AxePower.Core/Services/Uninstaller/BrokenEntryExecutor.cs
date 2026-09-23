using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// "Remove broken entry": for an uninstall entry whose uninstaller is gone, back the key up with
/// <c>reg export</c> and delete only the listing. Files are never touched here. If the backup
/// fails the key stays (fail closed); HKLM keys need administrator rights.
/// </summary>
public sealed class BrokenEntryExecutor : IUninstallExecutor
{
    private readonly IRegistryBackupService _backup;
    private readonly IProtectionService _protection;
    private readonly BackupManifestStore _manifests;

    public BrokenEntryExecutor(IRegistryBackupService backup, IProtectionService protection, BackupManifestStore manifests)
    {
        _backup = backup;
        _protection = protection;
        _manifests = manifests;
    }

    public IReadOnlyCollection<UninstallerKind> SupportedKinds { get; } = [UninstallerKind.BrokenEntry];

    public async Task<UninstallExecution> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        UninstallRunContext context,
        CancellationToken cancellationToken = default)
    {
        const string method = "Remove broken entry";
        var sw = Stopwatch.StartNew();
        var keys = application.AllRegistryKeyPaths.Where(RegistryPath.KeyExists).ToList();
        var command = $"Back up and delete {string.Join(", ", keys)}";

        if (keys.Count == 0)
            return Result(ExecutionVerdict.EntryRemoved, "The entry was already gone.");

        foreach (var key in keys)
        {
            var blocked = _protection.GetBlockReason(key, []);
            if (blocked is not null)
                return Result(ExecutionVerdict.Failed, $"Refused to delete {key}: {blocked}");
        }

        if (settings.DryRunMode)
            return new UninstallExecution { Verdict = ExecutionVerdict.DryRun, CommandExecuted = $"[DRY RUN] Would {command}", Method = method };

        if (!context.IsElevated && keys.Any(k => k.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)))
            return Result(ExecutionVerdict.NeedsAdmin, "Removing this entry needs administrator rights.");

        var folder = _manifests.GetSessionFolder(context.SessionId);
        foreach (var key in keys)
        {
            var sequence = _manifests.NextSequence(context.SessionId);
            var file = $"{sequence:000}.reg";
            var entry = new BackupManifestEntry
            {
                Sequence = sequence,
                ApplicationName = application.DisplayName,
                ItemType = LeftoverItemType.RegistryKey,
                Path = key,
                BackupKind = BackupKind.RegistryKey,
                BackupFile = file,
            };

            if (!await _backup.ExportKeyAsync(key, Path.Combine(folder, file), cancellationToken).ConfigureAwait(false))
            {
                entry.Status = CleanupItemStatus.SkippedBackupFailed;
                entry.Reason = "The registry backup failed, so the entry was left in place.";
                _manifests.Record(context.SessionId, entry);
                return Result(ExecutionVerdict.Failed, entry.Reason);
            }

            try
            {
                RegistryPath.TryParse(key, out var hive, out var sub);
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                baseKey.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                entry.Status = RegistryPath.KeyExists(key) ? CleanupItemStatus.Failed : CleanupItemStatus.Deleted;
                if (entry.Status == CleanupItemStatus.Failed) entry.Reason = "The key is still there after deleting it.";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                entry.Status = CleanupItemStatus.Failed;
                entry.Reason = "Access denied.";
            }
            catch (Exception ex)
            {
                entry.Status = CleanupItemStatus.Failed;
                entry.Reason = ex.Message;
            }

            _manifests.Record(context.SessionId, entry);
            if (entry.Status != CleanupItemStatus.Deleted)
                return Result(ExecutionVerdict.Failed, $"Could not delete {key}: {entry.Reason}");
        }

        return Result(ExecutionVerdict.EntryRemoved, "Only the listing was removed; a .reg backup was saved.");

        UninstallExecution Result(ExecutionVerdict verdict, string message) => new()
        {
            Verdict = verdict,
            Message = message,
            CommandExecuted = command,
            Method = method,
            Duration = sw.Elapsed,
        };
    }
}
