namespace AxePower.Core.Models.Uninstaller;

/// <summary>Summary of leftover cleanup actions.</summary>
public sealed class CleanupResult
{
    public int DeletedRegistryItems { get; set; }
    public int DeletedFiles { get; set; }
    public int DeletedFolders { get; set; }
    public int FailedItems { get; set; }
    public int SkippedProtectedItems { get; set; }
    public int SkippedLowConfidenceItems { get; set; }
    public long TotalBytesReclaimed { get; set; }
    public bool WasDryRun { get; set; }

    public string DisplayReclaimed
    {
        get
        {
            double mb = TotalBytesReclaimed / (1024.0 * 1024.0);
            if (mb >= 1024) return $"{mb / 1024:F2} GB";
            if (mb >= 1)    return $"{mb:F1} MB";
            double kb = TotalBytesReclaimed / 1024.0;
            if (kb >= 1)    return $"{kb:F0} KB";
            return $"{TotalBytesReclaimed} B";
        }
    }
}
