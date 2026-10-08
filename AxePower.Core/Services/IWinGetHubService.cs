using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;

namespace AxePower.Core.Services;

public interface IWinGetHubService
{
    IReadOnlyList<SoftwarePackageItem> GetAvailablePackages();
    Task RefreshInstallationStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>Plans and runs one action, reporting output lines; cancelling ends the running process tree.</summary>
    Task<PackageOperationResult> RunAsync(PackageAction action, string id, IProgress<string> outputProgress, CancellationToken cancellationToken = default);

    /// <summary>Installs packages one after another. A failure does not stop the rest; cancelling stops the current one and skips the remainder.</summary>
    Task<PackageBatchResult> InstallManyAsync(IReadOnlyList<string> ids, IProgress<string> outputProgress,
        IProgress<PackageBatchProgress>? batchProgress = null, CancellationToken cancellationToken = default);

    Task<bool> InstallPackageAsync(string wingetId, IProgress<string> outputProgress, CancellationToken cancellationToken = default);
    Task<bool> UpgradePackageAsync(string wingetId, IProgress<string> outputProgress, CancellationToken cancellationToken = default);
    Task<bool> UninstallPackageAsync(string wingetId, IProgress<string> outputProgress, CancellationToken cancellationToken = default);
    Task<bool> UpgradeAllPackagesAsync(IProgress<string> outputProgress, CancellationToken cancellationToken = default);

    /// <summary>Starts an installed package (its protocol or program) with the user's normal rights.</summary>
    ShellLaunchResult OpenInstalled(SoftwarePackageItem package);

    /// <summary>Opens a web page with the user's normal rights.</summary>
    ShellLaunchResult OpenWebPage(string url);

    /// <summary>Starts a package's bundled installer. Not found: the result offers its web page.</summary>
    PackageOperationResult LaunchLocalInstaller(SoftwarePackageItem package);
}
