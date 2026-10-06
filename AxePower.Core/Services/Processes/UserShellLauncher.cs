using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AxePower.Core.Services.Processes;

/// <summary>Which rights a launched program should have.</summary>
public enum RunAs
{
    /// <summary>Whatever AxePower has (winget, installers that elevate themselves).</summary>
    CurrentProcess,
    /// <summary>The signed-in user's normal rights: browsers, apps, downloaded tools.</summary>
    StandardUser,
    /// <summary>Must be elevated (Windows Update policy, restore points).</summary>
    Administrator
}

public enum LaunchRoute
{
    Direct,
    /// <summary>Handed to Explorer, which starts it with the user's normal rights.</summary>
    ViaUserShell,
    Refuse
}

public static class ElevationPolicy
{
    /// <summary>How to start something that needs <paramref name="need"/> when AxePower's elevation is <paramref name="processElevated"/>.</summary>
    public static LaunchRoute Decide(RunAs need, bool processElevated) => need switch
    {
        RunAs.StandardUser => processElevated ? LaunchRoute.ViaUserShell : LaunchRoute.Direct,
        RunAs.Administrator => processElevated ? LaunchRoute.Direct : LaunchRoute.Refuse,
        _ => LaunchRoute.Direct
    };
}

public enum ShellLaunchStatus
{
    Started,
    /// <summary>The target failed validation and was not started.</summary>
    Refused,
    Failed
}

public sealed record ShellLaunchResult(ShellLaunchStatus Status, string Message)
{
    public bool Started => Status == ShellLaunchStatus.Started;
}

public interface IUserShellLauncher
{
    /// <summary>Opens a web page or a registered app protocol (steam://, ms-windows-store://...) with the user's normal rights.</summary>
    ShellLaunchResult OpenUri(string target);

    /// <summary>Starts a program by its full path with the user's normal rights.</summary>
    ShellLaunchResult OpenProgram(string path);

    /// <summary>Starts an installer by its full path with AxePower's rights; installers elevate themselves when needed.</summary>
    ShellLaunchResult RunInstaller(string path);
}

