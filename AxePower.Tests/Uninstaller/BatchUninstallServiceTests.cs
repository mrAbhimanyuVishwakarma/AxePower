using System.IO;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// The batch with every dependency faked: no process is started and nothing is uninstalled.
/// Verification alone decides whether an app counts as removed (D1, D4).
/// </summary>
public class BatchUninstallServiceTests
{
    private sealed class Harness
    {
        public FakeVerifier Verifier { get; } = new();
        public FakeScanner Scanner { get; } = new();
        public FakeDiscovery Discovery { get; } = new();
        public InMemoryHistory History { get; } = new();
        public NoRunningProcesses Processes { get; } = new();
        public FakeFileSystemProbe Fs { get; } = new();
        public FakeMsiReader Msi { get; } = new();
        public FakeExecutor Executor { get; }

        public Harness(Func<InstalledApplication, UninstallExecution> execution)
        {
            Executor = new FakeExecutor(execution);
        }

        public BatchUninstallService Build()
        {
            var log = new LogService();
            return new BatchUninstallService(
                new UninstallExecutorFactory([Executor]), Scanner, Verifier, Discovery, new ProtectionService(), History,
                new SystemRestoreService(log), Processes, Fs, Msi, log, () => false);
        }
    }

    private static readonly UninstallerSettings Settings = new() { CreateSystemRestorePoint = false };

    private static InstalledApplication App(string name = "Test App", UninstallerKind kind = UninstallerKind.Nsis) => new()
    {
        Id = name,
        DisplayName = name,
        RegistryKeyPath = $@"HKCU\Software\AxePower.Tests\Uninstall\{name}",
        UninstallerKind = kind,
        UninstallerPath = @"C:\Test\uninst.exe",
        ProductCode = kind == UninstallerKind.Msi ? "{11111111-2222-3333-4444-555555555555}" : string.Empty,
        CanUninstall = true,
    };

    private static UninstallExecution Exit(int code, UninstallerKind kind = UninstallerKind.Nsis)
    {
        var (verdict, message) = ExitCodeInterpreter.Interpret(kind, code);
        return new UninstallExecution { Verdict = verdict, ExitCode = code, Message = message, CommandExecuted = "test" };
    }

    [Fact]
    public async Task ExitZero_ButStillRegistered_IsStillInstalled_AndNotScanned()
    {
        var h = new Harness(_ => Exit(0));
        h.Verifier.StillInstalledAfterRun = true;

        var session = await h.Build().ExecuteBatchAsync([App()], Settings);

        var result = Assert.Single(session.Results);
        Assert.Equal(UninstallOutcome.StillInstalled, result.Outcome);
        Assert.False(result.Success);
        Assert.Empty(h.Scanner.Scanned);
    }

    [Fact]
    public async Task ExitZero_AndGone_IsRemoved_AndScannedAgainstAFreshSnapshot()
    {
        var h = new Harness(_ => Exit(0));
        h.Discovery.Apps.Add(new InstalledApplication { DisplayName = "Still here", InstallLocation = @"C:\Other" });

        var session = await h.Build().ExecuteBatchAsync([App()], Settings);

        Assert.Equal(UninstallOutcome.Removed, Assert.Single(session.Results).Outcome);
        Assert.Equal(["Test App"], h.Scanner.Scanned);
        Assert.Single(h.Scanner.LastOptions!.InstalledApplications);
        Assert.True(h.Scanner.LastOptions.AllowPreselection);
        Assert.Single(session.LeftoverItems);
    }

    [Fact]
    public async Task Msi1605_AndGone_IsAlreadyRemoved()
    {
        var h = new Harness(_ => Exit(ExitCodeInterpreter.MsiUnknownProduct, UninstallerKind.Msi));

        var session = await h.Build().ExecuteBatchAsync([App(kind: UninstallerKind.Msi)], Settings);

        Assert.Equal(UninstallOutcome.AlreadyRemoved, Assert.Single(session.Results).Outcome);
    }

    [Fact]
    public async Task Msi1602_IsCancelled_AndNotScanned()
    {
        var h = new Harness(_ => Exit(ExitCodeInterpreter.MsiUserExit, UninstallerKind.Msi));
        h.Verifier.StillInstalledAfterRun = true;

        var session = await h.Build().ExecuteBatchAsync([App(kind: UninstallerKind.Msi)], Settings);

        Assert.Equal(UninstallOutcome.Cancelled, Assert.Single(session.Results).Outcome);
        Assert.Empty(h.Scanner.Scanned);
    }

