using System.ComponentModel;
using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Starts an <see cref="UninstallPlan"/> and waits for its whole process tree. Shared by the
/// Win32 and MSI executors.
/// </summary>
internal static class UninstallerLauncher
{
    private static readonly TimeSpan HandOffWindow = TimeSpan.FromSeconds(3);

    public static async Task<UninstallExecution> RunAsync(
        UninstallPlan plan,
        InstalledApplication app,
        UninstallerSettings settings,
        IProcessTreeWatcher watcher,
        UninstallRunContext context,
        string method,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            // The uninstaller's own manifest decides whether it needs UAC
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(plan.FileName) ?? string.Empty,
        };

        var launched = DateTime.UtcNow;
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ExitCodeInterpreter.ErrorCancelled)
        {
            return Result(ExecutionVerdict.Cancelled, null, "Administrator permission was refused.");
        }
        catch (Exception ex)
        {
            // Reported: the verdict carries the reason to the page and the history.
            return Result(ExecutionVerdict.Failed, null, $"The uninstaller could not be started: {ex.Message}");
        }

        if (process is null)
        {
            // The shell passed the command to a program that was already running
            return Result(ExecutionVerdict.Finished, null, string.Empty, handedOff: true);
        }

        using (process)
        {
            context.OnLaunched?.Invoke(plan);

            var options = new ProcessWatchOptions(
                IncludeMsiexecChildren: plan.Kind is UninstallerKind.Msi or UninstallerKind.BurnBundle,
                InstallLocation: app.InstallLocation,
                PollInterval: TimeSpan.FromSeconds(1),
                GracePeriod: TimeSpan.FromSeconds(2))
            {
                KillToken = context.KillToken,
                StallAfter = plan.IsQuiet && settings.QuietStallMinutes > 0
                    ? TimeSpan.FromMinutes(settings.QuietStallMinutes)
                    : null,
                OnStalled = plan.IsQuiet ? context.OnQuietStalled : null,
            };

            var tree = await watcher.WaitAsync(process.Id, launched, options, cancellationToken).ConfigureAwait(false);

            int? rootCode = tree.RootExitCode;
            if (rootCode is null)
            {
                try { if (process.HasExited) rootCode = process.ExitCode; } catch (Exception caught) when (ExpectedFailures.Process(caught)) { /* No exit code available: the watcher's verdict stands. */ }
            }

            if (tree.Status == ProcessTreeStatus.WaitAbandoned)
                return Result(ExecutionVerdict.WaitAbandoned, rootCode,
                    "Stopped waiting for the uninstaller; it may still be running.");

            // msiexec's own exit code is authoritative; for the rest, the last process to exit
            // is the real uninstaller (the NSIS temp copy, not its launcher stub).
            var exitCode = plan.Kind is UninstallerKind.Msi or UninstallerKind.BurnBundle
                ? rootCode ?? tree.LastExitCode
                : tree.LastExitCode ?? rootCode;

            if (tree.Status == ProcessTreeStatus.Killed)
                return Result(ExecutionVerdict.Cancelled, exitCode, "The uninstaller was stopped.");

            var (verdict, message) = ExitCodeInterpreter.Interpret(plan.Kind, exitCode);
            var handedOff = tree.ProcessCount <= 1 && tree.Elapsed < HandOffWindow;
            return Result(verdict, exitCode, message, handedOff, tree.KillFailed);
        }

        UninstallExecution Result(ExecutionVerdict verdict, int? exitCode, string message,
            bool handedOff = false, bool stopFailed = false) => new()
        {
            Verdict = verdict,
            ExitCode = exitCode,
            Message = message,
            CommandExecuted = plan.CommandLine,
            Method = method,
            WasQuiet = plan.IsQuiet,
            LogPath = plan.LogPath,
            Duration = stopwatch.Elapsed,
            HandedOff = handedOff,
            StopFailed = stopFailed,
        };
    }

    public static UninstallExecution DryRun(UninstallPlan plan, string method) => new()
    {
        Verdict = ExecutionVerdict.DryRun,
        CommandExecuted = $"[DRY RUN] Would execute: {(plan.CanRun ? plan.CommandLine : plan.Description)}",
        Method = method,
        WasQuiet = plan.IsQuiet,
    };

    public static UninstallExecution PlanError(UninstallPlan plan, string method) => new()
    {
        Verdict = ExecutionVerdict.Failed,
        Message = plan.Error ?? "No uninstall command could be built.",
        Method = method,
    };
}
