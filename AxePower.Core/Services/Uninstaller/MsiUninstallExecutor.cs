using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Uninstalls MSI products with <c>msiexec /x {ProductCode}</c> (basic UI, or silent when quiet
/// mode is on), always with <c>/norestart</c> and a verbose log. Waits for msiexec and its custom
/// actions, and retries twice when another installation is in progress (1618).
/// </summary>
public sealed class MsiUninstallExecutor : IUninstallExecutor
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 3;

    private readonly IFileSystemProbe _fs;
    private readonly IProcessTreeWatcher _watcher;

    public MsiUninstallExecutor() : this(new FileSystemProbe(), new ProcessTreeWatcher()) { }

    public MsiUninstallExecutor(IFileSystemProbe fs, IProcessTreeWatcher watcher)
    {
        _fs = fs;
        _watcher = watcher;
    }

    public IReadOnlyCollection<UninstallerKind> SupportedKinds { get; } = [UninstallerKind.Msi];

    public async Task<UninstallExecution> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        UninstallRunContext context,
        CancellationToken cancellationToken = default)
    {
        const string method = "MSI";

        if (settings.DryRunMode)
            return UninstallerLauncher.DryRun(UninstallCommandBuilder.Build(application, settings.PreferQuietUninstall, _fs), method);

        UninstallExecution execution = null!;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var plan = UninstallCommandBuilder.Build(application, settings.PreferQuietUninstall, _fs, CreateLogPath(application));
            if (!plan.CanRun) return UninstallerLauncher.PlanError(plan, method);

            execution = await UninstallerLauncher.RunAsync(plan, application, settings, _watcher, context, method, cancellationToken)
                .ConfigureAwait(false);

            if (execution.Verdict != ExecutionVerdict.Busy) return execution;
            if (attempt < MaxAttempts)
                await Task.Delay(BusyRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return new UninstallExecution
        {
            Verdict = ExecutionVerdict.Failed,
            ExitCode = execution.ExitCode,
            Message = "Another installation kept Windows Installer busy (1618). Try again when it has finished.",
            CommandExecuted = execution.CommandExecuted,
            Method = method,
            WasQuiet = execution.WasQuiet,
            LogPath = execution.LogPath,
            Duration = execution.Duration,
        };
    }

    /// <summary>Validates MSI ProductCode format: {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</summary>
    public static bool IsValidProductCode(string productCode) =>
        MsiProductStateReader.IsProductCode(productCode);

    private static string CreateLogPath(InstalledApplication app)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AxePower", "UninstallLogs");
        Directory.CreateDirectory(logDir);
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(app.DisplayName.Where(c => !invalid.Contains(c))).Replace(' ', '_');
        return Path.Combine(logDir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }
}
