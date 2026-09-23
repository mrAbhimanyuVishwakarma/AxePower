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

    /// <summary>The queue was stopped (or AxePower closed) before every app was processed.</summary>
    public bool WasCancelled { get; set; }

    /// <summary>
    /// True for a "Scan Leftovers" run started from the app list rather than an uninstall batch.
    /// </summary>
    public bool IsStandaloneScan { get; set; }

    /// <summary>
    /// The user finished with the leftover review (cleaned or closed it). False while it is
    /// still open or was saved for after an administrator restart.
    /// </summary>
    public bool LeftoverReviewDone { get; set; }

    /// <summary>Leftovers were found and the review hasn't been finished yet.</summary>
    public bool HasPendingLeftoverReview => LeftoverItems.Count > 0 && !LeftoverReviewDone;

    public int SuccessCount => Results.Count(r => r.Success);
    public int FailedCount => Results.Count(r => r.Outcome is UninstallOutcome.Failed or UninstallOutcome.NeedsAdmin);
    public int SkippedCount => Results.Count(r => r.Outcome == UninstallOutcome.Skipped);
    public int CancelledCount => Results.Count(r => r.Outcome == UninstallOutcome.Cancelled);
    public int StillInstalledCount => Results.Count(r => r.Outcome == UninstallOutcome.StillInstalled);
    public int DryRunCount => Results.Count(r => r.Outcome == UninstallOutcome.DryRun);

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

    /// <summary>"2 removed · 1 cancelled"-style summary of the outcomes, for toasts and the history list.</summary>
    public string OutcomeSummary
    {
        get
        {
            var parts = Results
                .GroupBy(r => r.OutcomeLabel)
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")
                .ToList();
            return parts.Count == 0 ? "No apps processed" : string.Join(" · ", parts);
        }
    }
}
