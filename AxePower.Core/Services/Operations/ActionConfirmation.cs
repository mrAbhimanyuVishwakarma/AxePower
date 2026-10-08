namespace AxePower.Core.Services.Operations;

/// <summary>How much a mistaken click could cost. High needs an explicit "I understand" before the action button works.</summary>
public enum RiskLevel { Low, Medium, High }

/// <summary>What has to happen before the change fully takes effect.</summary>
public enum RestartNeed { None, SignOut, Restart, RestartApp }

/// <summary>
/// Everything a confirmation for a destructive or system-wide action must show (Prompt 27). Built
/// from metadata (an <see cref="OperationRecord"/> or a feature's own description), never from a
/// generic "Are you sure?", so the dialog's content can be tested without the dialog.
/// </summary>
public sealed record ActionConfirmation
{
    /// <summary>Plain-language action and scope, e.g. "Remove 14 leftover files and registry keys of Spotify".</summary>
    public required string Action { get; init; }

    /// <summary>The feature asking, e.g. "Uninstaller Pro".</summary>
    public required string Source { get; init; }

    /// <summary>The apps, files or settings affected, one line each (the dialog shows them all, scrollable).</summary>
    public IReadOnlyList<string> Affected { get; init; } = Array.Empty<string>();

    public bool RequiresAdministrator { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Medium;

    /// <summary>What is backed up first and how to get it back, or that nothing is.</summary>
    public required string Backup { get; init; }

    public RestartNeed Restart { get; init; } = RestartNeed.None;

    public Reversibility Reversibility { get; init; }

    /// <summary>Why it is not fully reversible, in plain words, when it is not.</summary>
    public string? ReversibilityNote { get; init; }

    /// <summary>The action button's label: a verb and an object ("Remove 14 leftovers"), never "OK" or "Yes".</summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>The tick box a high-risk action needs before its button works.</summary>
    public string Acknowledgement => Reversibility == Reversibility.Irreversible
        ? "I understand this cannot be undone from AxePower."
        : "I understand what will change.";

    /// <summary>High risk requires the tick box; the safe default (Cancel) is always the default button.</summary>
    public bool RequiresAcknowledgement => Risk == RiskLevel.High;

    public string RestartText => Restart switch
    {
        RestartNeed.SignOut => "You need to sign out of Windows and back in for it to take full effect.",
        RestartNeed.Restart => "You need to restart the PC for it to take full effect.",
        RestartNeed.RestartApp => "AxePower restarts to finish.",
        _ => "No restart or sign-out needed.",
    };

    public string RiskText => Risk switch
    {
        RiskLevel.High => "High risk",
        RiskLevel.Medium => "Medium risk",
        _ => "Low risk",
    };

    public string AdministratorText => RequiresAdministrator ? "Needs administrator rights." : "No administrator rights needed.";

    public string ReversibilityText => Reversibility switch
    {
        Reversibility.FullyReversible => "Fully reversible: it can be undone exactly.",
        Reversibility.PartiallyReversible => "Partly reversible: " + (ReversibilityNote ?? "some of it cannot be undone."),
        _ => "Not reversible: " + (ReversibilityNote ?? "it cannot be undone from AxePower."),
    };

    /// <summary>A confirmation for a planned operation: the action, affected targets, admin need and reversibility come from the plan.</summary>
    public static ActionConfirmation ForOperation(OperationRecord plan, RiskLevel risk, string backup, string confirmLabel, RestartNeed restart = RestartNeed.None) => new()
    {
        Action = plan.Action,
        Source = plan.SourceFeature,
        Affected = plan.Steps.Where(s => s.Status != StepStatus.Skipped).Select(s => s.Description).ToList(),
        RequiresAdministrator = plan.RequiresAdministrator,
        Risk = risk,
        Backup = backup,
        Restart = restart,
        Reversibility = plan.Reversibility,
        ReversibilityNote = plan.Steps.Select(s => s.ReversibilityNote).FirstOrDefault(n => n is not null),
        ConfirmLabel = confirmLabel,
    };

    /// <summary>Words a confirmation must never use: generic questions and pressure.</summary>
    public static readonly IReadOnlyList<string> ForbiddenWords = new[]
    {
        "are you sure", "ok", "yes", "hurry", "now or never", "last chance", "act now", "don't miss", "recommended!", "immediately",
    };
}
