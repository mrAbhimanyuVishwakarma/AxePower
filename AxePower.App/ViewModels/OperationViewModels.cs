using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AxePower.App.Services;
using AxePower.Core.Services;
using AxePower.Core.Services.Operations;

namespace AxePower.App.ViewModels;

/// <summary>
/// The words AxePower uses for each reversibility level. Kept in one place so the preview, the
/// history and the result messages never promise more than the framework can do.
/// </summary>
public static class ReversibilityText
{
    public static string Label(Reversibility level) => level switch
    {
        Reversibility.FullyReversible => "Fully reversible",
        Reversibility.PartiallyReversible => "Partially reversible",
        _ => "Irreversible",
    };

    public static string Explanation(Reversibility level) => level switch
    {
        Reversibility.FullyReversible => "Can be undone exactly from Settings › Change history.",
        Reversibility.PartiallyReversible => "Settings can be put back from Settings › Change history, but some effects cannot be undone.",
        _ => "Cannot be undone from AxePower.",
    };

    /// <summary>"Fully", "Partially" or "Irreversible": the key the badge style colours by.</summary>
    public static string Key(Reversibility level) => level switch
    {
        Reversibility.FullyReversible => "Fully",
        Reversibility.PartiallyReversible => "Partially",
        _ => "Irreversible",
    };

    public static string Status(OperationRecord record) => record.Status switch
    {
        OperationStatus.Planned => "Not applied yet",
        OperationStatus.Succeeded => record.IsRollback ? "Rolled back" : "Done",
        OperationStatus.PartiallySucceeded => "Partly done",
        OperationStatus.Failed => "Failed; nothing changed",
        OperationStatus.FailedAndUndone => "Failed; undone automatically",
        _ => "Not started",
    };

    /// <summary>One sentence for a notification after an operation ran.</summary>
    public static string Outcome(OperationRecord record)
    {
        int done = record.Steps.Count(s => s.Status == StepStatus.Succeeded);
        int failed = record.Steps.Count(s => s.Status == StepStatus.Failed);
        return record.Status switch
        {
            OperationStatus.Succeeded when done == 0 => $"{record.Action}: nothing needed changing.",
            OperationStatus.Succeeded => $"{record.Action}: done ({done} change(s)).",
            OperationStatus.PartiallySucceeded => $"{record.Action}: {done} change(s) done, {failed} failed. See Settings › Change history.",
            OperationStatus.FailedAndUndone => $"{record.Action}: failed, and what was done was undone. See Settings › Change history.",
            OperationStatus.Refused => $"{record.Action}: {record.StatusMessage}",
            _ => $"{record.Action}: failed; nothing was changed. See Settings › Change history.",
        };
    }
}

public sealed class OperationStepView
{
    public OperationStepView(OperationStep step)
    {
        Description = step.Description;
        Status = step.Status switch
        {
            StepStatus.Planned => string.Empty,
            StepStatus.Succeeded => step.UndoneAutomatically ? "Done, then undone" : "Done",
            StepStatus.Failed => "Failed",
            _ => "Skipped",
        };
        Message = step.Message;
        ReversibilityKey = ReversibilityText.Key(step.Reversibility);
        ReversibilityLabel = ReversibilityText.Label(step.Reversibility);
        Note = step.ReversibilityNote;
        IsSkipped = step.Status == StepStatus.Skipped;
        RequiresAdministrator = step.RequiresAdministrator;
    }

    public string Description { get; }
    public string Status { get; }
    public string? Message { get; }
    public string ReversibilityKey { get; }
    public string ReversibilityLabel { get; }
    public string? Note { get; }
    public bool IsSkipped { get; }
    public bool RequiresAdministrator { get; }
}

/// <summary>One operation as the preview and the history show it.</summary>
public partial class OperationView : ObservableObject
{
    public OperationView(OperationRecord record)
    {
        Record = record;
        Steps = record.Steps.Select(s => new OperationStepView(s)).ToList();
    }

