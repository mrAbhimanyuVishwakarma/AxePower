namespace AxePower.Core.Models.Uninstaller;

/// <summary>Progress update during a batch uninstall operation.</summary>
public sealed class UninstallProgress
{
    public string CurrentApplicationName { get; set; } = string.Empty;
    public string CurrentApplicationId { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int CompletedCount { get; set; }
    public UninstallPhase Phase { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public bool IsCancelled { get; set; }
    public bool IsPaused { get; set; }

    /// <summary>The current app's uninstaller runs without its own UI (so "Stop uninstaller" may be offered).</summary>
    public bool IsQuiet { get; set; }

    public double ProgressPercent =>
        TotalCount > 0 ? (CompletedCount / (double)TotalCount) * 100.0 : 0;
}

public enum UninstallPhase
{
    Idle,
    PreFlight,
    CheckingProcess,
    CreatingRestorePoint,
    Uninstalling,
    WaitingForUninstaller,
    Verifying,
    ScanningLeftovers,
    Complete,
    Failed,
    Cancelled,
}
