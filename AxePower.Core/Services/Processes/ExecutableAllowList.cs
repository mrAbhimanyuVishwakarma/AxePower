using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AxePower.Core.Services.Processes;

/// <summary>The only programs AxePower runs without a shell. Each maps to one fixed location.</summary>
public enum AllowedTool
{
    WinGet,
    PowerShell,
    UsoClient,
    Explorer
}

/// <summary>
/// Resolves an <see cref="AllowedTool"/> to a fully qualified path. Nothing is looked up on
/// <c>PATH</c> or in the working directory, so a planted <c>winget.exe</c> cannot stand in.
/// When AxePower is elevated, WinGet comes from its package folder under Program Files (writable
/// only by the system), never from the per-user alias folder that any process of the user can write.
/// </summary>
public sealed class ExecutableAllowList
{
    private const string AppInstallerFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    private readonly IReadOnlyDictionary<AllowedTool, Func<string?>> _locate;

    public ExecutableAllowList(IReadOnlyDictionary<AllowedTool, Func<string?>> locate) => _locate = locate;

    /// <summary>Fixed paths, for tests that point a tool at a harmless stand-in.</summary>
    public static ExecutableAllowList FromPaths(IReadOnlyDictionary<AllowedTool, string> paths) =>
        new(paths.ToDictionary(p => p.Key, p => (Func<string?>)(() => p.Value)));

    /// <summary>The real locations on this PC.</summary>
    public static ExecutableAllowList Windows(Func<bool>? isElevated = null) => new(new Dictionary<AllowedTool, Func<string?>>
    {
        [AllowedTool.WinGet] = () => LocateWinGet((isElevated ?? TerminalService.IsProcessElevated)()),
        [AllowedTool.PowerShell] = () => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        [AllowedTool.UsoClient] = () => Path.Combine(Environment.SystemDirectory, "usoclient.exe"),
        [AllowedTool.Explorer] = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
    });

    /// <summary>The tool's full path, or null when it is not allowed, not fully qualified, or not there.</summary>
    public string? Resolve(AllowedTool tool)
    {
        if (!_locate.TryGetValue(tool, out var locate)) return null;
        string? path;
        try { path = locate(); }
        catch (Exception) { /* Fails closed: a locator that throws means the tool counts as absent and is not run. */ return null; }

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? path : null;
    }

    private static string? LocateWinGet(bool elevated)
    {
        var packaged = FindPackagedWinGet();
        if (packaged != null) return packaged;
        if (elevated) return null; // the alias folder is user-writable: not something an elevated process runs

        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    private static string? FindPackagedWinGet()
    {
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            return manager.FindPackagesForUser(string.Empty, AppInstallerFamily)
                .OrderByDescending(p => new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision))
                .Select(p => Path.Combine(p.InstalledLocation.Path, "winget.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception)
        {
            // Package API unavailable (no AppX support): only the non-elevated alias fallback remains.
            return null;
        }
    }
}