    public OperationRecord Record { get; }
    public IReadOnlyList<OperationStepView> Steps { get; }

    public string Action => Record.Action;
    public string Source => Record.SourceFeature;
    public string? Notice => Record.Notice;
    public string When => (Record.CompletedUtc ?? Record.CreatedUtc).ToLocalTime().ToString("d MMM yyyy, HH:mm");
    public string Status => ReversibilityText.Status(Record);
    public string? StatusMessage => Record.StatusMessage;
    public string Version => $"AxePower {Record.AppVersion}";
    public string AdminText => Record.RequiresAdministrator ? "Needs administrator rights" : "No administrator rights needed";
    public string ReversibilityKey => ReversibilityText.Key(Record.Reversibility);
    public string ReversibilityLabel => ReversibilityText.Label(Record.Reversibility);
    public string ReversibilityExplanation => ReversibilityText.Explanation(Record.Reversibility);
    public int ChangeCount => Record.Steps.Count(s => s.Status != StepStatus.Skipped);
    public string ChangeCountText => ChangeCount == 1 ? "1 change" : $"{ChangeCount} changes";
    public bool CanRollBack => OperationRunner.CanRollBack(Record);
    public string? WhyNotRollBack => OperationRunner.WhyNotRollBack(Record);
    public string? RolledBackText => Record.RolledBackUtc is { } at ? $"Rolled back {at.ToLocalTime():d MMM yyyy, HH:mm}" : null;

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>The preview shown before anything is changed. Approve runs it; Cancel changes nothing.</summary>
public sealed class OperationPreviewViewModel
{
    public OperationPreviewViewModel(IReadOnlyList<OperationRecord> plans)
    {
        Operations = plans.Select(p => new OperationView(p)).ToList();
        bool rollback = plans.All(p => p.IsRollback);
        Title = rollback ? "Review the rollback" : "Review the changes";
        ApproveText = rollback ? "Roll back" : plans.Count > 1 ? $"Apply {plans.Count} changes" : "Apply";
        int changes = Operations.Sum(o => o.ChangeCount);
        Summary = changes == 0
            ? "Nothing needs changing."
            : $"{changes} change(s) to Windows. Nothing happens until you click {ApproveText}.";
        HasChanges = changes > 0;
    }

    public IReadOnlyList<OperationView> Operations { get; }
    public string Title { get; }
    public string ApproveText { get; }
    public string Summary { get; }
    public bool HasChanges { get; }
}

/// <summary>Settings › Change history: what AxePower changed, and rolling back what can be.</summary>
public partial class ChangeHistoryViewModel : ObservableObject
{
    private readonly OperationRunner _runner;
    private readonly IOperationPrompts _prompts;
    private readonly NotificationService? _notifications;

    public ChangeHistoryViewModel(OperationRunner runner, IOperationPrompts prompts, NotificationService? notifications = null)
    {
        _runner = runner;
        _prompts = prompts;
        _notifications = notifications;
        Refresh();
    }

    public ObservableCollection<OperationView> Operations { get; } = new();

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    public void Refresh()
    {
        Operations.Clear();
        foreach (var record in _runner.Journal.All()) Operations.Add(new OperationView(record));
        SummaryText = Operations.Count == 0
            ? "No changes recorded yet."
            : $"{Operations.Count} change(s) recorded, newest first. The newest {OperationJournal.MaxRecords} are kept.";
    }

    [RelayCommand]
    private async Task RollBackAsync(OperationView? operation)
    {
        if (operation is null || IsBusy) return;
        if (!operation.CanRollBack)
        {
            StatusText = operation.WhyNotRollBack ?? "This change cannot be rolled back.";
            return;
        }

        var plan = _runner.PlanRollback(operation.Record);
        if (!_prompts.Approve(new[] { plan })) return;

        IsBusy = true;
        try
        {
            var result = await _runner.ExecuteAsync(plan, FailurePolicy.ContinueWithOthers);
            StatusText = ReversibilityText.Outcome(result);
            _notifications?.Show(StatusText);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}
