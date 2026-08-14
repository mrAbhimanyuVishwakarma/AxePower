namespace AxePower.Core.Models.Uninstaller;

/// <summary>A recorded uninstall session for the history view.</summary>
public sealed class UninstallSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }
    public List<string> ApplicationNames { get; set; } = new();
    public List<UninstallResult> Results { get; set; } = new();
    public List<LeftoverItem> LeftoverItems { get; set; } = new();
    public List<LeftoverItem> CleanedItems { get; set; } = new();
    public List<RegistryBackupItem> RegistryBackups { get; set; } = new();
    public CleanupResult? CleanupResult { get; set; }
    public bool SystemRestorePointCreated { get; set; }
    public string ScanMode { get; set; } = "Moderate";
    public bool WasDryRun { get; set; }

    public int SuccessCount => Results.Count(r => r.Success && !r.WasSkipped);
    public int FailedCount  => Results.Count(r => !r.Success && !r.WasSkipped);
    public int SkippedCount => Results.Count(r => r.WasSkipped);

    public string DisplayDuration
    {
        get
        {
            if (EndTime is null) return "In progress";
            var elapsed = EndTime.Value - StartTime;
            return elapsed.TotalMinutes >= 1
                ? $"{elapsed.TotalMinutes:F0}m {elapsed.Seconds}s"
                : $"{elapsed.Seconds}s";
        }
    }
}
