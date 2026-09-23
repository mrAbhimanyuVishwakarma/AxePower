namespace AxePower.Core.Models.Uninstaller;

/// <summary>What happened to one leftover item during cleanup.</summary>
public enum CleanupItemStatus
{
    /// <summary>Backed up (or recycled) and removed; verified gone.</summary>
    Deleted,

    /// <summary>It no longer existed when cleanup reached it.</summary>
    AlreadyGone,

    /// <summary>Deletion was attempted and failed; see the reason.</summary>
    Failed,

    /// <summary>Needs administrator rights and AxePower isn't elevated. Nothing was touched.</summary>
    SkippedNeedsAdmin,

    /// <summary>The backup failed, so the item was left in place (fail closed).</summary>
    SkippedBackupFailed,

    /// <summary>Refused by the protection rules; see the reason.</summary>
    Blocked,

    /// <summary>Dry run: nothing was touched.</summary>
    DryRun,
}

/// <summary>The honest per-item result of cleaning one leftover.</summary>
public sealed class CleanupItemResult
{
    public string ItemId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;
    public LeftoverItemType ItemType { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? ValueName { get; set; }
    public CleanupItemStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The .reg / .xml file holding the backup, relative to the session folder (empty when none).</summary>
    public string BackupFile { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string StatusLabel => Status switch
    {
        CleanupItemStatus.Deleted => "Deleted",
        CleanupItemStatus.AlreadyGone => "Already gone",
        CleanupItemStatus.Failed => "Failed",
        CleanupItemStatus.SkippedNeedsAdmin => "Needs admin",
        CleanupItemStatus.SkippedBackupFailed => "Backup failed",
        CleanupItemStatus.Blocked => "Blocked",
        CleanupItemStatus.DryRun => "Dry run",
        _ => Status.ToString(),
    };

    public string DisplayPath => string.IsNullOrEmpty(ValueName) ? Path : $"{Path}  →  {ValueName}";
}

/// <summary>Summary of a leftover cleanup: the per-item results and their counts.</summary>
public sealed class CleanupResult
{
    public List<CleanupItemResult> Items { get; set; } = new();
    public bool WasDryRun { get; set; }

    /// <summary>Folder holding this cleanup's backups and manifest.json.</summary>
    public string BackupFolder { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public int DeletedCount => Count(CleanupItemStatus.Deleted);
    public int AlreadyGoneCount => Count(CleanupItemStatus.AlreadyGone);
    public int FailedCount => Count(CleanupItemStatus.Failed);
    public int SkippedNeedsAdminCount => Count(CleanupItemStatus.SkippedNeedsAdmin);
    public int SkippedBackupFailedCount => Count(CleanupItemStatus.SkippedBackupFailed);
    public int BlockedCount => Count(CleanupItemStatus.Blocked);

    public long TotalBytesReclaimed =>
        Items.Where(i => i.Status == CleanupItemStatus.Deleted).Sum(i => i.SizeBytes);

    public string DisplayReclaimed
    {
        get
        {
            var bytes = TotalBytesReclaimed;
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024) return $"{mb / 1024:F2} GB";
            if (mb >= 1)    return $"{mb:F1} MB";
            double kb = bytes / 1024.0;
            if (kb >= 1)    return $"{kb:F0} KB";
            return $"{bytes} B";
        }
    }

    /// <summary>"3 deleted · 1 needs admin"-style summary built only from real per-item results.</summary>
    public string Summary
    {
        get
        {
            var parts = Items
                .GroupBy(i => i.StatusLabel)
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")
                .ToList();
            return parts.Count == 0 ? "Nothing cleaned" : string.Join(" · ", parts);
        }
    }

    private int Count(CleanupItemStatus status) => Items.Count(i => i.Status == status);
}
