using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Runs exe uninstallers (Burn bundles, Inno Setup, NSIS, InstallShield, Squirrel, PowerShell
/// scripts and unidentified exes) through the plan from <see cref="UninstallCommandBuilder"/>,
/// then waits for the uninstaller's whole process tree. Unidentified exes run exactly as
/// registered; switches are never appended to them.
/// </summary>
public sealed class Win32UninstallExecutor : IUninstallExecutor
{
    private readonly IFileSystemProbe _fs;
    private readonly IProcessTreeWatcher _watcher;

    public Win32UninstallExecutor() : this(new FileSystemProbe(), new ProcessTreeWatcher()) { }

    public Win32UninstallExecutor(IFileSystemProbe fs, IProcessTreeWatcher watcher)
    {
        _fs = fs;
        _watcher = watcher;
    }

    public IReadOnlyCollection<UninstallerKind> SupportedKinds { get; } =
    [
        UninstallerKind.Unknown, UninstallerKind.BurnBundle, UninstallerKind.InnoSetup, UninstallerKind.Nsis,
        UninstallerKind.InstallShield, UninstallerKind.Squirrel, UninstallerKind.PowerShell,
    ];

    public async Task<UninstallExecution> ExecuteAsync(
        InstalledApplication application,
        UninstallerSettings settings,
        UninstallRunContext context,
        CancellationToken cancellationToken = default)
    {
        var plan = UninstallCommandBuilder.Build(application, settings.PreferQuietUninstall, _fs);
        var method = UninstallCommandBuilder.KindName(application.UninstallerKind);

        if (settings.DryRunMode) return UninstallerLauncher.DryRun(plan, method);
        if (!plan.CanRun) return UninstallerLauncher.PlanError(plan, method);

        return await UninstallerLauncher.RunAsync(plan, application, settings, _watcher, context, method, cancellationToken)
            .ConfigureAwait(false);
    }
}
