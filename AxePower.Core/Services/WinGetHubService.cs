using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;

namespace AxePower.Core.Services;

/// <summary>
/// The Software Hub's application service. It owns no data or Windows calls of its own: the
/// catalog (<see cref="SoftwareCatalog"/>) says what exists, <see cref="IPackageDiscovery"/> what is
/// installed, <see cref="InstallPlanner"/> what to do, and this class runs the plan through the
/// allow-listed <see cref="IProcessRunner"/>, the <see cref="IUserShellLauncher"/> and the
/// <see cref="IPortableToolDownloader"/>, turning each step into progress lines.
/// </summary>
public class WinGetHubService : IWinGetHubService
{
    /// <summary>winget's "no applicable update" also counts as done.</summary>
    private static readonly int[] SuccessExitCodes = { 0, -1978335189 };

    private readonly LogService _log;
    private readonly SoftwareCatalog _catalog;
    private readonly IPackageDiscovery _discovery;
    private readonly InstallPlanner _planner;
    private readonly IProcessRunner _runner;
    private readonly IUserShellLauncher _launcher;
    private readonly IPortableToolDownloader _downloader;

    /// <summary>The real machine: built-in catalog, Windows tool locations, GitHub downloads.</summary>
    public WinGetHubService(LogService log)
        : this(log, SoftwareCatalog.CreateDefault(log), new AllowListedProcessRunner(ExecutableAllowList.Windows(), log),
               new UserShellLauncher(log), new GitHubPortableToolDownloader(log))
    {
    }

    public WinGetHubService(LogService log, SoftwareCatalog catalog, IProcessRunner runner, IUserShellLauncher launcher,
        IPortableToolDownloader downloader, IPackageDiscovery? discovery = null, InstallPlanner? planner = null)
    {
        _log = log;
        _catalog = catalog;
        _runner = runner;
        _launcher = launcher;
        _downloader = downloader;
        _planner = planner ?? new InstallPlanner(catalog);
        _discovery = discovery ?? new PackageDiscovery(runner, log, _planner.ToolsRoot);
    }

    public IReadOnlyList<SoftwarePackageItem> GetAvailablePackages() => _catalog.Items;

    public Task RefreshInstallationStatusesAsync(CancellationToken cancellationToken = default) =>
        _discovery.RefreshAsync(_catalog.Items, cancellationToken);

    /// <summary>Kept for callers that only need the registry/file check.</summary>
    public static bool CheckIsInstalledFast(SoftwarePackageItem pkg) => PackageDiscovery.CheckIsInstalledFast(pkg);

