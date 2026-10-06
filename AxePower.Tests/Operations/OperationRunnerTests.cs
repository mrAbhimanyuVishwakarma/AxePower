using System;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Services.Operations;
using Microsoft.Win32;
using Xunit;

namespace AxePower.Tests.Operations;

/// <summary>Preview, execution, history and rollback, on a scratch registry.</summary>
public class OperationRunnerTests : IDisposable
{
    private readonly OperationTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private const string Key = @"HKCU\Software\Test\Mouse";

    private void Seed(string name, object value, RegistryValueKind kind)
    {
        using var key = _kit.Root(RegistryHive.CurrentUser).CreateSubKey(@"Software\Test\Mouse");
        key.SetValue(name, value, kind);
    }

    private string? Read(string name)
    {
        using var key = _kit.Root(RegistryHive.CurrentUser).OpenSubKey(@"Software\Test\Mouse");
        return key?.GetValue(name)?.ToString();
    }

    [Fact]
    public void ThePreview_ShowsBeforeAndAfter_AndChangesNothing()
    {
        Seed("MouseSpeed", "2", RegistryValueKind.String);
        var before = _kit.Dump();

        var plan = _kit.Runner.Plan("Test", "Tests", "test:1", new[]
        {
            RegistryChangeHandler.SetString(Key, "MouseSpeed", "0"),
            RegistryChangeHandler.SetDword(Key, "Missing", 1),
        });

        Assert.Equal(before, _kit.Dump());
        Assert.Equal(OperationStatus.Planned, plan.Status);
        Assert.Equal("\"2\" (String)", plan.Steps[0].BeforeText);
        Assert.Equal("\"0\" (String)", plan.Steps[0].AfterText);
        Assert.Equal("not set", plan.Steps[1].BeforeText);
        Assert.Contains("→", plan.Steps[0].Description);
        Assert.Equal(Reversibility.FullyReversible, plan.Reversibility);
        Assert.False(plan.RequiresAdministrator);
        Assert.Empty(_kit.Runner.Journal.All()); // a preview is not history
    }

    [Fact]
    public void ARequestThatChangesNothing_IsSkipped()
    {
        Seed("MouseSpeed", "0", RegistryValueKind.String);

        var plan = _kit.Runner.Plan("Test", "Tests", "test:1", new[] { RegistryChangeHandler.SetString(Key, "MouseSpeed", "0") });

        Assert.Equal(StepStatus.Skipped, Assert.Single(plan.Steps).Status);
    }

    [Fact]
    public async Task Executing_RecordsStepsTimesVersionAndCorrelation()
    {
        Seed("MouseSpeed", "2", RegistryValueKind.String);
        var plan = _kit.Runner.Plan("Disable acceleration", "Tests › Mouse", "test:mouse", new[] { RegistryChangeHandler.SetString(Key, "MouseSpeed", "0") });

        var done = await _kit.Runner.ExecuteAsync(plan, FailurePolicy.UndoCompleted);

        Assert.Equal(OperationStatus.Succeeded, done.Status);
        Assert.Equal("0", Read("MouseSpeed"));
        var stored = Assert.Single(_kit.Runner.Journal.All());
        Assert.Equal("Disable acceleration", stored.Action);
        Assert.Equal("Tests › Mouse", stored.SourceFeature);
        Assert.Equal(_kit.Now, stored.CreatedUtc);
        Assert.NotNull(stored.ApprovedUtc);
        Assert.NotNull(stored.CompletedUtc);
        Assert.False(string.IsNullOrEmpty(stored.AppVersion));
        Assert.False(string.IsNullOrEmpty(stored.CorrelationId));
        var step = Assert.Single(stored.Steps);
        Assert.Equal(StepStatus.Succeeded, step.Status);
        Assert.Equal("2", step.Before["data"]);
        Assert.NotNull(step.ExecutedUtc);
    }

