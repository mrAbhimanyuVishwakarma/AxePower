using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Answers "is this app still installed?" from the registry, Windows Installer and AppX.</summary>
public interface IInstallStateVerifier
{
    /// <summary>
    /// True while any of the app's uninstall keys still exists, its MSI product is still
    /// registered, or its Store package is still listed.
    /// </summary>
    Task<bool> IsStillInstalledAsync(InstalledApplication app, CancellationToken cancellationToken);

    /// <summary>True while any of the app's uninstall keys exists (Store apps: the package is listed).</summary>
    Task<bool> IsRegisteredAsync(InstalledApplication app, CancellationToken cancellationToken);

    /// <summary>
    /// Polls until the app is gone or <paramref name="timeout"/> passes; some uninstallers remove
    /// their key last. Returns the final "still installed" answer.
    /// </summary>
    Task<bool> WaitUntilRemovedAsync(InstalledApplication app, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class InstallStateVerifier : IInstallStateVerifier
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private readonly IMsiProductStateReader _msi;
    private readonly IFileSystemProbe _fs;

    public InstallStateVerifier() : this(new MsiProductStateReader(), new FileSystemProbe()) { }

    public InstallStateVerifier(IMsiProductStateReader msi, IFileSystemProbe fs)
    {
        _msi = msi;
        _fs = fs;
    }

    public async Task<bool> IsStillInstalledAsync(InstalledApplication app, CancellationToken cancellationToken)
    {
        if (app.UninstallerKind == UninstallerKind.StoreApp)
            return await IsPackageListedAsync(app.PackageFullName, cancellationToken).ConfigureAwait(false);

        // Nothing to check against: never report a removal we can't prove
        if (string.IsNullOrEmpty(app.RegistryKeyPath) && app.UninstallerKind != UninstallerKind.Msi)
            return true;

        if (RegistryPath.KeyExists(app.RegistryKeyPath))
            return true;

        // A second registration of the same product only keeps it "installed" while it still has
        // a working uninstaller; a stale one is a leftover, offered in the review.
        if (app.RelatedKeyPaths.Any(IsWorkingEntry))
            return true;

        // Only -1 (not registered) counts as removed for an MSI product
        return app.UninstallerKind == UninstallerKind.Msi &&
               MsiProductStateReader.IsProductCode(app.ProductCode) &&
               _msi.QueryProductState(app.ProductCode) != MsiProductStateReader.InstallStateUnknown;
    }

    public async Task<bool> IsRegisteredAsync(InstalledApplication app, CancellationToken cancellationToken)
    {
        if (app.UninstallerKind == UninstallerKind.StoreApp)
            return await IsPackageListedAsync(app.PackageFullName, cancellationToken).ConfigureAwait(false);
        return app.AllRegistryKeyPaths.Any(RegistryPath.KeyExists);
    }

    public async Task<bool> WaitUntilRemovedAsync(InstalledApplication app, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Each Store check starts PowerShell, so ask once rather than twice a second
        if (app.UninstallerKind == UninstallerKind.StoreApp)
            return await IsStillInstalledAsync(app, cancellationToken).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var installed = await IsStillInstalledAsync(app, cancellationToken).ConfigureAwait(false);
            if (!installed || DateTime.UtcNow >= deadline) return installed;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsWorkingEntry(string keyPath)
    {
        try
        {
            using var key = RegistryPath.OpenKey(keyPath);
            if (key is null) return false;
            var name = keyPath[(keyPath.LastIndexOf('\\') + 1)..];
            var entry = UninstallEntryClassifier.Classify(UninstallEntryValues.FromRegistryKey(key), name, keyPath, _fs, _msi);
            return entry is not null && entry.UninstallerKind != UninstallerKind.BrokenEntry;
        }
        catch
        {
            return true;
        }
    }

    private static async Task<bool> IsPackageListedAsync(string packageFullName, CancellationToken cancellationToken)
    {
        // The name is validated, so it is safe inside a single-quoted PowerShell string
        if (!UninstallCommandBuilder.IsValidPackageFullName(packageFullName)) return false;

        var script = $"if (Get-AppxPackage | Where-Object PackageFullName -eq '{packageFullName}') {{ 'present' }} else {{ 'absent' }}";
        var result = await PowerShellRunner.RunAsync(script, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        // If PowerShell failed we can't claim the package is gone
        if (result.TimedOut || !result.StandardOutput.Contains("absent", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
