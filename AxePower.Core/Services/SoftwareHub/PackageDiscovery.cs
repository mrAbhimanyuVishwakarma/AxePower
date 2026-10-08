using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;
using Microsoft.Win32;

namespace AxePower.Core.Services.SoftwareHub;

public interface IPackageDiscovery
{
    /// <summary>Registry, protocol and file checks only; no process is started.</summary>
    bool IsInstalledFast(SoftwarePackageItem package);

    /// <summary>Updates <see cref="SoftwarePackageItem.IsInstalled"/> for every package: the fast checks, then one <c>winget list</c>.</summary>
    Task RefreshAsync(IReadOnlyList<SoftwarePackageItem> packages, CancellationToken cancellationToken = default);

    /// <summary>What "Open" should start: a listed protocol or a full .exe path, or null when nothing can be found.</summary>
    string? ResolveLaunchTarget(SoftwarePackageItem package);
}

/// <summary>Finds out which catalog packages are installed and where they start from.</summary>
public sealed class PackageDiscovery : IPackageDiscovery
{
    public static string DefaultToolsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Tools");

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    private readonly IProcessRunner _runner;
    private readonly LogService? _log;
    private readonly string _toolsRoot;

    public PackageDiscovery(IProcessRunner runner, LogService? log = null, string? toolsRoot = null)
    {
        _runner = runner;
        _log = log;
        _toolsRoot = toolsRoot ?? DefaultToolsRoot;
    }

    public bool IsInstalledFast(SoftwarePackageItem package) => CheckIsInstalledFast(package, _toolsRoot);

    public async Task RefreshAsync(IReadOnlyList<SoftwarePackageItem> packages, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() =>
            {
                foreach (var pkg in packages)
                    pkg.IsInstalled = !pkg.IsWebLinkOnly && IsInstalledFast(pkg);
            }, cancellationToken).ConfigureAwait(false);

