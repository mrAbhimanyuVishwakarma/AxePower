using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Orchestrates sequential batch uninstallation of multiple applications.
/// Never launches uninstallers simultaneously, and never records success without verifying it.
///
/// Per app: pre-check (entry still there?) → running-app check → run the uninstaller → wait for
/// its whole process tree → read the exit code → verify against the registry / Windows
/// Installer / AppX → scan leftovers only after a verified removal, against a fresh list of the
/// other installed apps. Failure of one application does not abort the remaining queue.
/// </summary>
public sealed class BatchUninstallService : IBatchUninstallService
{
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly UninstallExecutorFactory _executorFactory;
    private readonly ILeftoverScannerService _leftoverScanner;
    private readonly IInstallStateVerifier _verifier;
    private readonly IApplicationDiscoveryService _discovery;
    private readonly IProtectionService _protection;
    private readonly IUninstallHistoryService _historyService;
    private readonly SystemRestoreService _systemRestore;
    private readonly IProcessDetectionService _processDetection;
    private readonly IFileSystemProbe _fs;
    private readonly IMsiProductStateReader _msi;
    private readonly LogService _log;
    private readonly Func<bool> _isElevated;

    public BatchUninstallService(
        UninstallExecutorFactory executorFactory,
        ILeftoverScannerService leftoverScanner,
        IInstallStateVerifier verifier,
        IApplicationDiscoveryService discovery,
        IProtectionService protection,
        IUninstallHistoryService historyService,
        SystemRestoreService systemRestore,
        IProcessDetectionService processDetection,
        IFileSystemProbe fs,
        IMsiProductStateReader msi,
        LogService log,
        ElevationService elevation)
        : this(executorFactory, leftoverScanner, verifier, discovery, protection, historyService, systemRestore,
               processDetection, fs, msi, log, () => elevation.IsElevated)
    {
    }

    public BatchUninstallService(
        UninstallExecutorFactory executorFactory,
        ILeftoverScannerService leftoverScanner,
        IInstallStateVerifier verifier,
        IApplicationDiscoveryService discovery,
        IProtectionService protection,
        IUninstallHistoryService historyService,
        SystemRestoreService systemRestore,
        IProcessDetectionService processDetection,
        IFileSystemProbe fs,
        IMsiProductStateReader msi,
        LogService log,
        Func<bool> isElevated)
    {
        _executorFactory  = executorFactory;
        _leftoverScanner  = leftoverScanner;
        _verifier         = verifier;
        _discovery        = discovery;
        _protection       = protection;
        _historyService   = historyService;
        _systemRestore    = systemRestore;
        _processDetection = processDetection;
        _fs               = fs;
        _msi              = msi;
        _log              = log;
        _isElevated       = isElevated;
    }