    [Fact]
    public async Task RollingBack_RestoresTheExactEarlierValues_IncludingAbsentOnes()
    {
        Seed("MouseSpeed", "2", RegistryValueKind.String);
        Seed("Flags", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
        var before = _kit.Dump();
        var applied = await _kit.Runner.ExecuteAsync(_kit.Runner.Plan("Apply", "Tests", "test:2", new[]
        {
            RegistryChangeHandler.SetString(Key, "MouseSpeed", "0"),
            RegistryChangeHandler.DeleteValue(Key, "Flags"),
            RegistryChangeHandler.SetDword(Key, "New", 7),
            RegistryChangeHandler.EnsureKey(@"HKCU\Software\Test\Brand\New\Deep"),
        }), FailurePolicy.UndoCompleted);
        Assert.NotEqual(before, _kit.Dump());

        var rollback = _kit.Runner.PlanRollback(applied);
        Assert.Equal(applied.Id, rollback.RollbackOf);
        Assert.Equal(4, rollback.Steps.Count(s => s.Status == StepStatus.Planned));
        var undone = await _kit.Runner.ExecuteAsync(rollback, FailurePolicy.ContinueWithOthers);

        Assert.Equal(OperationStatus.Succeeded, undone.Status);
        Assert.Equal(before, _kit.Dump());
        var original = _kit.Runner.Journal.Find(applied.Id)!;
        Assert.Equal(undone.Id, original.RolledBackBy);
        Assert.Equal("Already rolled back.", OperationRunner.WhyNotRollBack(original));
        Assert.Equal(2, _kit.Runner.Journal.All().Count);
    }

    [Fact]
    public async Task RollingBack_LeavesAValueChangedSinceByUs_Alone()
    {
        Seed("MouseSpeed", "2", RegistryValueKind.String);
        var applied = await _kit.Runner.ExecuteAsync(_kit.Runner.Plan("Apply", "Tests", "test:3", new[]
        {
            RegistryChangeHandler.SetString(Key, "MouseSpeed", "0"),
            RegistryChangeHandler.SetString(Key, "MouseThreshold1", "0"),
        }), FailurePolicy.UndoCompleted);
        Seed("MouseSpeed", "5", RegistryValueKind.String); // the user changed it in Control Panel since

        var rollback = _kit.Runner.PlanRollback(applied);
        var changedSince = rollback.Steps.Single(s => s.Target.EndsWith("MouseSpeed", StringComparison.Ordinal));
        Assert.Equal(StepStatus.Skipped, changedSince.Status);
        Assert.Equal("Changed since by something else; left as it is.", changedSince.Message);
        await _kit.Runner.ExecuteAsync(rollback, FailurePolicy.ContinueWithOthers);

        Assert.Equal("5", Read("MouseSpeed"));
        Assert.Null(Read("MouseThreshold1"));
    }

    [Fact]
    public async Task WithoutAdministratorRights_AnAdminChangeIsRefused_AndNothingChanges()
    {
        _kit.Elevated = false;
        var before = _kit.Dump();
        var plan = _kit.Runner.Plan("Policy", "Tests", "test:4", new[] { RegistryChangeHandler.SetDword(@"HKLM\SOFTWARE\Policies\Test", "Value", 1) });
        Assert.True(plan.RequiresAdministrator);

        var result = await _kit.Runner.ExecuteAsync(plan, FailurePolicy.UndoCompleted);

        Assert.Equal(OperationStatus.Refused, result.Status);
        Assert.Contains("Administrator rights are needed", result.StatusMessage);
        Assert.Equal(before, _kit.Dump());
        Assert.Equal("Nothing was changed.", OperationRunner.WhyNotRollBack(result));
    }

    [Fact]
    public async Task AFailedStep_UndoesTheOthers_WhenTheyBelongTogether()
    {
        _kit.Services.Installed["Svc"] = new ServiceSnapshot(ServiceStart.Automatic, Running: true);
        _kit.Services.RefuseToStop.Add("Svc");
        Seed("MouseSpeed", "2", RegistryValueKind.String);
        var before = _kit.Dump();

        var result = await _kit.Runner.ExecuteAsync(_kit.Runner.Plan("Together", "Tests", "test:5", new[]
        {
            RegistryChangeHandler.SetString(Key, "MouseSpeed", "0"),
            ServiceChangeHandler.Set("Svc", ServiceStart.Manual, running: false),
            RegistryChangeHandler.SetString(Key, "Other", "x"),
        }), FailurePolicy.UndoCompleted);

        Assert.Equal(OperationStatus.FailedAndUndone, result.Status);
        Assert.Equal(before, _kit.Dump());
        Assert.True(result.Steps[0].UndoneAutomatically);
        Assert.Equal(StepStatus.Failed, result.Steps[1].Status);
        Assert.Contains("Cannot stop Svc", result.Steps[1].Message);
        Assert.Equal(StepStatus.Skipped, result.Steps[2].Status);
        Assert.Equal(ServiceStart.Automatic, _kit.Services.Installed["Svc"].Start); // its start type was put back too
        Assert.Equal("It was undone automatically when it failed.", OperationRunner.WhyNotRollBack(result));
    }

    [Fact]
    public void TheJournal_SurvivesARestart_AndSkipsADamagedRecord()
    {
        var plan = _kit.Runner.Plan("Kept", "Tests", "test:6", new[] { RegistryChangeHandler.SetDword(Key, "A", 1) });
        _kit.Runner.Journal.Save(plan);
        System.IO.File.WriteAllText(System.IO.Path.Combine(_kit.JournalDirectory, "op-20261006-000000-broken.json"), "{\"id\":");

        var reopened = new OperationJournal(_kit.JournalDirectory);

        var record = Assert.Single(reopened.All());
        Assert.Equal("Kept", record.Action);
        Assert.Equal(plan.Steps[0].After["data"], record.Steps[0].After["data"]);
    }

    [Fact]
    public void ARollback_CannotItselfBeRolledBack_AndIrreversibleStepsAreNeverPromised()
    {
        var rollback = new OperationRecord { RollbackOf = "x", Status = OperationStatus.Succeeded, Steps = { new OperationStep { Status = StepStatus.Succeeded, Reversibility = Reversibility.FullyReversible } } };
        var irreversible = new OperationRecord { Status = OperationStatus.Succeeded, Steps = { new OperationStep { Status = StepStatus.Succeeded, Reversibility = Reversibility.Irreversible } } };
        var mixed = new OperationRecord
        {
            Status = OperationStatus.Succeeded,
            Steps =
            {
                new OperationStep { Status = StepStatus.Succeeded, Reversibility = Reversibility.FullyReversible },
                new OperationStep { Status = StepStatus.Succeeded, Reversibility = Reversibility.Irreversible },
            },
        };

        Assert.False(OperationRunner.CanRollBack(rollback));
        Assert.Equal("This change cannot be undone from AxePower.", OperationRunner.WhyNotRollBack(irreversible));
        Assert.Equal(Reversibility.Irreversible, mixed.Reversibility); // the whole is only as reversible as its weakest step
        Assert.True(OperationRunner.CanRollBack(mixed));
    }
}
