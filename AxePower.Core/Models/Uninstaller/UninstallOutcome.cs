namespace AxePower.Core.Models.Uninstaller;

/// <summary>
/// The verified result of uninstalling one application. An app only counts as removed when
/// verification says so; exit codes only choose the wording of a failure.
/// </summary>
public enum UninstallOutcome
{
    /// <summary>Verified gone.</summary>
    Removed,

    /// <summary>The entry was already gone before we ran, or msiexec reported 1605 and it is verified gone.</summary>
    AlreadyRemoved,

    /// <summary>The uninstaller asked for a restart (3010/1641) and the app stays registered until then.</summary>
    RemovedPendingReboot,

    /// <summary>The uninstaller finished but the app is still registered.</summary>
    StillInstalled,

    /// <summary>The user cancelled the uninstaller, refused UAC, or stopped waiting.</summary>
    Cancelled,

    /// <summary>An error; see <see cref="UninstallResult.ErrorMessage"/>.</summary>
    Failed,

    /// <summary>A broken entry was backed up and deleted at the user's request.</summary>
    BrokenEntryRemoved,

    /// <summary>The user chose to skip the app, it was protected, or the queue was stopped before it.</summary>
    Skipped,

    /// <summary>The action needs administrator rights and AxePower isn't elevated.</summary>
    NeedsAdmin,

    /// <summary>Dry run: nothing was executed.</summary>
    DryRun,
}
