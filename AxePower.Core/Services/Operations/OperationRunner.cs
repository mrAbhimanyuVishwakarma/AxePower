using System.ComponentModel;
using System.Reflection;
using AxePower.Core.Services.Diagnostics;

namespace AxePower.Core.Services.Operations;

/// <summary>What to do when a step fails.</summary>
public enum FailurePolicy
{
    /// <summary>Stop, and undo the steps already done (for changes that only make sense together).</summary>
    UndoCompleted,

    /// <summary>Carry on with the remaining steps and report each one (for independent changes).</summary>
    ContinueWithOthers,
}

/// <summary>
/// The one way AxePower changes Windows state. <see cref="Plan"/> reads the current state and changes
/// nothing, so it can be shown for approval. <see cref="ExecuteAsync"/> takes a fresh snapshot right
/// before each step, applies it, checks the result and records everything in the journal.
/// <see cref="PlanRollback"/> turns a recorded operation into its inverse, which is previewed and
/// executed like any other operation and recorded as well.
/// </summary>
public sealed class OperationRunner
{
    private readonly Dictionary<string, IChangeHandler> _handlers;
    private readonly OperationJournal _journal;
    private readonly LogService _log;
    private readonly Func<bool> _isElevated;
    private readonly Func<DateTime> _utcNow;

