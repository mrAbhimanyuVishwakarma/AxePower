using System;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Operations;
using Microsoft.Win32;
using Xunit;

namespace AxePower.Tests.Operations;

/// <summary>The two features on the framework: System Optimizer tweaks (low risk) and Game Mode services (high risk).</summary>
public class TweakAndServiceOperationTests : IDisposable
{
    private readonly OperationTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private TweakService Tweaks() => new(_kit.Log, _kit.Runner, _kit.Root);

    // -- Tweaks -------------------------------------------------------------------------

    public static TheoryData<string> AllTweakIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in new TweakService(new LogService()).GetAllTweaks().Select(t => t.Id)) data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTweakIds))]
    public async Task EveryTweak_AppliesAndRevertsToExactlyWhatWasThere(string tweakId)
    {
        var tweaks = Tweaks();
        // Something unrelated in the same keys must survive both ways.
        using (var mouse = _kit.Root(RegistryHive.CurrentUser).CreateSubKey(@"Control Panel\Mouse")) mouse.SetValue("DoubleClickSpeed", "500");
        var before = _kit.Dump();

        var applied = await tweaks.ExecuteAsync(tweaks.PlanApply(tweakId));
        Assert.Equal(OperationStatus.Succeeded, applied.Status);
        Assert.True(tweaks.GetAllTweaks().Single(t => t.Id == tweakId).IsApplied);
        Assert.NotEqual(before, _kit.Dump());

        var revert = tweaks.PlanRevert(tweakId);
        Assert.Equal(applied.Id, revert.RollbackOf); // a real rollback, not defaults
        var reverted = await tweaks.ExecuteAsync(revert);

        Assert.Equal(OperationStatus.Succeeded, reverted.Status);
        Assert.Equal(before, _kit.Dump());
        Assert.False(tweaks.GetAllTweaks().Single(t => t.Id == tweakId).IsApplied);
    }

    [Fact]
    public async Task Reverting_RestoresTheUsersOwnValue_NotAWindowsDefault()
    {
        // Before Prompt 24, revert wrote "1" / "6" / "10" whatever was there before.
        using (var mouse = _kit.Root(RegistryHive.CurrentUser).CreateSubKey(@"Control Panel\Mouse"))
        {
            mouse.SetValue("MouseSpeed", "2");
            mouse.SetValue("MouseThreshold1", "4");
            mouse.SetValue("MouseThreshold2", "12");
        }
        var tweaks = Tweaks();

        await tweaks.ExecuteAsync(tweaks.PlanApply("mouse-accel-disable"));
        await tweaks.ExecuteAsync(tweaks.PlanRevert("mouse-accel-disable"));

        using var after = _kit.Root(RegistryHive.CurrentUser).OpenSubKey(@"Control Panel\Mouse")!;
        Assert.Equal("2", after.GetValue("MouseSpeed"));
        Assert.Equal("4", after.GetValue("MouseThreshold1"));
        Assert.Equal("12", after.GetValue("MouseThreshold2"));
    }

    [Fact]
    public void ATweakAppliedBeforeTheHistoryExisted_RevertsToDefaults_AndSaysSo()
    {
        using (var keys = _kit.Root(RegistryHive.CurrentUser).CreateSubKey(@"Control Panel\Accessibility\StickyKeys")) keys.SetValue("Flags", "506");
        var tweaks = Tweaks();

        var plan = tweaks.PlanRevert("sticky-keys-disable");

        Assert.Null(plan.RollbackOf);
        Assert.StartsWith("Restore Windows defaults", plan.Action);
        Assert.Contains("no record of what was set before", plan.Notice);
        Assert.Equal("\"510\" (String)", plan.Steps.Single().AfterText);
    }

    [Fact]
    public async Task TheClassicMenuKey_IsRemovedWhole_OnRollback()
    {
        var tweaks = Tweaks();
        var before = _kit.Dump();

        var applied = await tweaks.ExecuteAsync(tweaks.PlanApply("classic-context-menu"));
        Assert.Contains("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", _kit.Dump());
        await tweaks.ExecuteAsync(tweaks.PlanRevert("classic-context-menu"));

        Assert.Equal(before, _kit.Dump()); // not even an empty CLSID or Classes key is left behind
        Assert.Contains(applied.Steps, s => s.Kind == RegistryChangeHandler.KeyKind);
    }

    [Fact]
    public void TweaksThatTouchHklm_SayTheyNeedAdministratorRights()
    {
        var tweaks = Tweaks();

        Assert.True(tweaks.PlanApply("telemetry-disable").RequiresAdministrator);
        Assert.False(tweaks.PlanApply("sticky-keys-disable").RequiresAdministrator);
        Assert.All(tweaks.GetAllTweaks(), t => Assert.Equal(Reversibility.FullyReversible, tweaks.PlanApply(t.Id).Reversibility));
    }

    [Fact]
    public void TweakService_NoLongerWritesTheRegistryItself()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "AxePower.Core", "Services", "TweakService.cs"));
        // Every write goes through RegistryChangeHandler (requests), never straight to a key.
        Assert.DoesNotMatch(@"(?<!RegistryChangeHandler)\.(SetValue|DeleteValue)\(", source);
        Assert.DoesNotContain("DeleteSubKeyTree(", source);
        Assert.DoesNotContain("CreateSubKey(", source);
    }

    // -- Game Mode services -----------------------------------------------------------

    private SystemServiceOptimizer Services() => new(_kit.Log, _kit.Runner);

    [Fact]
    public void PausingServices_IsPreviewedWithTheirStateNow_AndMarkedPartiallyReversible()
    {
        _kit.Services.Installed["WSearch"] = new ServiceSnapshot(ServiceStart.AutomaticDelayed, Running: true);
        _kit.Services.Installed["Spooler"] = new ServiceSnapshot(ServiceStart.Automatic, Running: false);

        var plan = Services().PlanPause(new[] { "WSearch", "Spooler", "NotOnTheList" });

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("Service WSearch: Automatic (delayed start), running → Manual, stopped", plan.Steps[0].Description);
        Assert.Equal(Reversibility.PartiallyReversible, plan.Reversibility);
        Assert.Contains("whatever it was doing when it was stopped is not restored", plan.Steps[0].ReversibilityNote);
        Assert.True(plan.RequiresAdministrator);
        Assert.Empty(_kit.Services.Calls); // a preview touches nothing
    }

    [Fact]
    public async Task PausedServices_RollBackToTheirExactStartupTypeAndState()
    {
        _kit.Services.Installed["WSearch"] = new ServiceSnapshot(ServiceStart.AutomaticDelayed, Running: true);
        _kit.Services.Installed["Spooler"] = new ServiceSnapshot(ServiceStart.Automatic, Running: false);
        var services = Services();

        var paused = await services.ExecuteAsync(services.PlanPause(new[] { "WSearch", "Spooler" }));
        Assert.Equal(OperationStatus.Succeeded, paused.Status);
        Assert.Equal(new ServiceSnapshot(ServiceStart.Manual, false), _kit.Services.Installed["WSearch"]);

        var rolledBack = await _kit.Runner.ExecuteAsync(_kit.Runner.PlanRollback(paused), FailurePolicy.ContinueWithOthers);

        Assert.Equal(OperationStatus.Succeeded, rolledBack.Status);
        Assert.Equal(new ServiceSnapshot(ServiceStart.AutomaticDelayed, true), _kit.Services.Installed["WSearch"]);
        Assert.Equal(new ServiceSnapshot(ServiceStart.Automatic, false), _kit.Services.Installed["Spooler"]);
        Assert.NotNull(rolledBack.Notice); // a partial rollback says what it cannot undo
    }

    [Fact]
    public async Task OneServiceFailing_DoesNotStopTheOthers_AndThePartCanStillBeRolledBack()
    {
        _kit.Services.Installed["WSearch"] = new ServiceSnapshot(ServiceStart.Automatic, Running: true);
        _kit.Services.Installed["WaaSMedicSvc"] = new ServiceSnapshot(ServiceStart.Manual, Running: true);
        _kit.Services.RefuseToStop.Add("WaaSMedicSvc"); // protected by Windows
        var services = Services();

        var result = await services.ExecuteAsync(services.PlanPause(new[] { "WSearch", "WaaSMedicSvc" }));

        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
        Assert.Equal(StepStatus.Succeeded, result.Steps[0].Status);
        Assert.Equal(StepStatus.Failed, result.Steps[1].Status);
        Assert.True(OperationRunner.CanRollBack(result));
        await _kit.Runner.ExecuteAsync(_kit.Runner.PlanRollback(result), FailurePolicy.ContinueWithOthers);
        Assert.Equal(new ServiceSnapshot(ServiceStart.Automatic, true), _kit.Services.Installed["WSearch"]);
    }

    [Fact]
    public async Task AServiceThatIsNotInstalled_FailsItsStep_WithoutGuessing()
    {
        var services = Services();

        var result = await services.ExecuteAsync(services.PlanPause(new[] { "Spooler" }));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal("not installed", result.Steps[0].BeforeText);
        Assert.Contains("not installed", result.Steps[0].Message);
    }

    [Fact]
    public void GameModeServices_NoLongerLaunchScExe()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "AxePower.Core", "Services", "SystemServiceOptimizer.cs"));
        Assert.DoesNotContain("ProcessStartInfo(", source);
        Assert.DoesNotContain("Process.Start", source);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new System.IO.DirectoryNotFoundException("AxePower.sln not found");
    }
}