/// <summary>
/// Opens pages, protocols and programs without a shell command line. When AxePower is elevated,
/// the target is handed to Explorer as one argument, and Explorer starts it as the signed-in user;
/// if that hand-off fails, nothing is started elevated instead. Every target is validated first:
/// only listed URI schemes, http(s) with a host and no user info, fully qualified local .exe paths,
/// and no quote or comma (Explorer would read a comma as a switch separator).
/// </summary>
public sealed class UserShellLauncher : IUserShellLauncher
{
    /// <summary>Web, Microsoft Store and the game launchers the Software Hub opens.</summary>
    public static readonly IReadOnlySet<string> DefaultSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "ms-windows-store",
        "steam", "com.epicgames.launcher", "rockstargames", "uplay", "battlenet", "goggalaxy", "origin2", "discord"
    };

    private readonly IReadOnlySet<string> _schemes;
    private readonly Func<bool> _isElevated;
    private readonly ExecutableAllowList _allowList;
    private readonly Action<ProcessStartInfo> _start;
    private readonly LogService? _log;

    public UserShellLauncher(LogService? log = null, ExecutableAllowList? allowList = null, Func<bool>? isElevated = null,
        Action<ProcessStartInfo>? start = null, IReadOnlySet<string>? schemes = null)
    {
        _log = log;
        _isElevated = isElevated ?? TerminalService.IsProcessElevated;
        _allowList = allowList ?? ExecutableAllowList.Windows(_isElevated);
        _start = start ?? (psi => { using var _ = Process.Start(psi); });
        _schemes = schemes ?? DefaultSchemes;
    }

    public ShellLaunchResult OpenUri(string target)
    {
        if (!TryValidateUri(target, _schemes, out var normalized, out var reason)) return Refuse(target, reason);
        return Launch(normalized, RunAs.StandardUser, workingDirectory: null);
    }

    public ShellLaunchResult OpenProgram(string path)
    {
        if (!TryValidateProgram(path, out var reason)) return Refuse(path, reason);
        return Launch(path, RunAs.StandardUser, Path.GetDirectoryName(path));
    }

    public ShellLaunchResult RunInstaller(string path)
    {
        if (!TryValidateProgram(path, out var reason)) return Refuse(path, reason);
        return Launch(path, RunAs.CurrentProcess, Path.GetDirectoryName(path));
    }

    /// <summary>Accepts an absolute URI whose scheme is listed; web addresses need a host and no user info.</summary>
    public static bool TryValidateUri(string? target, IReadOnlySet<string> schemes, out string normalized, out string reason)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(target)) { reason = "no address"; return false; }
        if (target.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or ',' or '<' or '>' or '`' or '^' or '|'))
        {
            reason = "the address contains characters that are not allowed";
            return false;
        }
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) { reason = "not an absolute address"; return false; }
        if (!schemes.Contains(uri.Scheme)) { reason = $"the {uri.Scheme}: scheme is not allowed"; return false; }
        if (uri.Scheme is "http" or "https")
        {
            if (string.IsNullOrEmpty(uri.Host)) { reason = "the address has no host"; return false; }
            if (!string.IsNullOrEmpty(uri.UserInfo)) { reason = "the address carries a user name"; return false; }
            normalized = uri.AbsoluteUri;
        }
        else
        {
            normalized = target; // custom protocols keep their exact form (steam:// has no host)
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Accepts a fully qualified local path to an existing .exe.</summary>
    public static bool TryValidateProgram(string? path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path)) { reason = "no program"; return false; }
        if (path.Any(c => char.IsControl(c) || c is '"' or ',' or '*' or '?' or '<' or '>' or '|'))
        {
            reason = "the path contains characters that are not allowed";
            return false;
        }
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) { reason = "not a full local path"; return false; }
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) { reason = "not a program (.exe)"; return false; }
        if (!File.Exists(path)) { reason = "the program is not there"; return false; }
        reason = string.Empty;
        return true;
    }

    private ShellLaunchResult Launch(string target, RunAs need, string? workingDirectory)
    {
        var route = ElevationPolicy.Decide(need, _isElevated());
        try
        {
            switch (route)
            {
                case LaunchRoute.ViaUserShell:
                    var explorer = _allowList.Resolve(AllowedTool.Explorer);
                    if (explorer is null) return Fail(target, "Explorer was not found, so it was not started without administrator rights");
                    var viaShell = new ProcessStartInfo(explorer) { UseShellExecute = false };
                    viaShell.ArgumentList.Add(target);
                    _start(viaShell);
                    break;
                case LaunchRoute.Direct:
                    var direct = new ProcessStartInfo(target) { UseShellExecute = true };
                    if (workingDirectory != null) direct.WorkingDirectory = workingDirectory;
                    _start(direct);
                    break;
                default:
                    return Refuse(target, "it needs administrator rights");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return Fail(target, ex.Message);
        }

        _log?.LogInfo($"[Launch] Started {target} ({(route == LaunchRoute.ViaUserShell ? "as the signed-in user" : "directly")}).");
        return new ShellLaunchResult(ShellLaunchStatus.Started, route == LaunchRoute.ViaUserShell ? "Started without administrator rights." : "Started.");
    }

    private ShellLaunchResult Refuse(string? target, string reason)
    {
        _log?.LogWarning($"[Launch] Refused '{target}': {reason}.");
        return new ShellLaunchResult(ShellLaunchStatus.Refused, $"Not opened: {reason}.");
    }

    private ShellLaunchResult Fail(string target, string reason)
    {
        _log?.LogWarning($"[Launch] Could not start '{target}': {reason}.");
        return new ShellLaunchResult(ShellLaunchStatus.Failed, $"Could not open it: {reason}.");
    }
}