    public async Task<bool> InstallPackageAsync(string wingetId, IProgress<string> outputProgress, CancellationToken cancellationToken = default) =>
        (await RunAsync(PackageAction.Install, wingetId, outputProgress, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<bool> UpgradePackageAsync(string wingetId, IProgress<string> outputProgress, CancellationToken cancellationToken = default) =>
        (await RunAsync(PackageAction.Upgrade, wingetId, outputProgress, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<bool> UninstallPackageAsync(string wingetId, IProgress<string> outputProgress, CancellationToken cancellationToken = default) =>
        (await RunAsync(PackageAction.Uninstall, wingetId, outputProgress, cancellationToken).ConfigureAwait(false)).Succeeded;

    public async Task<PackageOperationResult> RunAsync(PackageAction action, string id, IProgress<string> outputProgress, CancellationToken cancellationToken = default)
    {
        using var correlation = LogService.BeginCorrelation();
        var plan = _planner.Plan(action, id);
        try
        {
            return await ExecuteAsync(plan, outputProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outputProgress.Report($"[CANCELLED] {plan.DisplayName}: cancelled.");
            return new PackageOperationResult(PackageOutcome.Cancelled, "Cancelled.");
        }
    }

    public async Task<PackageBatchResult> InstallManyAsync(IReadOnlyList<string> ids, IProgress<string> outputProgress,
        IProgress<PackageBatchProgress>? batchProgress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<PackageBatchItem>();
        for (int i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var name = _catalog.Find(id)?.Name ?? id;
            if (cancellationToken.IsCancellationRequested)
            {
                items.Add(new PackageBatchItem(id, name, new PackageOperationResult(PackageOutcome.Skipped, "Not started: the batch was cancelled.")));
                continue;
            }

            batchProgress?.Report(new PackageBatchProgress(i + 1, ids.Count, id, name, null));
            PackageOperationResult result;
            try
            {
                result = await RunAsync(PackageAction.Install, id, outputProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One package going wrong must not take the rest of the batch with it.
                _log.LogError($"[SoftwareHub] Batch install of '{id}' failed", ex);
                outputProgress.Report($"[EXCEPTION] {name}: {ex.Message}");
                result = new PackageOperationResult(PackageOutcome.Failed, ex.Message);
            }
            items.Add(new PackageBatchItem(id, name, result));
            batchProgress?.Report(new PackageBatchProgress(i + 1, ids.Count, id, name, result.Outcome));
        }
        return new PackageBatchResult(items);
    }

    public async Task<bool> UpgradeAllPackagesAsync(IProgress<string> outputProgress, CancellationToken cancellationToken = default)
    {
        outputProgress.Report("\n=== Starting Batch Upgrade (winget upgrade --all) ===");
        _log.LogInfo("[WinGetHub] Upgrading all installed packages...");

        var (outcome, ok, _) = await RunWinGetAsync(InstallPlanner.WinGetUpgradeAll(), outputProgress, cancellationToken).ConfigureAwait(false);

        outputProgress.Report("\n=== Batch Upgrade Completed ===");
        if (outcome != ProcessRunOutcome.Cancelled)
            await RefreshInstallationStatusesAsync(cancellationToken).ConfigureAwait(false);
        return ok;
    }

    public ShellLaunchResult OpenInstalled(SoftwarePackageItem package)
    {
        var target = _discovery.ResolveLaunchTarget(package);
        if (target is null) return new ShellLaunchResult(ShellLaunchStatus.Failed, $"{package.Name} was not found on this PC.");
        return target.Contains("://", StringComparison.Ordinal) ? _launcher.OpenUri(target) : _launcher.OpenProgram(target);
    }

    public ShellLaunchResult OpenWebPage(string url) => _launcher.OpenUri(url);

    public PackageOperationResult LaunchLocalInstaller(SoftwarePackageItem package)
    {
        var plan = _planner.Plan(PackageAction.Install, package.Id);
        var step = plan.Steps.OfType<LocalInstallerStep>().FirstOrDefault();
        if (step is null) return new PackageOperationResult(PackageOutcome.Refused, $"{package.Name} has no bundled installer.");
        return RunLocalInstaller(step, plan.DisplayName, new Progress<string>());
    }

    // -- Running a plan --------------------------------------------------------------------------

    private async Task<PackageOperationResult> ExecuteAsync(InstallPlan plan, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (plan.IsRefused)
        {
            progress.Report($"\n[Refused] {plan.RefusedReason}");
            _log.LogWarning($"[WinGetHub] {plan.Action} '{plan.TargetId}' refused: {plan.RefusedReason}");
            return new PackageOperationResult(PackageOutcome.Refused, plan.RefusedReason);
        }

        var pkg = plan.Package;
        if (plan.Steps.Any(s => s is WinGetStep { When: StepCondition.Always }))
        {
            progress.Report(plan.Action switch
            {
                PackageAction.Install => $"\n[Install] > {plan.DisplayName} ({plan.TargetId})",
                PackageAction.Upgrade => $"\n[Update] > {plan.DisplayName} ({plan.TargetId})",
                _ => $"\n[Uninstall] > {plan.DisplayName} ({plan.TargetId})"
            });
        }
        _log.LogInfo($"[WinGetHub] {plan.Action} '{plan.TargetId}': {plan.Steps.Count} step(s).");

        bool succeeded = false;
        PackageOperationResult? early = null;
        var wingetFailure = Outcomes.FailureKind.None;
        string? failureReason = null;
        foreach (var step in plan.Steps)
        {
            if (step.When == StepCondition.IfNotYetSucceeded && succeeded) continue;
            cancellationToken.ThrowIfCancellationRequested();

            switch (step)
            {
                case WinGetStep winget:
                    if (winget.Notice != null) progress.Report(winget.Notice);
                    var (outcome, ok, exitCode) = await RunWinGetAsync(winget.Command, progress, cancellationToken).ConfigureAwait(false);
                    if (outcome == ProcessRunOutcome.Cancelled) throw new OperationCanceledException(cancellationToken);
                    succeeded |= ok;
                    if (!ok)
                    {
                        (wingetFailure, failureReason) = outcome == ProcessRunOutcome.TimedOut
                            ? (Outcomes.FailureKind.TimedOut, "WinGet did not finish in time")
                            : Outcomes.OutcomeClassifier.ClassifyWinGet(exitCode ?? -1);
                        if (wingetFailure == Outcomes.FailureKind.None && failureReason != null) succeeded = true; // already installed / up to date
                        if (failureReason != null) progress.Report($"[WinGet] {failureReason}.");
                    }
                    break;

                case CheckInstalledStep check:
                    if (pkg != null && _discovery.IsInstalledFast(pkg))
                    {
                        succeeded = true;
                        if (check.ReportWhenFound) progress.Report($"\n[Install] {plan.DisplayName} is already installed: {pkg.LaunchProtocolOrExe}");
                    }
                    break;

                case OpenWebPageStep page:
                    foreach (var message in page.Messages) progress.Report(message);
                    var opened = _launcher.OpenUri(page.Url);
                    if (!opened.Started) progress.Report($"[Browser Error] {opened.Message}");
                    break;

                case DownloadPortableToolStep download:
                    var exe = await _downloader.DownloadAsync(download.Spec, download.Folder, progress, cancellationToken).ConfigureAwait(false);
                    if (exe != null)
                    {
                        succeeded = true;
                        if (pkg != null) pkg.LaunchProtocolOrExe = exe;
                        progress.Report($"[{download.Spec.DisplayName}] 🚀 Launching {download.Spec.DisplayName} portable...");
                        var started = _launcher.OpenProgram(exe);
                        if (!started.Started) progress.Report($"[{download.Spec.DisplayName}] {started.Message}");
                        _log.LogInfo($"[WinGetHub] Downloaded {download.Spec.DisplayName} to {exe}");
                    }
                    break;

                case RemoveToolFolderStep remove:
                    var (removed, failure) = RemoveToolFolder(remove);
                    if (removed)
                    {
                        succeeded = true;
                        progress.Report($"\n[{remove.Label}] ✓ Removed local files.");
                    }
                    else if (failure != null)
                    {
                        progress.Report($"\n[{remove.Label}] Could not remove portable files: {failure}");
                    }
                    break;

                case LocalInstallerStep local:
                    early = RunLocalInstaller(local, plan.DisplayName, progress);
                    succeeded = early.Succeeded;
                    break;
            }
        }

        if (succeeded && pkg != null) pkg.IsInstalled = plan.Action != PackageAction.Uninstall;
        if (early != null) return early;
        return succeeded
            ? new PackageOperationResult(PackageOutcome.Succeeded)
            : new PackageOperationResult(PackageOutcome.Failed, failureReason, Failure: wingetFailure == Outcomes.FailureKind.None ? Outcomes.FailureKind.PackageManagerFailed : wingetFailure);
    }

    /// <summary>Runs winget and reports its output. Ok is true for a success exit code; ExitCode is null when it did not exit.</summary>
    private async Task<(ProcessRunOutcome Outcome, bool Ok, int? ExitCode)> RunWinGetAsync(ProcessCommand command, IProgress<string> progress, CancellationToken cancellationToken)
    {
        bool ok = false;
        var result = await _runner.RunAsync(command, line =>
        {
            if (string.IsNullOrWhiteSpace(line.Text)) return;
            progress.Report(line.IsError ? $"[ERROR] {line.Text}" : line.Text);
        }, cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case ProcessRunOutcome.Exited:
                progress.Report($"[DONE] Process exited with code: {result.ExitCode}");
                ok = result.ExitedWithAny(SuccessExitCodes);
                break;
            case ProcessRunOutcome.TimedOut:
                progress.Report($"[TIMEOUT] Command timed out after {(int)command.Timeout.TotalSeconds}s.");
                break;
            case ProcessRunOutcome.Cancelled:
                progress.Report("[CANCELLED] Stopped; the running installer and its child processes were ended.");
                break;
            default:
                progress.Report($"[EXCEPTION] Failed to run winget: {result.Error}");
                break;
        }
        return (result.Outcome, ok, result.Outcome == ProcessRunOutcome.Exited ? result.ExitCode : null);
    }

    private PackageOperationResult RunLocalInstaller(LocalInstallerStep step, string displayName, IProgress<string> progress)
    {
        if (step.InstallerPath.Length == 0 || !File.Exists(step.InstallerPath))
            return new PackageOperationResult(PackageOutcome.Failed, $"Local installer for {displayName} not found.", step.FallbackUrl);

        var started = _launcher.RunInstaller(step.InstallerPath);
        if (!started.Started)
        {
            progress.Report($"\n[ERROR] Could not start installer: {started.Message}");
            return new PackageOperationResult(PackageOutcome.Failed, started.Message);
        }
        progress.Report($"\n>>> Launched {displayName} Installer: {step.InstallerPath}");
        return new PackageOperationResult(PackageOutcome.Succeeded, $"Launching {displayName} Installer...");
    }

    /// <summary>Deletes a folder only when it is strictly inside the tools folder and is a real folder, not a link.</summary>
    private (bool Removed, string? Failure) RemoveToolFolder(RemoveToolFolderStep step)
    {
        if (!PackageInput.IsStrictlyInside(step.Folder, _planner.ToolsRoot))
            return (false, "the folder is outside AxePower's tools folder");
        try
        {
            var dir = new DirectoryInfo(step.Folder);
            if (!dir.Exists) return (step.MissingCountsAsRemoved, null);
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return (false, "the folder is a link");
            dir.Delete(recursive: true);
            _log.LogInfo($"[WinGetHub] Removed {step.Folder}.");
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }
}