    public OperationRunner(
        OperationJournal journal,
        LogService log,
        IEnumerable<IChangeHandler> handlers,
        Func<bool>? isElevated = null,
        Func<DateTime>? utcNow = null)
    {
        _journal = journal;
        _log = log;
        _handlers = handlers.SelectMany(h => h.Kinds.Select(k => (k, h))).ToDictionary(p => p.k, p => p.h, StringComparer.Ordinal);
        _isElevated = isElevated ?? (() => Environment.IsPrivilegedProcess);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public OperationJournal Journal => _journal;

    public static string AppVersion { get; } =
        (typeof(OperationRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(OperationRunner).Assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];

    /// <summary>Reads the current state of every target and describes the change. Changes nothing.</summary>
    public OperationRecord Plan(string action, string sourceFeature, string subject, IEnumerable<ChangeRequest> requests, string? notice = null)
    {
        var record = new OperationRecord
        {
            Action = action,
            SourceFeature = sourceFeature,
            Subject = subject,
            Notice = notice,
            AppVersion = AppVersion,
            CreatedUtc = _utcNow(),
        };
        foreach (var request in requests)
        {
            var handler = HandlerFor(request.Kind);
            var (level, note) = handler.ReversibilityOf(request.Kind, request.Target);
            var step = new OperationStep
            {
                Kind = request.Kind,
                Target = request.Target,
                RequiresAdministrator = handler.RequiresAdministrator(request.Kind, request.Target),
                Reversibility = level,
                ReversibilityNote = note,
                After = request.Desired,
                AfterText = handler.Describe(request.Kind, request.Target, request.Desired),
            };
            Snapshot(step, handler);
            if (handler.Same(step.Kind, step.Before, step.After))
            {
                step.Status = StepStatus.Skipped;
                step.Message = "Already set; nothing to change.";
            }
            record.Steps.Add(step);
        }
        return record;
    }

    /// <summary>Runs an approved plan. Never throws for a failed step: the record says what happened.</summary>
    public Task<OperationRecord> ExecuteAsync(OperationRecord plan, FailurePolicy policy) => Task.Run(() => Execute(plan, policy));

    /// <summary>
    /// The inverse of a recorded operation, ready for preview. Each step that was applied gets a step
    /// putting back its snapshot, in reverse order; a target changed by something else since is left
    /// alone (skipped), and steps that cannot be undone are listed as such.
    /// </summary>
    public OperationRecord PlanRollback(OperationRecord original)
    {
        if (!CanRollBack(original)) throw new InvalidOperationException(WhyNotRollBack(original) ?? "This change cannot be rolled back.");

        var rollback = new OperationRecord
        {
            Action = $"Roll back: {original.Action}",
            SourceFeature = original.SourceFeature,
            Subject = original.Subject,
            AppVersion = AppVersion,
            CreatedUtc = _utcNow(),
            RollbackOf = original.Id,
            Notice = original.Reversibility == Reversibility.PartiallyReversible
                ? "Settings are put back as they were. Some effects of the original change cannot be undone (see each step)."
                : null,
        };

        foreach (var done in Enumerable.Reverse(original.Steps).Where(Touched))
        {
            var handler = HandlerFor(done.Kind);
            bool failedPartWay = done.Status == StepStatus.Failed;
            var step = new OperationStep
            {
                Kind = done.Kind,
                Target = done.Target,
                RequiresAdministrator = done.RequiresAdministrator,
                Reversibility = done.Reversibility,
                ReversibilityNote = done.ReversibilityNote,
                After = done.Before,
                AfterText = done.BeforeText,
                // A step that failed part-way has no known end state to check against.
                ExpectedCurrent = failedPartWay ? null : done.After,
            };
            Snapshot(step, handler);
            if (failedPartWay)
            {
                if (handler.Same(step.Kind, step.Before, done.Before)) continue; // it failed before changing anything
                step.Message = "This step failed part-way; what was there before is put back.";
            }
            if (done.Reversibility == Reversibility.Irreversible)
            {
                step.Status = StepStatus.Skipped;
                step.Message = "This step cannot be undone.";
            }
            else if (!failedPartWay && !handler.Same(step.Kind, step.Before, done.After))
            {
                step.Status = StepStatus.Skipped;
                step.Message = "Changed since by something else; left as it is.";
            }
            rollback.Steps.Add(step);
        }
        return rollback;
    }

    public static bool CanRollBack(OperationRecord record) => WhyNotRollBack(record) is null;

    /// <summary>Null when <paramref name="record"/> can be rolled back; otherwise the reason, for the UI.</summary>
    public static string? WhyNotRollBack(OperationRecord record)
    {
        if (record.IsRollback) return "This is a rollback. Make the change again instead.";
        if (record.WasRolledBack) return "Already rolled back.";
        if (record.Status == OperationStatus.FailedAndUndone) return "It was undone automatically when it failed.";
        if (record.Status is OperationStatus.Planned or OperationStatus.Refused) return "Nothing was changed.";
        var applied = record.Steps.Where(Touched).ToList();
        if (applied.Count == 0) return "Nothing was changed.";
        if (applied.All(s => s.Reversibility == Reversibility.Irreversible)) return "This change cannot be undone from AxePower.";
        return null;
    }

    /// <summary>A step that may have changed something: it succeeded, or it failed (possibly part-way), and was not undone already.</summary>
    private static bool Touched(OperationStep step) =>
        step.Status is StepStatus.Succeeded or StepStatus.Failed && !step.UndoneAutomatically;

    // -- Execution ----------------------------------------------------------------

    private OperationRecord Execute(OperationRecord plan, FailurePolicy policy)
    {
        using var correlation = LogService.BeginCorrelation();
        plan.CorrelationId = LogService.CurrentCorrelationId;
        plan.ApprovedUtc = _utcNow();

        if (plan.RequiresAdministrator && !_isElevated())
        {
            plan.Status = OperationStatus.Refused;
            plan.StatusMessage = "Administrator rights are needed and AxePower is not running as administrator. Nothing was changed.";
            foreach (var step in plan.Steps.Where(s => s.Status == StepStatus.Planned)) step.Status = StepStatus.Skipped;
            return Finish(plan, original: null);
        }

        var completed = new List<OperationStep>();
        bool failed = false;
        foreach (var step in plan.Steps)
        {
            if (step.Status == StepStatus.Skipped) continue;
            if (failed && policy == FailurePolicy.UndoCompleted)
            {
                step.Status = StepStatus.Skipped;
                step.Message = "Not attempted: an earlier step failed.";
                continue;
            }

            var handler = HandlerFor(step.Kind);
            try
            {
                var now = handler.Read(step.Kind, step.Target);
                if (step.ExpectedCurrent is not null && !handler.Same(step.Kind, now, step.ExpectedCurrent))
                {
                    step.Status = StepStatus.Skipped;
                    step.Message = "Changed since by something else; left as it is.";
                    continue;
                }
                if (!handler.Same(step.Kind, now, step.Before))
                {
                    step.Message = "Changed after the preview; the newer value was saved for rollback.";
                }
                step.Before = now;
                step.BeforeText = handler.Describe(step.Kind, step.Target, now);

                handler.Apply(step.Kind, step.Target, step.After);
                step.ExecutedUtc = _utcNow();

                var result = handler.Read(step.Kind, step.Target);
                if (handler.Same(step.Kind, result, step.After))
                {
                    step.Status = StepStatus.Succeeded;
                    completed.Add(step);
                }
                else
                {
                    step.Status = StepStatus.Failed;
                    step.Message = $"Windows reports {handler.Describe(step.Kind, step.Target, result)} afterwards.";
                    failed = true;
                    // It may have half-applied: undo it as well when undoing.
                    completed.Add(step);
                }
            }
            catch (Exception ex) when (IsStepFailure(ex))
            {
                step.ExecutedUtc = _utcNow();
                step.Status = StepStatus.Failed;
                step.Message = LogRedactor.Describe(ex);
                failed = true;
                // A step can fail half way (a service's start type changed, then stopping it failed):
                // include it when undoing. Putting back a value that never changed is harmless.
                completed.Add(step);
            }
        }

        if (failed && policy == FailurePolicy.UndoCompleted && completed.Count > 0)
        {
            UndoAutomatically(completed);
            plan.Status = OperationStatus.FailedAndUndone;
            plan.StatusMessage = "A step failed, so the steps already done were undone.";
        }
        else
        {
            int succeeded = plan.Steps.Count(s => s.Status == StepStatus.Succeeded);
            int changing = plan.Steps.Count(s => s.Status is StepStatus.Succeeded or StepStatus.Failed);
            plan.Status = changing == 0 ? OperationStatus.Succeeded
                : succeeded == changing ? OperationStatus.Succeeded
                : succeeded == 0 ? OperationStatus.Failed
                : OperationStatus.PartiallySucceeded;
        }

        return Finish(plan, plan.RollbackOf is { } id ? _journal.Find(id) : null);
    }

    private void UndoAutomatically(List<OperationStep> completed)
    {
        foreach (var step in Enumerable.Reverse(completed))
        {
            if (step.Reversibility == Reversibility.Irreversible) continue;
            var handler = HandlerFor(step.Kind);
            try
            {
                handler.Apply(step.Kind, step.Target, step.Before);
                step.UndoneAutomatically = handler.Same(step.Kind, handler.Read(step.Kind, step.Target), step.Before);
                if (!step.UndoneAutomatically) step.Message = (step.Message is null ? "" : step.Message + " ") + "Could not be undone automatically.";
            }
            catch (Exception ex) when (IsStepFailure(ex))
            {
                step.Message = (step.Message is null ? "" : step.Message + " ") + $"Could not be undone automatically: {LogRedactor.Describe(ex)}";
            }
        }
    }

    private OperationRecord Finish(OperationRecord record, OperationRecord? original)
    {
        record.CompletedUtc = _utcNow();
        try
        {
            if (original is not null && record.Status is OperationStatus.Succeeded or OperationStatus.PartiallySucceeded)
            {
                original.RolledBackBy = record.Id;
                original.RolledBackUtc = record.CompletedUtc;
                _journal.Save(original);
            }
            _journal.Save(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The change itself happened; say plainly that it cannot be undone from the history.
            record.StatusMessage = (record.StatusMessage is null ? "" : record.StatusMessage + " ")
                + "It could not be saved to the change history, so it cannot be rolled back from there.";
            _log.LogWarning("[Operations] Could not save a change to the history", ex);
        }

        var summary = $"{record.Action} ({record.SourceFeature}): {record.Status}, {record.Steps.Count(s => s.Status == StepStatus.Succeeded)} of {record.Steps.Count} step(s) done.";
        if (record.IsRollback)
            _log.Log(LogLevels.Info, "Operations", LogEvents.OperationRolledBack, summary);
        else if (record.Status is OperationStatus.Succeeded)
            _log.Log(LogLevels.Info, "Operations", LogEvents.OperationCompleted, summary);
        else
            _log.Log(LogLevels.Warning, "Operations", LogEvents.OperationFailed, summary + (record.StatusMessage is null ? "" : " " + record.StatusMessage));
        return record;
    }

    private void Snapshot(OperationStep step, IChangeHandler handler)
    {
        try
        {
            step.Before = handler.Read(step.Kind, step.Target);
            step.BeforeText = handler.Describe(step.Kind, step.Target, step.Before);
        }
        catch (Exception ex) when (IsStepFailure(ex))
        {
            step.Before = new ChangeState();
            step.BeforeText = "could not be read";
            step.Message = $"The current value could not be read: {LogRedactor.Describe(ex)}";
        }
        step.Description = $"{TargetName(step)}: {step.BeforeText} → {step.AfterText}";
    }

    private static string TargetName(OperationStep step) => step.Kind switch
    {
        ServiceChangeHandler.Kind => $"Service {step.Target}",
        RegistryChangeHandler.KeyKind => $"Registry key {step.Target}",
        _ => $"Registry value {step.Target.Replace("|", " › ", StringComparison.Ordinal)}",
    };

    private IChangeHandler HandlerFor(string kind) =>
        _handlers.TryGetValue(kind, out var handler) ? handler : throw new NotSupportedException($"No handler for '{kind}' changes.");

    private static bool IsStepFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or Win32Exception
            or InvalidOperationException or System.ServiceProcess.TimeoutException or ArgumentException or FormatException
            or NotSupportedException or OverflowException;
}
