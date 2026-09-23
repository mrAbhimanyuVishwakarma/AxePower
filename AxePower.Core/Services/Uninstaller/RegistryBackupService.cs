using System.Diagnostics;
using System.Text.Json;
using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Real registry backups: whole keys go through <c>reg export … /reg:64</c> to .reg files (the
/// same format Windows' own tools restore), single values are kept as typed JSON so their kind
/// and data survive exactly. <c>reg.exe</c> always comes from System32 and gets its arguments
/// through ArgumentList.
/// </summary>
public sealed class RegistryBackupService : IRegistryBackupService
{
    private static readonly TimeSpan RegTimeout = TimeSpan.FromSeconds(60);
    private readonly BackupManifestStore _manifests;

    public RegistryBackupService() : this(new BackupManifestStore()) { }

    public RegistryBackupService(BackupManifestStore manifests)
    {
        _manifests = manifests;
    }

    public static string RegExePath => Path.Combine(Environment.SystemDirectory, "reg.exe");

    public async Task<bool> ExportKeyAsync(string fullKeyPath, string targetFile, CancellationToken cancellationToken = default)
    {
        if (!RegistryPath.TryParse(fullKeyPath, out var hive, out var sub) || sub.Length == 0) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

        var exitCode = await RunRegAsync(cancellationToken, "export", $"{RegistryPath.HiveName(hive)}\\{sub}",
            targetFile, "/y", "/reg:64").ConfigureAwait(false);

        try
        {
            return exitCode == 0 && File.Exists(targetFile) && new FileInfo(targetFile).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ImportAsync(string regFile, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(regFile)) return false;
        var exitCode = await RunRegAsync(cancellationToken, "import", regFile, "/reg:64").ConfigureAwait(false);
        return exitCode == 0;
    }

    public RegistryBackupItem? BackupValue(string fullKeyPath, string valueName)
    {
        try
        {
            if (!RegistryPath.TryParse(fullKeyPath, out var hive, out var sub)) return null;
            using var key = RegistryPath.OpenKey(fullKeyPath);
            if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                return null;

            var kind = key.GetValueKind(valueName);
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

            string json;
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    json = JsonSerializer.Serialize(raw as string ?? string.Empty);
                    break;
                case RegistryValueKind.DWord:
                    json = JsonSerializer.Serialize(Convert.ToInt32(raw));
                    break;
                case RegistryValueKind.QWord:
                    json = JsonSerializer.Serialize(Convert.ToInt64(raw));
                    break;
                case RegistryValueKind.MultiString:
                    json = JsonSerializer.Serialize(raw as string[] ?? []);
                    break;
                case RegistryValueKind.Binary:
                case RegistryValueKind.None:
                    json = JsonSerializer.Serialize(raw as byte[] ?? []);
                    break;
                default:
                    // A kind SetValue can't write back: refuse, so the value isn't deleted
                    return null;
            }

            return new RegistryBackupItem
            {
                Hive = RegistryPath.HiveName(hive),
                KeyPath = sub,
                ValueName = valueName,
                ValueKind = kind.ToString(),
                ValueData = json,
            };
        }
        catch
        {
            return null;
        }
    }

    public bool RestoreValue(RegistryBackupItem item)
    {
        try
        {
            if (item.ValueName is null || item.ValueData is null) return false;
            if (!Enum.TryParse<RegistryValueKind>(item.ValueKind, out var kind)) return false;
            if (!RegistryPath.TryParse(item.FullKeyPath, out var hive, out var sub) || sub.Length == 0) return false;

            object value = kind switch
            {
                RegistryValueKind.String or RegistryValueKind.ExpandString =>
                    JsonSerializer.Deserialize<string>(item.ValueData) ?? string.Empty,
                RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(item.ValueData),
                RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(item.ValueData),
                RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(item.ValueData) ?? [],
                RegistryValueKind.Binary or RegistryValueKind.None =>
                    JsonSerializer.Deserialize<byte[]>(item.ValueData) ?? [],
                _ => throw new NotSupportedException(),
            };

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(sub, writable: true);
            if (key is null) return false;
            key.SetValue(item.ValueName, value, kind);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<RestoreReport> RestoreSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var report = new RestoreReport();
        var manifest = _manifests.Load(sessionId);
        var folder = _manifests.GetSessionFolder(sessionId);

        var pending = manifest.Entries
            .Where(e => e.Status is CleanupItemStatus.Deleted && e.RestoredAt is null)
            .OrderByDescending(e => e.Sequence)
            .ToList();

        // .reg files first (newest first), then values, then tasks
        var ordered = pending.Where(e => e.BackupKind == BackupKind.RegistryKey)
            .Concat(pending.Where(e => e.BackupKind == BackupKind.RegistryValue))
            .Concat(pending.Where(e => e.BackupKind == BackupKind.ScheduledTask))
            .ToList();

        foreach (var entry in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool ok = entry.BackupKind switch
            {
                BackupKind.RegistryKey => await ImportAsync(Path.Combine(folder, entry.BackupFile), cancellationToken).ConfigureAwait(false),
                BackupKind.RegistryValue => entry.Value is not null && RestoreValue(entry.Value),
                BackupKind.ScheduledTask => ScheduledTaskService.TryRegisterFromXmlFile(entry.Path, Path.Combine(folder, entry.BackupFile)),
                _ => false,
            };

            if (ok)
            {
                entry.RestoredAt = DateTime.Now;
                report.Restored++;
            }
            else
            {
                report.Failed++;
                report.Messages.Add($"Could not restore {entry.Path}{(entry.ValueName is null ? string.Empty : $" → {entry.ValueName}")}" +
                                    (entry.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? " (needs administrator rights)" : string.Empty));
            }
        }

        report.NotRestorable = pending.Count(e => e.BackupKind == BackupKind.RecycleBin);
        if (report.NotRestorable > 0)
            report.Messages.Add($"{report.NotRestorable} file(s) and folder(s) are in the Recycle Bin; restore them from there.");

        _manifests.Save(manifest);
        return report;
    }

    private static async Task<int> RunRegAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = RegExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return -1;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RegTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }
}
