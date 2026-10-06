using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>How an item was backed up before it was deleted.</summary>
public enum BackupKind
{
    /// <summary>A registry key exported with <c>reg export</c> to a .reg file.</summary>
    RegistryKey,

    /// <summary>A registry value stored as typed JSON in the manifest.</summary>
    RegistryValue,

    /// <summary>A scheduled task's XML definition.</summary>
    ScheduledTask,

    /// <summary>A file or folder moved to the Recycle Bin.</summary>
    RecycleBin,

    /// <summary>Deleted permanently (Recycle Bin turned off); nothing to restore.</summary>
    None,
}

/// <summary>One line of a session's manifest.json.</summary>
public sealed class BackupManifestEntry
{
    public int Sequence { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ApplicationName { get; set; } = string.Empty;
    public LeftoverItemType ItemType { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? ValueName { get; set; }
    public BackupKind BackupKind { get; set; }

    /// <summary>.reg or .xml file name relative to the session folder.</summary>
    public string BackupFile { get; set; } = string.Empty;

    /// <summary>For value backups: the value, typed by <see cref="RegistryBackupItem.ValueKind"/>.</summary>
    public RegistryBackupItem? Value { get; set; }

    public CleanupItemStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>Set once the backup has been restored.</summary>
    public DateTime? RestoredAt { get; set; }
}

/// <summary>Everything backed up and deleted in one session, written atomically after every change.</summary>
public sealed class BackupManifest
{
    public string SessionId { get; set; } = string.Empty;
    public List<BackupManifestEntry> Entries { get; set; } = new();
}

/// <summary>
/// Stores backups under %LOCALAPPDATA%\AxePower\UninstallerHistory\{sessionId}\:
/// NNN.reg for keys, tasks\NNN.xml for scheduled tasks, and manifest.json listing every item.
/// </summary>
public sealed class BackupManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly LogService? _log;

    public BackupManifestStore() : this(UninstallHistoryService.DefaultRoot) { }

    public BackupManifestStore(LogService log) : this(UninstallHistoryService.DefaultRoot, log) { }

    public BackupManifestStore(string root, LogService? log = null)
    {
        Root = root;
        _log = log;
    }

    public string Root { get; }

    /// <summary>
    /// The session's backup folder. Session IDs are GUIDs; anything else (a hand-edited or very
    /// old history file) is reduced to a safe folder name so a bad ID can never walk out of Root.
    /// </summary>
    public string GetSessionFolder(string sessionId)
    {
        if (Guid.TryParse(sessionId, out var id)) return Path.Combine(Root, id.ToString());

        var safe = new string((sessionId ?? string.Empty)
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .Take(64)
            .ToArray());
        return Path.Combine(Root, safe.Length > 0 ? safe : "unknown-session");
    }

    public BackupManifest Load(string sessionId)
    {
        lock (_gate)
        {
            var path = Path.Combine(GetSessionFolder(sessionId), "manifest.json");
            try
            {
                if (File.Exists(path))
                {
                    var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), JsonOptions)
                                   ?? new BackupManifest { SessionId = sessionId };
                    var repair = JsonNullRepair.Repair(manifest);
                    if (repair.Changed) _log?.LogWarning($"[Uninstaller] The backup index for session {sessionId} had empty values ({repair.Describe()}).");
                    // The folder name is the session id, so it is known even when the file lost it.
                    if (manifest.SessionId.Length == 0) manifest.SessionId = sessionId;
                    return manifest;
                }
            }
            catch (JsonException ex)
            {
                // A damaged index must not be overwritten by the next Record: it is the only map from
                // backup files to what they restore. Keep it beside the new one for manual recovery.
                var kept = Path.Combine(Path.GetDirectoryName(path)!, $"manifest.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");
                try { File.Move(path, kept); }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { kept = path + " (could not be moved)"; }
                _log?.LogWarning($"[Uninstaller] The backup index for session {sessionId} was damaged and was kept as {kept}; a new index is started", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning($"[Uninstaller] The backup index for session {sessionId} could not be read", ex);
            }
            return new BackupManifest { SessionId = sessionId };
        }
    }

    /// <summary>Adds (or replaces, by sequence) an entry and rewrites manifest.json atomically.</summary>
    public void Record(string sessionId, BackupManifestEntry entry)
    {
        lock (_gate)
        {
            var manifest = Load(sessionId);
            manifest.Entries.RemoveAll(e => e.Sequence == entry.Sequence);
            manifest.Entries.Add(entry);
            manifest.Entries.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            Save(manifest);
        }
    }

    public void Save(BackupManifest manifest)
    {
        lock (_gate)
        {
            var folder = GetSessionFolder(manifest.SessionId);
            Directory.CreateDirectory(folder);
            AtomicFile.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        }
    }

    /// <summary>The next unused sequence number, used for backup file names (001.reg, 002.reg…).</summary>
    public int NextSequence(string sessionId)
    {
        lock (_gate)
        {
            var manifest = Load(sessionId);
            return manifest.Entries.Count == 0 ? 1 : manifest.Entries.Max(e => e.Sequence) + 1;
        }
    }
}

/// <summary>Write-to-temp then move, so a crash never leaves a half-written file.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