    [Fact]
    public async Task RebootRequired_StillRegistered_IsRemovedPendingReboot()
    {
        var h = new Harness(_ => Exit(ExitCodeInterpreter.RebootRequired, UninstallerKind.Msi));
        h.Verifier.StillInstalledAfterRun = true;

        var session = await h.Build().ExecuteBatchAsync([App(kind: UninstallerKind.Msi)], Settings);

        var result = Assert.Single(session.Results);
        Assert.Equal(UninstallOutcome.RemovedPendingReboot, result.Outcome);
        Assert.True(result.RequiresReboot);
    }

    [Fact]
    public async Task EntryAlreadyGone_IsAlreadyRemoved_AndNothingRuns()
    {
        var h = new Harness(_ => Exit(0));
        h.Verifier.Registered = false;

        var session = await h.Build().ExecuteBatchAsync([App()], Settings);

        Assert.Equal(UninstallOutcome.AlreadyRemoved, Assert.Single(session.Results).Outcome);
        Assert.Empty(h.Executor.Executed);
    }

    [Fact]
    public async Task BrokenEntry_IsNeverReportedAsRemoved()
    {
        var h = new Harness(_ => new UninstallExecution { Verdict = ExecutionVerdict.EntryRemoved });

        var session = await h.Build().ExecuteBatchAsync([App(kind: UninstallerKind.BrokenEntry)], Settings);

        var result = Assert.Single(session.Results);
        Assert.Equal(UninstallOutcome.BrokenEntryRemoved, result.Outcome);
        Assert.NotEqual(UninstallOutcome.Removed, result.Outcome);
        // Its leftovers are offered but never pre-ticked
        Assert.False(h.Scanner.LastOptions!.AllowPreselection);
    }

    [Fact]
    public async Task UacRefused_IsCancelled_NotFailed()
    {
        var h = new Harness(_ => new UninstallExecution { Verdict = ExecutionVerdict.Cancelled, Message = "Administrator permission was refused." });
        h.Verifier.StillInstalledAfterRun = true;

        var session = await h.Build().ExecuteBatchAsync([App()], Settings);

        var result = Assert.Single(session.Results);
        Assert.Equal(UninstallOutcome.Cancelled, result.Outcome);
        Assert.Contains("refused", result.ErrorMessage);
    }

    [Fact]
    public async Task OneFailure_DoesNotStopTheQueue()
    {
        var h = new Harness(app => app.DisplayName == "Bad" ? new UninstallExecution { Verdict = ExecutionVerdict.Failed, Message = "boom" } : Exit(0));

        var session = await h.Build().ExecuteBatchAsync([App("Bad"), App("Good")], Settings);

        Assert.Equal(["Bad", "Good"], h.Executor.Executed);
        Assert.Equal(2, session.Results.Count);
        Assert.Single(h.History.Sessions);
    }

    [Fact]
    public async Task StopRequested_EndsTheQueueAfterTheCurrentApp_AndSavesTheSession()
    {
        var control = new UninstallBatchControl();
        var h = new Harness(_ => Exit(0));
        h.Executor.BeforeReturn = (_, _) =>
        {
            control.RequestStop();
            return Task.CompletedTask;
        };

        var session = await h.Build().ExecuteBatchAsync([App("First"), App("Second")], Settings, control: control);

        Assert.Equal(["First"], h.Executor.Executed);
        Assert.True(session.WasCancelled);
        Assert.Equal(UninstallOutcome.Removed, session.Results[0].Outcome);
        Assert.Equal(UninstallOutcome.Skipped, session.Results[1].Outcome);
        Assert.Contains(h.History.Sessions, s => s.SessionId == session.SessionId);
    }

    [Fact]
    public async Task HardCancellation_SavesTheFinishedItems()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness(_ => Exit(0));
        h.Executor.BeforeReturn = (_, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var session = await h.Build().ExecuteBatchAsync([App("First"), App("Second")], Settings, cancellationToken: cts.Token);

        Assert.True(session.WasCancelled);
        Assert.Equal(2, session.Results.Count);
        Assert.Single(h.History.Sessions);
    }

