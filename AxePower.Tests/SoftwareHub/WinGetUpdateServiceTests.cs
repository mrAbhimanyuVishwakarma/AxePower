using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;

namespace AxePower.Tests.SoftwareHub;

public sealed class WinGetUpdateServiceTests
{
    private static readonly WinGetUpdateCandidate Reviewed =
        new("Visual Studio Code", "Microsoft.VisualStudioCode", "1.98.0", "1.99.1", "winget", true);

    [Fact]
    public async Task ScansKnownSourcesWithoutAcceptingAgreementsOrSelectingPinnedAndUnknown()
    {
        var runner = new PreviewRunner(c => c.Arguments[3] == "winget"
            ? (0, Sample(Reviewed))
            : (unchecked((int)0x8A15003A), Array.Empty<string>()));

        var scan = await new WinGetUpdateService(runner, () => true).ScanAsync();

        Assert.True(scan.Succeeded);
        Assert.True(scan.CanBulkUpgrade);
        Assert.True(scan.IsPartial);
        Assert.Equal(Reviewed, Assert.Single(scan.Candidates));
        Assert.Contains("policy", scan.Detail);
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal(AllowedTool.WinGet, command.Tool);
            Assert.Equal("list", command.Arguments[0]);
            Assert.Contains("--upgrade-available", command.Arguments);
            Assert.DoesNotContain("--accept-source-agreements", command.Arguments);
            Assert.DoesNotContain("--include-unknown", command.Arguments);
            Assert.DoesNotContain("--include-pinned", command.Arguments);
        });
    }

    [Fact]
    public async Task UnreviewedSourceTermsPermitOnlyTheSeparateBulkAction()
    {
        var runner = new PreviewRunner(_ => (unchecked((int)0x8A150046), Array.Empty<string>()));
        var service = new WinGetUpdateService(runner, () => true);

        var scan = await service.ScanAsync();
        var selected = await service.UpgradeAsync(Reviewed, new LineSink());

        Assert.False(scan.Succeeded);
        Assert.True(scan.CanBulkUpgrade);
        Assert.Contains("terms", scan.Detail);
        Assert.Empty(scan.Candidates);
        Assert.Equal(WinGetUpdateOutcome.Refused, selected.Outcome);
        Assert.All(runner.Commands, command => Assert.Equal("list", command.Arguments[0]));
    }

    [Fact]
    public async Task SameDesktopAccountIsRequiredBeforeScanningOrRunningAnUpgrade()
    {
        var runner = new PreviewRunner(_ => (0, Sample(Reviewed)));
        var service = new WinGetUpdateService(runner, () => false);

        var scan = await service.ScanAsync();
        var selected = await service.UpgradeAsync(Reviewed, new LineSink());
        var bulk = await service.UpgradeAllAsync(true, new LineSink());

        Assert.False(scan.Succeeded);
        Assert.False(scan.CanBulkUpgrade);
        Assert.Equal(WinGetUpdateOutcome.Refused, selected.Outcome);
        Assert.Equal(WinGetUpdateOutcome.Refused, bulk.Outcome);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task SelectedUpgradeRechecksAndUsesReviewedSourceAndVersion()
    {
        var runner = new PreviewRunner(c => (0, c.Arguments[0] == "list" ? Sample(Reviewed) : Array.Empty<string>()));

        var result = await new WinGetUpdateService(runner, () => true).UpgradeAsync(Reviewed, new LineSink());

        Assert.Equal(WinGetUpdateOutcome.Applied, result.Outcome);
        Assert.Collection(runner.Commands,
            command => Assert.Equal(new[] { "list", "--upgrade-available", "--source", "winget", "--disable-interactivity", "--id", Reviewed.Id, "--exact" }, command.Arguments),
            command => Assert.Equal(new[] { "upgrade", "--id", Reviewed.Id, "--exact", "--source", "winget", "--version", Reviewed.AvailableVersion,
                "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" }, command.Arguments));
    }

    [Fact]
    public async Task AChangedVersionNeverExecutesAnInstaller()
    {
        var runner = new PreviewRunner(_ => (0, Sample(Reviewed with { AvailableVersion = "1.99.2" })));

        var result = await new WinGetUpdateService(runner, () => true).UpgradeAsync(Reviewed, new LineSink());

        Assert.Equal(WinGetUpdateOutcome.Refused, result.Outcome);
        Assert.Equal("list", Assert.Single(runner.Commands).Arguments[0]);
    }

    [Fact]
    public async Task UnknownVersionsAreOptInAndBulkReturnsAnAggregateOutcome()
    {
        var runner = new PreviewRunner(_ => (0, Array.Empty<string>()));
        var service = new WinGetUpdateService(runner, () => true);

        await service.UpgradeAllAsync(false, new LineSink());
        var result = await service.UpgradeAllAsync(true, new LineSink());

        Assert.Equal(WinGetUpdateOutcome.Finished, result.Outcome);
        Assert.Collection(runner.Commands,
            command => Assert.Equal(new[] { "upgrade", "--all", "--accept-package-agreements", "--accept-source-agreements" }, command.Arguments),
            command => Assert.Equal(new[] { "upgrade", "--all", "--include-unknown", "--accept-package-agreements", "--accept-source-agreements" }, command.Arguments));
    }

    [Theory]
    [InlineData(0x8A150109u, WinGetUpdateOutcome.RestartRequired, true)]
    [InlineData(0x8A15010Au, WinGetUpdateOutcome.RestartRequired, true)]
    [InlineData(0x8A150103u, WinGetUpdateOutcome.Failed, false)]
    [InlineData(0x8A15002Cu, WinGetUpdateOutcome.Partial, false)]
    [InlineData(0x8A15002Bu, WinGetUpdateOutcome.NoApplicableUpdates, false)]
    [InlineData(0x8A15010Cu, WinGetUpdateOutcome.Cancelled, false)]
    public async Task KnownExitCodesAreReportedWithoutAUniversalSuccessClaim(uint code, WinGetUpdateOutcome outcome, bool reboot)
    {
        var runner = new PreviewRunner(_ => (unchecked((int)code), Array.Empty<string>()));

        var result = await new WinGetUpdateService(runner, () => true).UpgradeAllAsync(false, new LineSink());

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reboot, result.RebootRequired);
    }

    [Fact]
    public void ReadsAlignedLocalizedColumnsAndRefusesAmbiguousPicks()
    {
        var sample = Sample(Reviewed);
        sample[0] = Row("Nom de l’application", "Identifiant", "Version", "Disponible", "Source");
        Assert.Equal(Reviewed, Assert.Single(WinGetUpdateService.ParsePreview(sample, "winget").Candidates));

        var duplicate = sample.Take(3).Concat(new[] { sample[2], sample[3] });
        Assert.False(WinGetUpdateService.ParsePreview(duplicate, "winget").Succeeded);

        var pinned = new[] { "An explicit upgrade is needed for this pinned package:" }.Concat(sample);
        Assert.False(WinGetUpdateService.ParsePreview(pinned, "winget").Succeeded);
    }

    [Fact]
    public void UnknownOrTruncatedIdentityIsNeverActionable()
    {
        Assert.Empty(WinGetUpdateService.ParsePreview(Sample(Reviewed with { InstalledVersion = "Unknown" }), "winget").Candidates);
        Assert.Empty(WinGetUpdateService.ParsePreview(Sample(Reviewed with { Id = "Microsoft.VisualStudioC…" }), "winget").Candidates);
        Assert.Empty(WinGetUpdateService.ParsePreview(Sample(Reviewed with { AvailableVersion = "1.99.…" }), "winget").Candidates);
        Assert.Empty(WinGetUpdateService.ParsePreview(Sample(Reviewed with { Source = "other" }), "winget").Candidates);
    }

    [Fact]
    public void NamedSourceTableMayHaveAnEmptySourceColumn()
    {
        var sample = Sample(Reviewed with { Source = "" });
        Assert.Equal(Reviewed, Assert.Single(WinGetUpdateService.ParsePreview(sample, "winget").Candidates));
    }

    // Named-source listings can leave the Source column empty.
    private static string[] Sample(WinGetUpdateCandidate item) =>
        new[] { Row("Name", "Id", "Version", "Available", "Source"), new string('-', 82),
            Row(item.Name, item.Id, item.InstalledVersion, item.AvailableVersion, item.Source), "1 upgrades available." };

    private static string Row(string name, string id, string installed, string available, string source) =>
        $"{name,-28}{id,-28}{installed,-12}{available,-13}{source}";

    private sealed class PreviewRunner(Func<ProcessCommand, (int Code, string[] Lines)> reply) : IProcessRunner
    {
        public List<ProcessCommand> Commands { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessCommand command, Action<ProcessOutputLine>? output = null, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var result = reply(command);
            foreach (var line in result.Lines) output?.Invoke(new ProcessOutputLine(line, false));
            return Task.FromResult(new ProcessRunResult(ProcessRunOutcome.Exited, result.Code));
        }
    }
}
