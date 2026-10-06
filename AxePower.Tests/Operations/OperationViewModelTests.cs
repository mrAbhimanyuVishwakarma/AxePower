using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Operations;
using AxePower.Core.Services.Uninstaller;
using Microsoft.Win32;
using Xunit;

namespace AxePower.Tests.Operations;

/// <summary>The UI flow: nothing changes without approval, and the words never promise more than the framework can do.</summary>
public class OperationViewModelTests : IDisposable
{
    private readonly OperationTestKit _kit = new();
    private readonly FakePrompts _prompts = new();

    public void Dispose() => _kit.Dispose();

    private (SystemOptimizerViewModel Vm, TweakService Tweaks) Optimizer()
    {
        var tweaks = new TweakService(_kit.Log, _kit.Runner, _kit.Root);
        var vm = new SystemOptimizerViewModel(tweaks, new SystemRestoreService(_kit.Log), new NotificationService(), _kit.Log, _prompts);
        return (vm, tweaks);
    }

    [Fact]
    public async Task ATweak_IsPreviewedFirst_AndDeclining_ChangesNothing()
    {
        var (vm, _) = Optimizer();
        var card = vm.AllTweaks.Single(t => t.Id == "sticky-keys-disable");
        var before = _kit.Dump();
        _prompts.Answer = false;

        await card.ToggleAsync();

        var shown = Assert.Single(_prompts.Shown);
        Assert.StartsWith("Apply tweak: ", shown.Single().Action);
        Assert.Equal(before, _kit.Dump());
        Assert.False(card.IsApplied);
        Assert.Empty(_kit.Runner.Journal.All());
    }

    [Fact]
    public async Task ATweak_Approved_IsApplied_ThenRevertingShowsARollback()
    {
        var (vm, _) = Optimizer();
        var card = vm.AllTweaks.Single(t => t.Id == "sticky-keys-disable");
        _prompts.Answer = true;

        await card.ToggleAsync();
        Assert.True(card.IsApplied);

        await card.ToggleAsync();

        Assert.False(card.IsApplied);
        Assert.NotNull(_prompts.Shown[1].Single().RollbackOf);
        Assert.Equal(2, _kit.Runner.Journal.All().Count);
    }

    [Fact]
    public async Task TheHistory_ListsChanges_AndRollsBackOnlyAfterApproval()
    {
        var tweaks = new TweakService(_kit.Log, _kit.Runner, _kit.Root);
        var before = _kit.Dump();
        await tweaks.ExecuteAsync(tweaks.PlanApply("gamedvr-disable"));
        var history = new ChangeHistoryViewModel(_kit.Runner, _prompts);
        var entry = Assert.Single(history.Operations);
        Assert.True(entry.CanRollBack);
        Assert.Equal("Fully reversible", entry.ReversibilityLabel);

        _prompts.Answer = false;
        await history.RollBackCommand.ExecuteAsync(entry);
        Assert.NotEqual(before, _kit.Dump());

        _prompts.Answer = true;
        await history.RollBackCommand.ExecuteAsync(history.Operations.Single());

        Assert.Equal(before, _kit.Dump());
        Assert.Equal(2, history.Operations.Count);
        var original = history.Operations.Single(o => !o.Record.IsRollback);
        Assert.False(original.CanRollBack);
        Assert.Equal("Already rolled back.", original.WhyNotRollBack);
        Assert.NotNull(original.RolledBackText);
    }

    [Fact]
    public void TheWords_MatchTheLevel_AndNeverPromiseAFullUndoForAPartialOne()
    {
        Assert.Equal("Fully reversible", ReversibilityText.Label(Reversibility.FullyReversible));
        Assert.Equal("Partially reversible", ReversibilityText.Label(Reversibility.PartiallyReversible));
        Assert.Equal("Irreversible", ReversibilityText.Label(Reversibility.Irreversible));
        Assert.Contains("some effects cannot be undone", ReversibilityText.Explanation(Reversibility.PartiallyReversible));
        Assert.Equal("Cannot be undone from AxePower.", ReversibilityText.Explanation(Reversibility.Irreversible));
        Assert.DoesNotContain("exactly", ReversibilityText.Explanation(Reversibility.PartiallyReversible));
    }

    [Fact]
    public void ThePreview_ExplainsNothingHappensUntilApproval_AndDisablesApproveWhenThereIsNothingToDo()
    {
        using (var keys = _kit.Root(RegistryHive.CurrentUser).CreateSubKey(@"Control Panel\Accessibility\StickyKeys")) keys.SetValue("Flags", "506");
        var tweaks = new TweakService(_kit.Log, _kit.Runner, _kit.Root);

        var nothing = new OperationPreviewViewModel(new[] { tweaks.PlanApply("sticky-keys-disable") });
        var something = new OperationPreviewViewModel(new[] { tweaks.PlanApply("gamedvr-disable") });

        Assert.False(nothing.HasChanges);
        Assert.Equal("Nothing needs changing.", nothing.Summary);
        Assert.True(something.HasChanges);
        Assert.Contains("Nothing happens until you click Apply", something.Summary);
        Assert.Equal("Needs administrator rights", something.Operations.Single().AdminText);
    }

    [Fact]
    public void Outcomes_ReportWhatReallyHappened()
    {
        var partly = new OperationRecord
        {
            Action = "Stop 2 services",
            Status = OperationStatus.PartiallySucceeded,
            Steps = { new OperationStep { Status = StepStatus.Succeeded }, new OperationStep { Status = StepStatus.Failed } },
        };

        Assert.Equal("Stop 2 services: 1 change(s) done, 1 failed. See Settings › Change history.", ReversibilityText.Outcome(partly));
    }

    private sealed class FakePrompts : IOperationPrompts
    {
        public bool Answer { get; set; }
        public List<IReadOnlyList<OperationRecord>> Shown { get; } = new();

        public bool Approve(IReadOnlyList<OperationRecord> plans)
        {
            Shown.Add(plans);
            return Answer;
        }
    }
}
