using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Services;
using Microsoft.Win32;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Orchestrates sequential batch uninstallation of multiple applications.
/// Never launches uninstallers simultaneously.
/// Failure of one application does not abort the remaining queue.
/// </summary>
public sealed class BatchUninstallService : IBatchUninstallService
{
    private readonly UninstallExecutorFactory _executorFactory;
    private readonly ILeftoverScannerService _leftoverScanner;
    private readonly IRegistryBackupService _registryBackup;
    private readonly IUninstallHistoryService _historyService;
    private readonly SystemRestoreService _systemRestore;
    private readonly ProcessDetectionService _processDetection;
    private readonly LogService _log;

    public BatchUninstallService(
        UninstallExecutorFactory executorFactory,
        ILeftoverScannerService leftoverScanner,
        IRegistryBackupService registryBackup,
        IUninstallHistoryService historyService,
        SystemRestoreService systemRestore,
        ProcessDetectionService processDetection,
        LogService log)
    {
        _executorFactory = executorFactory;
        _leftoverScanner = leftoverScanner;
        _registryBackup  = registryBackup;
        _historyService  = historyService;
        _systemRestore   = systemRestore;
        _processDetection = processDetection;
        _log             = log;
    }

    public async Task<UninstallSession> ExecuteBatchAsync(
        IReadOnlyList<InstalledApplication> applications,
        UninstallerSettings settings,
        IProgress<UninstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var session = new UninstallSession
        {
            StartTime  = DateTime.Now,
            ScanMode   = settings.DefaultScanMode.ToString(),
            WasDryRun  = settings.DryRunMode,
            ApplicationNames = applications.Select(a => a.DisplayName).ToList(),
        };

        _log.LogInfo($"[Uninstaller] Starting batch session {session.SessionId} — {applications.Count} apps.");

        // ── Step 1: System Restore Point ────────────────────────────────────
        if (settings.CreateSystemRestorePoint && !settings.DryRunMode)
        {
            Report(progress, applications, 0, UninstallPhase.CreatingRestorePoint,
                "Creating System Restore Point...", cancellationToken);

            session.SystemRestorePointCreated =
                await _systemRestore.TryCreateRestorePointAsync(
                    $"AxePower Uninstaller Pro — {applications.Count} apps");
        }

        // ── Step 2: Process each application sequentially ───────────────────
        for (int i = 0; i < applications.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var app = applications[i];
            _log.LogInfo($"[Uninstaller] [{i + 1}/{applications.Count}] Processing: {app.DisplayName}");

            Report(progress, applications, i, UninstallPhase.PreFlight,
                $"Preparing to uninstall {app.DisplayName}...", cancellationToken);

            // ── Pre-flight: check running state ─────────────────────────────
            Report(progress, applications, i, UninstallPhase.CheckingProcess,
                $"Checking if {app.DisplayName} is running...", cancellationToken);

            app.IsRunning = _processDetection.IsApplicationRunning(app);
            if (app.IsRunning)
                _log.LogWarning($"[Uninstaller] {app.DisplayName} is currently running.");

            // ── Execute uninstaller ──────────────────────────────────────────
            Report(progress, applications, i, UninstallPhase.Uninstalling,
                $"Uninstalling {app.DisplayName}...", cancellationToken);

            UninstallResult result;
            var executor = _executorFactory.GetExecutor(app);

            if (executor is null)
            {
                // Orphaned — no native uninstaller, go straight to leftover scan
                _log.LogInfo($"[Uninstaller] {app.DisplayName} is orphaned — skipping native uninstaller.");
                result = new UninstallResult
                {
                    ApplicationId = app.Id,
                    ApplicationName = app.DisplayName,
                    Success = true,
                    WasDryRun = settings.DryRunMode,
                    UninstallMethod = "ForcedOrphan",
                    CommandExecuted = "[No native uninstaller — proceeding to leftover scan]",
                };
            }
            else
            {
                try
                {
                    result = await executor.ExecuteAsync(app, settings, cancellationToken);
                    _log.LogInfo(
                        $"[Uninstaller] {app.DisplayName} — {result.UninstallMethod} " +
                        $"exit={result.ExitCode} success={result.Success} " +
                        $"duration={result.Duration.TotalSeconds:F1}s");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError($"[Uninstaller] {app.DisplayName} — executor threw: {ex.Message}");
                    result = new UninstallResult
                    {
                        ApplicationId = app.Id,
                        ApplicationName = app.DisplayName,
                        Success = false,
                        ErrorMessage = ex.Message,
                        UninstallMethod = executor.SupportedType.ToString(),
                    };
                }
            }

            session.Results.Add(result);

            // ── Leftover scan ────────────────────────────────────────────────
            if (settings.AutoScanLeftovers && (result.Success || executor is null))
            {
                Report(progress, applications, i, UninstallPhase.ScanningLeftovers,
                    $"Scanning leftovers for {app.DisplayName}...", cancellationToken);

                try
                {
                    var leftovers = await _leftoverScanner.ScanAsync(
                        app, settings.DefaultScanMode, settings, cancellationToken);

                    session.LeftoverItems.AddRange(leftovers);
                    _log.LogInfo(
                        $"[Uninstaller] {app.DisplayName} — {leftovers.Count} leftover items found.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogError($"[Uninstaller] Leftover scan failed for {app.DisplayName}: {ex.Message}");
                }
            }
        }

        // ── Finalize session ─────────────────────────────────────────────────
        session.EndTime = DateTime.Now;
        await _historyService.SaveSessionAsync(session);

        _log.LogInfo(
            $"[Uninstaller] Session {session.SessionId} complete — " +
            $"{session.SuccessCount} ok, {session.FailedCount} failed, " +
            $"{session.LeftoverItems.Count} leftovers detected.");

        Report(progress, applications, applications.Count, UninstallPhase.Complete,
            "Batch uninstall complete.", cancellationToken);

        return session;
    }

    private static void Report(
        IProgress<UninstallProgress>? progress,
        IReadOnlyList<InstalledApplication> apps,
        int completed,
        UninstallPhase phase,
        string message,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested || progress is null) return;
        progress.Report(new UninstallProgress
        {
            TotalCount = apps.Count,
            CompletedCount = completed,
            CurrentApplicationName = completed < apps.Count ? apps[completed].DisplayName : string.Empty,
            CurrentApplicationId   = completed < apps.Count ? apps[completed].Id : string.Empty,
            Phase = phase,
            StatusMessage = message,
        });
    }
}
