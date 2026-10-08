using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;

namespace AxePower.Tests.SoftwareHub;

/// <summary>Answers each command from a script instead of starting anything, and records what it was asked to run.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    private readonly Func<ProcessCommand, CancellationToken, Task<ProcessRunResult>> _answer;

    public FakeRunner(Func<ProcessCommand, ProcessRunResult>? answer = null)
        : this((c, _) => Task.FromResult(answer?.Invoke(c) ?? new ProcessRunResult(ProcessRunOutcome.Exited, 0))) { }

    public FakeRunner(Func<ProcessCommand, CancellationToken, Task<ProcessRunResult>> answer) => _answer = answer;

    public ConcurrentQueue<ProcessCommand> Commands { get; } = new();

    public IEnumerable<string> Verbs => Commands.Select(c => string.Join(' ', c.Arguments.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))));

    public async Task<ProcessRunResult> RunAsync(ProcessCommand command, Action<ProcessOutputLine>? output = null, CancellationToken cancellationToken = default)
    {
        Commands.Enqueue(command);
        output?.Invoke(new ProcessOutputLine("fake output for " + command.Arguments.FirstOrDefault(), false));
        return await _answer(command, cancellationToken);
    }

    /// <summary>A runner that waits until cancelled, as a hung installer would.</summary>
    public static FakeRunner Hanging(TaskCompletionSource? started = null) => new(async (_, ct) =>
    {
        started?.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        return new ProcessRunResult(ProcessRunOutcome.Cancelled);
    });
}

/// <summary>Records launches; never opens a browser or a program.</summary>
internal sealed class FakeLauncher : IUserShellLauncher
{
    public List<(string Kind, string Target)> Launches { get; } = new();

    public ShellLaunchResult OpenUri(string target) { Launches.Add(("uri", target)); return new ShellLaunchResult(ShellLaunchStatus.Started, "ok"); }
    public ShellLaunchResult OpenProgram(string path) { Launches.Add(("program", path)); return new ShellLaunchResult(ShellLaunchStatus.Started, "ok"); }
    public ShellLaunchResult RunInstaller(string path) { Launches.Add(("installer", path)); return new ShellLaunchResult(ShellLaunchStatus.Started, "ok"); }
    public ShellLaunchResult OpenFolder(string path) { Launches.Add(("folder", path)); return new ShellLaunchResult(ShellLaunchStatus.Started, "ok"); }
}

internal sealed class FakeDownloader : IPortableToolDownloader
{
    public Func<string, string?> Result { get; set; } = folder => Path.Combine(folder, "tool.exe");
    public List<string> Folders { get; } = new();

    public Task<string?> DownloadAsync(PortableToolSpec spec, string folder, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        Folders.Add(folder);
        return Task.FromResult(Result(folder));
    }
}

internal sealed class FakeDiscovery : IPackageDiscovery
{
    public HashSet<string> InstalledIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsInstalledFast(SoftwarePackageItem package) => InstalledIds.Contains(package.Id);
    public Task RefreshAsync(IReadOnlyList<SoftwarePackageItem> packages, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public string? ResolveLaunchTarget(SoftwarePackageItem package) => string.IsNullOrEmpty(package.LaunchProtocolOrExe) ? null : package.LaunchProtocolOrExe;
}

/// <summary>Collects progress lines synchronously (Progress&lt;T&gt; would post them later).</summary>
internal sealed class LineSink : IProgress<string>
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public void Report(string value) => Lines.Enqueue(value);
    public string All => string.Join("\n", Lines);
}

/// <summary>A hub wired to fakes and a temporary tools folder, so nothing touches the machine.</summary>
internal sealed class HubRig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "AxePower_HubRig_" + Guid.NewGuid().ToString("N"));
    public string ToolsRoot => Path.Combine(Root, "Tools");
    public string AppDirectory => Path.Combine(Root, "App");
    public FakeRunner Runner { get; }
    public FakeLauncher Launcher { get; } = new();
    public FakeDownloader Downloader { get; } = new();
    public FakeDiscovery Discovery { get; } = new();
    public SoftwareCatalog Catalog { get; }
    public InstallPlanner Planner { get; }
    public WinGetHubService Hub { get; }

    public HubRig(FakeRunner? runner = null, IEnumerable<SoftwarePackageItem>? items = null)
    {
        Directory.CreateDirectory(ToolsRoot);
        Directory.CreateDirectory(AppDirectory);
        Runner = runner ?? new FakeRunner();
        Catalog = new SoftwareCatalog(items ?? SoftwareCatalog.CreateDefaultItems());
        Planner = new InstallPlanner(Catalog, ToolsRoot, AppDirectory);
        Hub = new WinGetHubService(new LogService(), Catalog, Runner, Launcher, Downloader, Discovery, Planner);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

/// <summary>Real processes for runner tests: Windows PowerShell from System32, nothing else.</summary>
internal static class RealTools
{
    public static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>An allow-list where every tool is PowerShell, so a runner test can stand in for winget.</summary>
    public static ExecutableAllowList PowerShellOnly() =>
        ExecutableAllowList.FromPaths(Enum.GetValues<AllowedTool>().ToDictionary(t => t, _ => PowerShell));

    public static ProcessCommand Script(string script, TimeSpan? timeout = null) =>
        new(AllowedTool.PowerShell, new[] { "-NoProfile", "-NonInteractive", "-Command", script }, timeout ?? TimeSpan.FromSeconds(60));

    public static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void KillQuietly(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
    }
}