    public async Task<UninstallSession> ExecuteBatchAsync(
        IReadOnlyList<InstalledApplication> applications,
        UninstallerSettings settings,
        IProgress<UninstallProgress>? progress = null,
        IUninstallInteraction? interaction = null,
        UninstallBatchControl? control = null,
        CancellationToken cancellationToken = default)
    {
        control ??= new UninstallBatchControl();
        var session = new UninstallSession
        {
            StartTime  = DateTime.Now,
            ScanMode   = settings.DefaultScanMode.ToString(),
            WasDryRun  = settings.DryRunMode,
            ApplicationNames = applications.Select(a => a.DisplayName).ToList(),
        };

        _log.LogInfo($"[Uninstaller] Starting batch session {session.SessionId} — {applications.Count} apps.");

        // ── System Restore Point ────────────────────────────────────────────
        if (settings.CreateSystemRestorePoint && !settings.DryRunMode && applications.Count > 0)
        {
            Report(progress, applications, 0, UninstallPhase.CreatingRestorePoint, "Creating System Restore Point...");
            session.SystemRestorePointCreated =
                await _systemRestore.TryCreateRestorePointAsync(
                    $"AxePower Uninstaller — {applications.Count} apps").ConfigureAwait(false);
        }

        for (int i = 0; i < applications.Count; i++)
        {
            var app = applications[i];

            if (control.StopRequested || cancellationToken.IsCancellationRequested)
            {
                session.WasCancelled = true;
                foreach (var remaining in applications.Skip(i))
                    session.Results.Add(Skipped(remaining, "Not started: the queue was stopped."));
                break;
            }

            _log.LogInfo($"[Uninstaller] [{i + 1}/{applications.Count}] Processing: {app.DisplayName} ({app.UninstallerKind})");

            UninstallResult result;
            try
            {
                result = await UninstallOneAsync(app, i, applications, settings, session, progress, interaction, control, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = Result(app, UninstallOutcome.Cancelled, "AxePower stopped before the result could be verified.");
            }
            catch (Exception ex)
            {
                _log.LogError($"[Uninstaller] {app.DisplayName} — unexpected error: {ex.Message}");
                result = Result(app, UninstallOutcome.Failed, ex.Message);
            }

            session.Results.Add(result);
            _log.LogInfo($"[Uninstaller] {app.DisplayName} — {result.Outcome}" +
                         (result.ExitCode is { } code ? $" (exit {code})" : string.Empty) +
                         (string.IsNullOrEmpty(result.ErrorMessage) ? string.Empty : $": {result.ErrorMessage}"));

            // ── Leftovers: only after a verified removal ────────────────────
            if (settings.AutoScanLeftovers && !settings.DryRunMode && !cancellationToken.IsCancellationRequested &&
                result.Outcome is UninstallOutcome.Removed or UninstallOutcome.AlreadyRemoved or UninstallOutcome.BrokenEntryRemoved)
            {
                Report(progress, applications, i, UninstallPhase.ScanningLeftovers, $"Scanning leftovers for {app.DisplayName}...");
                try
                {
                    var others = await _discovery.DiscoverRegistryApplicationsAsync(cancellationToken).ConfigureAwait(false);
                    var leftovers = await _leftoverScanner.ScanAsync(app, settings.DefaultScanMode, settings,
                        new LeftoverScanOptions
                        {
                            InstalledApplications = others,
                            // A broken entry's leftovers are offered, never pre-ticked
                            AllowPreselection = result.Outcome != UninstallOutcome.BrokenEntryRemoved,
                            IsElevated = _isElevated(),
                        },
                        cancellationToken).ConfigureAwait(false);

                    session.LeftoverItems.AddRange(leftovers);
                    _log.LogInfo($"[Uninstaller] {app.DisplayName} — {leftovers.Count} leftover items found.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _log.LogError($"[Uninstaller] Leftover scan failed for {app.DisplayName}: {ex.Message}");
                }
            }
        }

        // ── Finalize session ─────────────────────────────────────────────────
        session.EndTime = DateTime.Now;
        await _historyService.SaveSessionAsync(session).ConfigureAwait(false);

        _log.LogInfo($"[Uninstaller] Session {session.SessionId} complete — {session.OutcomeSummary}, " +
                     $"{session.LeftoverItems.Count} leftovers detected.");

        Report(progress, applications, applications.Count, UninstallPhase.Complete,
            session.WasCancelled ? "Stopped." : "Batch uninstall complete.");
        return session;
    }

    private async Task<UninstallResult> UninstallOneAsync(
        InstalledApplication app,
        int index,
        IReadOnlyList<InstalledApplication> applications,
        UninstallerSettings settings,
        UninstallSession session,
        IProgress<UninstallProgress>? progress,
        IUninstallInteraction? interaction,
        UninstallBatchControl control,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // ── Protection (per app; a protected pick never aborts the batch) ────
        var protectedReason = _protection.GetProtectionReason(app);
        if (protectedReason is not null)
            return Result(app, UninstallOutcome.Skipped, $"Protected: {protectedReason}");
        if (!app.CanUninstall)
            return Result(app, UninstallOutcome.Skipped, "Windows marks this entry as not removable (NoRemove).");

        // ── 1. Pre-check: is it still there? ─────────────────────────────────
        Report(progress, applications, index, UninstallPhase.PreFlight, $"Preparing to uninstall {app.DisplayName}...");
        if (app.UninstallerKind != UninstallerKind.StoreApp &&
            !await _verifier.IsRegisteredAsync(app, cancellationToken).ConfigureAwait(false))
            return Result(app, UninstallOutcome.AlreadyRemoved, "Its uninstall entry was already gone.");

        // ── Running app ──────────────────────────────────────────────────────
        if (app.UninstallerKind is not (UninstallerKind.BrokenEntry or UninstallerKind.StoreApp) && !settings.DryRunMode)
        {
            Report(progress, applications, index, UninstallPhase.CheckingProcess, $"Checking if {app.DisplayName} is running...");
            var skipReason = await HandleRunningAppAsync(app, interaction, cancellationToken).ConfigureAwait(false);
            if (skipReason is not null)
                return Result(app, UninstallOutcome.Skipped, skipReason);
        }

        // ── 2–3. Execute and wait ────────────────────────────────────────────
        Report(progress, applications, index, UninstallPhase.Uninstalling, $"Uninstalling {app.DisplayName}...");
        var executor = _executorFactory.GetExecutor(app);

        UninstallExecution execution;
        using (var scope = control.BeginApp(app.DisplayName, cancellationToken))
        {
            var context = new UninstallRunContext
            {
                SessionId = session.SessionId,
                IsElevated = _isElevated(),
                KillToken = scope.KillToken,
                OnQuietStalled = interaction is null
                    ? null
                    : (elapsed, ct) => interaction.ShouldStopStalledUninstallerAsync(app, elapsed, ct),
                OnLaunched = plan =>
                {
                    control.SetRunning(true, plan.IsQuiet);
                    Report(progress, applications, index, UninstallPhase.WaitingForUninstaller,
                        $"Waiting for {app.DisplayName}'s uninstaller to finish...", plan.IsQuiet);
                },
            };

            try
            {
                execution = await executor.ExecuteAsync(app, settings, context, scope.AbandonToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scope.AbandonToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                execution = new UninstallExecution
                {
                    Verdict = ExecutionVerdict.WaitAbandoned,
                    Message = "Stopped waiting for the uninstaller; it may still be running.",
                };
            }
            finally
            {
                control.SetRunning(false, false);
            }
        }

        // ── 4–5. Interpret and verify ────────────────────────────────────────
        Report(progress, applications, index, UninstallPhase.Verifying, $"Verifying {app.DisplayName} was removed...");
        var result = await VerifyAsync(app, execution, cancellationToken).ConfigureAwait(false);
        result.Duration = sw.Elapsed;
        return result;
    }

    /// <summary>Returns a skip reason, or null to go ahead.</summary>
    private async Task<string?> HandleRunningAppAsync(InstalledApplication app, IUninstallInteraction? interaction,
        CancellationToken cancellationToken)
    {
        var running = _processDetection.GetRunningProcesses(app);
        app.IsRunning = running.Count > 0;
        if (running.Count == 0) return null;

        _log.LogWarning($"[Uninstaller] {app.DisplayName} is running ({running.Count} process(es)).");
        if (interaction is null) return null;

        var decision = await interaction.ResolveRunningApplicationAsync(app, running, false, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            switch (decision)
            {
                case RunningAppDecision.Skip:
                    return "Skipped because it was running.";
                case RunningAppDecision.Continue:
                    return null;
                case RunningAppDecision.Close:
                case RunningAppDecision.ForceClose:
                    var force = decision == RunningAppDecision.ForceClose;
                    if (await _processDetection.TryCloseApplicationAsync(app, CloseTimeout, force).ConfigureAwait(false))
                        return null;
                    _log.LogWarning($"[Uninstaller] {app.DisplayName} did not close ({(force ? "force" : "polite")}).");
                    running = _processDetection.GetRunningProcesses(app);
                    decision = await interaction.ResolveRunningApplicationAsync(app, running, true, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>Turns what the executor saw into a verified outcome.</summary>
    private async Task<UninstallResult> VerifyAsync(InstalledApplication app, UninstallExecution execution,
        CancellationToken cancellationToken)
    {
        var result = new UninstallResult
        {
            ApplicationId = app.Id,
            ApplicationName = app.DisplayName,
            ApplicationType = app.ApplicationType,
            UninstallerKind = app.UninstallerKind,
            UninstallMethod = execution.Method,
            CommandExecuted = execution.CommandExecuted,
            ExitCode = execution.ExitCode,
            WasQuiet = execution.WasQuiet,
            LogPath = execution.LogPath,
            Outcome = UninstallOutcome.Failed,
        };

        switch (execution.Verdict)
        {
            case ExecutionVerdict.DryRun:
                result.Outcome = UninstallOutcome.DryRun;
                return result;
            case ExecutionVerdict.NeedsAdmin:
                result.Outcome = UninstallOutcome.NeedsAdmin;
                result.ErrorMessage = execution.Message;
                return result;
            case ExecutionVerdict.EntryRemoved:
                var entryStillThere = await _verifier.IsRegisteredAsync(app, cancellationToken).ConfigureAwait(false);
                result.Outcome = entryStillThere ? UninstallOutcome.Failed : UninstallOutcome.BrokenEntryRemoved;
                result.ErrorMessage = entryStillThere ? "The entry is still there after deleting it." : execution.Message;
                return result;
        }

        // Some uninstallers remove their key last, so give a finished one a few seconds
        var poll = execution.Verdict is ExecutionVerdict.Finished or ExecutionVerdict.FinishedRebootRequired
                                     or ExecutionVerdict.UnknownProduct or ExecutionVerdict.UnknownError;
        var stillInstalled = poll
            ? await _verifier.WaitUntilRemovedAsync(app, VerifyTimeout, cancellationToken).ConfigureAwait(false)
            : await _verifier.IsStillInstalledAsync(app, cancellationToken).ConfigureAwait(false);

        var (outcome, message) = ResolveOutcome(execution, stillInstalled, app.DisplayName);
        result.Outcome = outcome;
        result.ErrorMessage = message;
        result.RequiresReboot = execution.Verdict == ExecutionVerdict.FinishedRebootRequired;

        if (stillInstalled)
            result.OfferRemoveBrokenEntry = UninstallerNowMissing(app);

        return result;
    }

    /// <summary>The verified-outcome table: the exit code only chooses the wording when the app is still installed.</summary>
    public static (UninstallOutcome Outcome, string Message) ResolveOutcome(UninstallExecution execution, bool stillInstalled, string appName)
    {
        if (!stillInstalled)
        {
            return execution.Verdict == ExecutionVerdict.UnknownProduct
                ? (UninstallOutcome.AlreadyRemoved, "Windows Installer had already removed it.")
                : (UninstallOutcome.Removed, execution.Verdict == ExecutionVerdict.FinishedRebootRequired
                    ? "Removed. The uninstaller recommends a restart."
                    : string.Empty);
        }

        return execution.Verdict switch
        {
            ExecutionVerdict.FinishedRebootRequired =>
                (UninstallOutcome.RemovedPendingReboot, "Restart Windows to finish removing it."),
            ExecutionVerdict.Finished or ExecutionVerdict.UnknownProduct when execution.HandedOff =>
                (UninstallOutcome.StillInstalled,
                 $"The uninstaller handed off to another program. If it is still asking for confirmation, finish there, then click Refresh."),
            ExecutionVerdict.Finished or ExecutionVerdict.UnknownProduct =>
                (UninstallOutcome.StillInstalled,
                 $"The uninstaller closed but {appName} is still installed. It may have been cancelled."),
            ExecutionVerdict.Cancelled =>
                (UninstallOutcome.Cancelled, Fallback(execution.Message, "The uninstall was cancelled.")),
            ExecutionVerdict.WaitAbandoned =>
                (UninstallOutcome.Cancelled, Fallback(execution.Message, "Stopped waiting for the uninstaller; it may still be running.")),
            _ =>
                (UninstallOutcome.Failed, Fallback(execution.Message, "The uninstall failed.")),
        };

        static string Fallback(string message, string fallback) =>
            string.IsNullOrWhiteSpace(message) ? fallback : message;
    }

    /// <summary>The app is still registered but nothing can uninstall it any more.</summary>
    private bool UninstallerNowMissing(InstalledApplication app) => app.UninstallerKind switch
    {
        UninstallerKind.StoreApp or UninstallerKind.BrokenEntry => false,
        UninstallerKind.Msi => _msi.QueryProductState(app.ProductCode) == MsiProductStateReader.InstallStateUnknown,
        _ => !string.IsNullOrEmpty(app.UninstallerPath) && !_fs.FileExists(app.UninstallerPath),
    };

    private static UninstallResult Result(InstalledApplication app, UninstallOutcome outcome, string message) => new()
    {
        ApplicationId = app.Id,
        ApplicationName = app.DisplayName,
        ApplicationType = app.ApplicationType,
        UninstallerKind = app.UninstallerKind,
        Outcome = outcome,
        ErrorMessage = message,
    };

    private static UninstallResult Skipped(InstalledApplication app, string message) =>
        Result(app, UninstallOutcome.Skipped, message);

    private static void Report(
        IProgress<UninstallProgress>? progress,
        IReadOnlyList<InstalledApplication> apps,
        int completed,
        UninstallPhase phase,
        string message,
        bool isQuiet = false)
    {
        progress?.Report(new UninstallProgress
        {
            TotalCount = apps.Count,
            CompletedCount = completed,
            CurrentApplicationName = completed < apps.Count ? apps[completed].DisplayName : string.Empty,
            CurrentApplicationId   = completed < apps.Count ? apps[completed].Id : string.Empty,
            Phase = phase,
            StatusMessage = message,
            IsQuiet = isQuiet,
        });
    }
}
