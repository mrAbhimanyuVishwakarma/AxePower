namespace AxePower.Core.Models.Uninstaller;

/// <summary>Result of a single application uninstall operation.</summary>
public sealed class UninstallResult
{
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>The verified outcome. Everything else on this class explains it.</summary>
    public UninstallOutcome Outcome { get; set; } = UninstallOutcome.Failed;

    private bool? _success;
    /// <summary>Only verified removals count as success.</summary>
    public bool Success
    {
        get => _success ?? (Outcome is UninstallOutcome.Removed
            or UninstallOutcome.AlreadyRemoved
            or UninstallOutcome.RemovedPendingReboot
            or UninstallOutcome.BrokenEntryRemoved);
        set
        {
            _success = value;
            if (value && Outcome == UninstallOutcome.Failed)
                Outcome = UninstallOutcome.Removed;
        }
    }

    public bool WasSkipped => Outcome == UninstallOutcome.Skipped;

    private bool? _wasDryRun;
    public bool WasDryRun
    {
        get => _wasDryRun ?? (Outcome == UninstallOutcome.DryRun);
        set => _wasDryRun = value;
    }

    public int? ExitCode { get; set; }

    /// <summary>Why the app wasn't removed, or a note about how it was (empty when there's nothing to add).</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    public bool RequiresReboot { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public ApplicationType ApplicationType { get; set; }
    public UninstallerKind UninstallerKind { get; set; }
    public string UninstallMethod { get; set; } = string.Empty;
    public string CommandExecuted { get; set; } = string.Empty;
    public bool WasQuiet { get; set; }

    /// <summary>Verbose msiexec log, when one was written.</summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>
    /// The app is still registered but its uninstaller has disappeared, so the listing can only
    /// be removed as a broken entry.
    /// </summary>
    public bool OfferRemoveBrokenEntry { get; set; }

    /// <summary>Short label for the outcome, for result lists and toasts.</summary>
    public string OutcomeLabel => Outcome switch
    {
        UninstallOutcome.Removed => "Removed",
        UninstallOutcome.AlreadyRemoved => "Already removed",
        UninstallOutcome.RemovedPendingReboot => "Restart required",
        UninstallOutcome.StillInstalled => "Still installed",
        UninstallOutcome.Cancelled => "Cancelled",
        UninstallOutcome.Failed => "Failed",
        UninstallOutcome.BrokenEntryRemoved => "Entry removed",
        UninstallOutcome.Skipped => "Skipped",
        UninstallOutcome.NeedsAdmin => "Needs admin",
        UninstallOutcome.DryRun => "Dry run",
        _ => Outcome.ToString(),
    };
}
