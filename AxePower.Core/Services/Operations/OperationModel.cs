using System.Text.Json.Serialization;

namespace AxePower.Core.Services.Operations;

/// <summary>
/// How completely a change can be undone. Never claim more than Windows (or a third-party
/// program) can guarantee: when in doubt, the lower level.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Reversibility
{
    /// <summary>Cannot be undone from AxePower.</summary>
    Irreversible = 0,

    /// <summary>Settings come back exactly, but some effect of the change cannot be undone.</summary>
    PartiallyReversible = 1,

    /// <summary>Restored exactly to what it was before.</summary>
    FullyReversible = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepStatus
{
    Planned,
    Succeeded,
    Failed,

    /// <summary>Not attempted: nothing to change, an earlier step failed, or rollback found it changed since.</summary>
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationStatus
{
    /// <summary>Previewed, not approved yet.</summary>
    Planned,
    Succeeded,

    /// <summary>Some steps worked and some did not.</summary>
    PartiallySucceeded,
    Failed,

    /// <summary>A step failed and the steps already done were undone automatically.</summary>
    FailedAndUndone,

    /// <summary>Refused before any change, for example because administrator rights were missing.</summary>
    Refused,
}

/// <summary>
/// A state as a change handler reads and writes it, kept as plain strings so it survives in the
/// journal and can be restored after a restart. Equality is by content.
/// </summary>
public sealed class ChangeState
{
    public ChangeState() { }

    public ChangeState(IReadOnlyDictionary<string, string> values) => Values = new SortedDictionary<string, string>(values.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);

    [JsonPropertyName("values")]
    public SortedDictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public string? this[string key] => Values.TryGetValue(key, out var value) ? value : null;

    public bool SameAs(ChangeState? other) =>
        other is not null && Values.Count == other.Values.Count && Values.All(p => other.Values.TryGetValue(p.Key, out var v) && v == p.Value);

    public static ChangeState Of(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value));
}

/// <summary>One change a feature asks for: put <see cref="Target"/> into <see cref="Desired"/>.</summary>
public sealed record ChangeRequest(string Kind, string Target, ChangeState Desired);

/// <summary>One step of an operation, from plan to execution to (possibly) rollback.</summary>
public sealed class OperationStep
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("target")] public string Target { get; set; } = string.Empty;

    /// <summary>A sentence for people, e.g. "Set MouseSpeed to "0" (was "1")".</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;

    [JsonPropertyName("requiresAdmin")] public bool RequiresAdministrator { get; set; }
    [JsonPropertyName("reversibility")] public Reversibility Reversibility { get; set; }

    /// <summary>Why it is not fully reversible, in plain words. Null when it is.</summary>
    [JsonPropertyName("reversibilityNote")] public string? ReversibilityNote { get; set; }

    /// <summary>The state found before the change: the snapshot rollback restores. Refreshed right before execution.</summary>
    [JsonPropertyName("before")] public ChangeState Before { get; set; } = new();

    [JsonPropertyName("after")] public ChangeState After { get; set; } = new();
    [JsonPropertyName("beforeText")] public string BeforeText { get; set; } = string.Empty;
    [JsonPropertyName("afterText")] public string AfterText { get; set; } = string.Empty;

    /// <summary>For rollback steps: the state the target must still be in, or the step is skipped.</summary>
    [JsonPropertyName("expected")] public ChangeState? ExpectedCurrent { get; set; }

    [JsonPropertyName("status")] public StepStatus Status { get; set; } = StepStatus.Planned;
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("executedUtc")] public DateTime? ExecutedUtc { get; set; }

    /// <summary>Set when a failed operation undid this step automatically.</summary>
    [JsonPropertyName("undoneAutomatically")] public bool UndoneAutomatically { get; set; }
}

/// <summary>
/// The record of one change to Windows: what was asked, by which feature, what exactly was planned,
/// what each step did, how reversible it is and whether it was rolled back. Kept in the journal.
/// </summary>
public sealed class OperationRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string SourceFeature { get; set; } = string.Empty;

    /// <summary>What the operation is about, e.g. "tweak:mouse-accel-disable", so a feature can find its last change.</summary>
    [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;

    /// <summary>A short explanation shown above the steps in the preview, e.g. why defaults are used.</summary>
    [JsonPropertyName("notice")] public string? Notice { get; set; }

    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = string.Empty;
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    [JsonPropertyName("approvedUtc")] public DateTime? ApprovedUtc { get; set; }
    [JsonPropertyName("completedUtc")] public DateTime? CompletedUtc { get; set; }
    [JsonPropertyName("status")] public OperationStatus Status { get; set; } = OperationStatus.Planned;
    [JsonPropertyName("statusMessage")] public string? StatusMessage { get; set; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
    [JsonPropertyName("steps")] public List<OperationStep> Steps { get; set; } = new();

    /// <summary>Set on a rollback operation: the operation it undoes.</summary>
    [JsonPropertyName("rollbackOf")] public string? RollbackOf { get; set; }

    /// <summary>Set on an operation that was rolled back: the rollback operation, and when.</summary>
    [JsonPropertyName("rolledBackBy")] public string? RolledBackBy { get; set; }
    [JsonPropertyName("rolledBackUtc")] public DateTime? RolledBackUtc { get; set; }

    [JsonIgnore] public bool RequiresAdministrator => Steps.Any(s => s.RequiresAdministrator && s.Status != StepStatus.Skipped);

    /// <summary>The weakest level among the steps that change something: a chain is only as reversible as its least reversible link.</summary>
    [JsonIgnore]
    public Reversibility Reversibility
    {
        get
        {
            var changing = Steps.Where(s => s.Status != StepStatus.Skipped).ToList();
            return changing.Count == 0 ? Reversibility.FullyReversible : changing.Min(s => s.Reversibility);
        }
    }

    [JsonIgnore] public bool IsRollback => RollbackOf is not null;

    [JsonIgnore] public bool WasRolledBack => RolledBackBy is not null;
}