    [Fact]
    public async Task ProtectedApp_IsSkipped_WithoutAbortingTheBatch()
    {
        var h = new Harness(_ => Exit(0));

        var session = await h.Build().ExecuteBatchAsync([App("AxePower Tools"), App("Normal")], Settings);

        Assert.Equal(UninstallOutcome.Skipped, session.Results[0].Outcome);
        Assert.Equal(UninstallOutcome.Removed, session.Results[1].Outcome);
        Assert.Equal(["Normal"], h.Executor.Executed);
    }

    [Fact]
    public async Task RunningApp_Skip_IsHonoured()
    {
        var h = new Harness(_ => Exit(0));
        h.Processes.Running.Add(new RunningProcessInfo(1234, "app.exe", @"C:\Test\app.exe"));
        var interaction = new FakeInteraction();
        interaction.RunningAnswers.Enqueue(RunningAppDecision.Skip);

        var session = await h.Build().ExecuteBatchAsync([App()], Settings, interaction: interaction);

        Assert.Equal(UninstallOutcome.Skipped, Assert.Single(session.Results).Outcome);
        Assert.Empty(h.Executor.Executed);
    }

    [Fact]
    public async Task RunningApp_Close_ThenUninstalls()
    {
        var h = new Harness(_ => Exit(0));
        h.Processes.Running.Add(new RunningProcessInfo(1234, "app.exe", @"C:\Test\app.exe"));
        var interaction = new FakeInteraction();
        interaction.RunningAnswers.Enqueue(RunningAppDecision.Close);

        var session = await h.Build().ExecuteBatchAsync([App()], Settings, interaction: interaction);

        Assert.Equal(UninstallOutcome.Removed, Assert.Single(session.Results).Outcome);
    }

    [Fact]
    public async Task StillInstalled_WithUninstallerGone_OffersRemoveBrokenEntry()
    {
        var h = new Harness(_ => Exit(0));
        h.Verifier.StillInstalledAfterRun = true; // the fake file system has no uninst.exe

        var session = await h.Build().ExecuteBatchAsync([App()], Settings);

        var result = Assert.Single(session.Results);
        Assert.Equal(UninstallOutcome.StillInstalled, result.Outcome);
        Assert.True(result.OfferRemoveBrokenEntry);
    }

    [Fact]
    public async Task DryRun_RecordsDryRun_AndScansNothing()
    {
        var h = new Harness(_ => new UninstallExecution { Verdict = ExecutionVerdict.DryRun, CommandExecuted = "[DRY RUN] x" });

        var session = await h.Build().ExecuteBatchAsync([App()], new UninstallerSettings { DryRunMode = true, CreateSystemRestorePoint = false });

        Assert.Equal(UninstallOutcome.DryRun, Assert.Single(session.Results).Outcome);
        Assert.Empty(h.Scanner.Scanned);
    }

    [Theory]
    [InlineData(ExecutionVerdict.Finished, false, UninstallOutcome.Removed)]
    [InlineData(ExecutionVerdict.Finished, true, UninstallOutcome.StillInstalled)]
    [InlineData(ExecutionVerdict.UnknownProduct, false, UninstallOutcome.AlreadyRemoved)]
    [InlineData(ExecutionVerdict.FinishedRebootRequired, true, UninstallOutcome.RemovedPendingReboot)]
    [InlineData(ExecutionVerdict.Cancelled, true, UninstallOutcome.Cancelled)]
    [InlineData(ExecutionVerdict.Cancelled, false, UninstallOutcome.Removed)]
    [InlineData(ExecutionVerdict.WaitAbandoned, true, UninstallOutcome.Cancelled)]
    [InlineData(ExecutionVerdict.UnknownError, false, UninstallOutcome.Removed)]
    [InlineData(ExecutionVerdict.UnknownError, true, UninstallOutcome.Failed)]
    [InlineData(ExecutionVerdict.Failed, true, UninstallOutcome.Failed)]
    public void OutcomeTable_VerificationDecides(ExecutionVerdict verdict, bool stillInstalled, UninstallOutcome expected)
    {
        var (outcome, _) = BatchUninstallService.ResolveOutcome(new UninstallExecution { Verdict = verdict }, stillInstalled, "App");
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public void HandedOffUninstaller_GetsItsOwnMessage()
    {
        var (outcome, message) = BatchUninstallService.ResolveOutcome(
            new UninstallExecution { Verdict = ExecutionVerdict.Finished, HandedOff = true }, true, "Game");
        Assert.Equal(UninstallOutcome.StillInstalled, outcome);
        Assert.Contains("handed off", message);
    }
}
