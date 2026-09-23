using System.Diagnostics;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Uninstalls AppX/MSIX packages using PowerShell Remove-AppxPackage.
/// Never deletes WindowsApps directory. Respects NonRemovable packages.
/// </summary>
public sealed class AppxUninstallExecutor : IUninstallExecutor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<UninstallerKind> SupportedKinds { get; } = [UninstallerKind.StoreApp];

    public async Task<UninstallExecution> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        UninstallRunContext context,
        CancellationToken cancellationToken = default)
    {
        const string method = "AppX";
        var sw = Stopwatch.StartNew();

        if (!application.CanUninstall)
            return Fail("This package is marked NonRemovable by Windows.");

        // Validated so the name can sit inside a single-quoted PowerShell string
        var plan = UninstallCommandBuilder.Build(application, preferQuiet: true, new FileSystemProbe());
        if (!plan.CanRun) return UninstallerLauncher.PlanError(plan, method);
        if (settings.DryRunMode) return UninstallerLauncher.DryRun(plan, method);

        context.OnLaunched?.Invoke(plan);
        var result = await PowerShellRunner.RunAsync(plan.Arguments, Timeout, cancellationToken).ConfigureAwait(false);

        return new UninstallExecution
        {
            Verdict = result.TimedOut ? ExecutionVerdict.WaitAbandoned
                    : result.ExitCode == 0 ? ExecutionVerdict.Finished
                    : ExecutionVerdict.UnknownError,
            ExitCode = result.TimedOut ? null : result.ExitCode,
            Message = result.TimedOut ? "Remove-AppxPackage did not finish in time."
                    : result.ExitCode == 0 ? string.Empty
                    : FirstLine(result.StandardError),
            CommandExecuted = plan.Arguments,
            Method = method,
            WasQuiet = true,
            Duration = sw.Elapsed,
        };

        UninstallExecution Fail(string message) => new()
        {
            Verdict = ExecutionVerdict.Failed,
            Message = message,
            Method = method,
            Duration = sw.Elapsed,
        };
    }

    public static bool IsValidPackageName(string name) =>
        UninstallCommandBuilder.IsValidPackageFullName(name);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "Remove-AppxPackage reported an error." : line;
    }
}
