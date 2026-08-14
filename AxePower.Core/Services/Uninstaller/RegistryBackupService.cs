using System.Text.Json;
using AxePower.Core.Models.Uninstaller;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Backs up registry keys/values before leftover cleanup and provides session restore.
/// Backups are stored as JSON in %AppData%\AxePower\UninstallerHistory\{sessionId}\.
/// </summary>
public sealed class RegistryBackupService : IRegistryBackupService
{
    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AxePower", "UninstallerHistory");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<RegistryBackupItem?> BackupAsync(
        string sessionId,
        string applicationId,
        string applicationName,
        string hive,
        string keyPath,
        string? valueName = null)
    {
        try
        {
            var item = new RegistryBackupItem
            {
                SessionId = sessionId,
                ApplicationId = applicationId,
                ApplicationName = applicationName,
                Hive = hive,
                KeyPath = keyPath,
                ValueName = valueName,
            };

            if (valueName is not null)
            {
                var (registryHive, subPath) = SplitHivePath($"{hive}\\{keyPath}");
                using var baseKey = OpenBaseKey(registryHive);
                if (baseKey is not null)
                {
                    using var key = baseKey.OpenSubKey(subPath, false);
                    if (key is not null)
                    {
                        var value = key.GetValue(valueName);
                        var kind  = key.GetValueKind(valueName);
                        item.ValueData = JsonSerializer.Serialize(value);
                        item.ValueKind = kind.ToString();
                    }
                }
            }

            return await Task.FromResult(item);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveSessionBackupsAsync(string sessionId, IReadOnlyList<RegistryBackupItem> items)
    {
        try
        {
            var dir = GetSessionDir(sessionId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "registry-backup.json");
            var json = JsonSerializer.Serialize(items, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        catch { }
    }

    public async Task<IReadOnlyList<RegistryBackupItem>> LoadSessionBackupsAsync(string sessionId)
    {
        try
        {
            var path = Path.Combine(GetSessionDir(sessionId), "registry-backup.json");
            if (!File.Exists(path)) return [];
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<List<RegistryBackupItem>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<int> RestoreSessionAsync(string sessionId)
    {
        var items = await LoadSessionBackupsAsync(sessionId);
        int restored = 0;

        foreach (var item in items)
        {
            try
            {
                var fullPath = $"{item.Hive}\\{item.KeyPath}";
                var (registryHive, subPath) = SplitHivePath(fullPath);
                using var baseKey = OpenBaseKey(registryHive);
                if (baseKey is null) continue;

                using var key = baseKey.CreateSubKey(subPath, writable: true);
                if (key is null) continue;

                if (item.ValueName is not null && item.ValueData is not null)
                {
                    if (Enum.TryParse<RegistryValueKind>(item.ValueKind, out var kind))
                    {
                        var value = JsonSerializer.Deserialize<object>(item.ValueData);
                        key.SetValue(item.ValueName, value ?? string.Empty, kind);
                        restored++;
                    }
                }
                else
                {
                    // Key was deleted — restoring the key itself is sufficient
                    restored++;
                }
            }
            catch { }
        }

        return restored;
    }

    private static string GetSessionDir(string sessionId) =>
        Path.Combine(BackupRoot, sessionId);

    private static (RegistryHive hive, string subPath) SplitHivePath(string fullPath)
    {
        if (fullPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, fullPath[5..]);
        if (fullPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, fullPath[5..]);
        return (RegistryHive.LocalMachine, fullPath);
    }

    private static RegistryKey? OpenBaseKey(RegistryHive hive) =>
        hive switch
        {
            RegistryHive.CurrentUser  => Registry.CurrentUser,
            RegistryHive.LocalMachine => Registry.LocalMachine,
            _ => null,
        };
}