            var output = new StringBuilder();
            var result = await _runner.RunAsync(new ProcessCommand(AllowedTool.WinGet, new[] { "list" }, ListTimeout),
                line => { if (!line.IsError) output.AppendLine(line.Text); }, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ProcessRunOutcome.Exited)
                ApplyWinGetList(packages, output.ToString());
            else
                _log?.LogWarning($"[SoftwareHub] winget list did not finish ({result.Outcome}); using the fast checks only.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[SoftwareHub] Failed to refresh package list: {ex.Message}");
        }
    }

    /// <summary>Marks packages that <c>winget list</c> output mentions by WinGet id or name.</summary>
    public static void ApplyWinGetList(IEnumerable<SoftwarePackageItem> packages, string listOutput)
    {
        if (string.IsNullOrWhiteSpace(listOutput)) return;
        foreach (var pkg in packages)
        {
            if (pkg.IsWebLinkOnly || pkg.IsInstalled || string.IsNullOrEmpty(pkg.WinGetId)) continue;
            if (listOutput.Contains(pkg.WinGetId, StringComparison.OrdinalIgnoreCase) ||
                listOutput.Contains(pkg.Name, StringComparison.OrdinalIgnoreCase))
                pkg.IsInstalled = true;
        }
    }

    public string? ResolveLaunchTarget(SoftwarePackageItem package)
    {
        var target = package.LaunchProtocolOrExe;
        if (string.IsNullOrEmpty(target)) return null;
        if (target.Contains("://", StringComparison.Ordinal)) return target;
        if (File.Exists(target)) return target;
        return FindInAppPaths(Path.GetFileName(target));
    }

    /// <summary>The registered full path for an exe name under App Paths (what the shell used to resolve a bare name).</summary>
    public static string? FindInAppPaths(string exeName)
    {
        if (string.IsNullOrEmpty(exeName) || exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (key?.GetValue(null) is string value)
                {
                    var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
                }
            }
            catch (Exception) { /* Unreadable App Paths entry: the next hive is tried, then "not found". */ }
        }
        return null;
    }

    public static bool CheckIsInstalledFast(SoftwarePackageItem pkg, string? toolsRoot = null)
    {
        try
        {
            // Direct file existence check if specified
            if (!string.IsNullOrEmpty(pkg.LaunchProtocolOrExe) && !pkg.LaunchProtocolOrExe.Contains("://"))
            {
                if (File.Exists(pkg.LaunchProtocolOrExe)) return true;
            }

            // Check protocol registration (e.g. steam://, com.epicgames.launcher://, uplay://, rockstargames://, battlenet://)
            if (!string.IsNullOrEmpty(pkg.LaunchProtocolOrExe) && pkg.LaunchProtocolOrExe.EndsWith("://"))
            {
                string proto = pkg.LaunchProtocolOrExe.TrimEnd('/', ':');
                using var key = Registry.ClassesRoot.OpenSubKey(proto);
                if (key != null) return true;
            }

            // Check well-known registry entries for launchers & tools
            switch (pkg.Id)
            {
                case "steam":
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                        if (k != null) return true;
                    break;
                case "epic":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher"))
                        if (k != null) return true;
                    break;
                case "rockstar":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Launcher"))
                        if (k != null) return true;
                    break;
                case "ubisoft":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher"))
                        if (k != null) return true;
                    break;
                case "battlenet":
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\Battle.net"))
                        if (k != null) return true;
                    break;
                case "afterburner":
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\MSI\Afterburner"))
                        if (k != null) return true;
                    break;
                case "camstudio":
                    string[] camCandidatePaths = new[]
                    {
                        Path.Combine(toolsRoot ?? DefaultToolsRoot, "CamStudio", "Recorder.exe"),
                        Path.Combine(toolsRoot ?? DefaultToolsRoot, "CamStudio", "CamStudio.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "CamStudio", "Recorder.exe"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CamStudio", "Recorder.exe")
                    };
                    // The official installer uses a versioned folder (e.g. "CamStudio 2.7"), so
                    // fall back to the uninstall entries and any "CamStudio*" Program Files folder
                    string? camFound = camCandidatePaths.FirstOrDefault(File.Exists) ?? FindCamStudioRecorder();
                    if (camFound != null)
                    {
                        pkg.LaunchProtocolOrExe = camFound;
                        return true;
                    }
                    break;
                case "dlss5swapper":
                    string dlssExe = Path.Combine(toolsRoot ?? DefaultToolsRoot, PortableTools.Dlss5SwapperFolder, PortableTools.Dlss5SwapperExe);
                    if (File.Exists(dlssExe))
                    {
                        pkg.LaunchProtocolOrExe = dlssExe;
                        return true;
                    }
                    break;
            }
        }
        catch (Exception caught) when (ExpectedFailures.Registry(caught)) { /* Unreadable: the package is shown as not installed. */ }

        return false;
    }

    /// <summary>Finds CamStudio's Recorder.exe from its uninstall entry or a "CamStudio*" folder under Program Files.</summary>
    private static string? FindCamStudioRecorder()
    {
        var uninstallRoots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, path) in uninstallRoots)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string displayName ||
                        !displayName.Contains("CamStudio", StringComparison.OrdinalIgnoreCase)) continue;

                    if (entry.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
                    {
                        string exe = Path.Combine(location.Trim('"'), "Recorder.exe");
                        if (File.Exists(exe)) return exe;
                    }
                    if (entry.GetValue("DisplayIcon") is string icon)
                    {
                        string exe = icon.Split(',')[0].Trim('"');
                        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe)) return exe;
                    }
                }
            }
            catch (Exception caught) when (ExpectedFailures.Registry(caught)) { /* Unreadable uninstall entries: fall back to searching Program Files. */ }
        }

        foreach (var programFiles in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            try
            {
                string dir = Environment.GetFolderPath(programFiles);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var camDir in Directory.EnumerateDirectories(dir, "CamStudio*"))
                {
                    string exe = Path.Combine(camDir, "Recorder.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Unreadable Program Files: CamStudio is shown as not installed. */ }
        }

        return null;
    }
}
