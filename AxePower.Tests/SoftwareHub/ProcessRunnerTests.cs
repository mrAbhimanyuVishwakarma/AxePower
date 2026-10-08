using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Processes;
using Xunit;

namespace AxePower.Tests.SoftwareHub;

/// <summary>
/// The runner against real processes (Windows PowerShell from System32 stands in for every tool):
/// arguments arrive exactly as passed, exit codes and output come back, a timeout or a cancel ends
/// the run's whole tree and nothing outside it, and a tool off the allow-list never starts.
/// </summary>
public class ProcessRunnerTests
{
    private readonly AllowListedProcessRunner _runner = new(RealTools.PowerShellOnly());

    [Fact]
    public async Task Arguments_ArriveExactlyAsPassed_NoSplittingQuotingOrExpansion()
    {
        // Values a hostile catalog or id could carry. Each must reach the program as one argv entry.
        var hostile = new[]
        {
            "plain",
            "with space",
            "quote\"inside",
            "\"fully quoted\"",
            "trailing\\",
            "trailing space and slash \\",
            "back\\\\\"slash",
            "a&b|c;d<e>f^g",
            "--override \"/S /D=C:\\evil\"",
            "%PATH%",
            "$(calc)",
            "`n",
            "",
            "ünïcødé ✓",
        };

        var script = Path.Combine(Path.GetTempPath(), "AxePower_EchoArgs_" + Guid.NewGuid().ToString("N") + ".ps1");
        // GetCommandLineArgs re-reads the raw command line with the C runtime's rules, which is
        // how winget (a C++ program) will split it.
        // Each argument comes back base64-encoded, so output decoding cannot blur the comparison.
        File.WriteAllText(script, "[Environment]::GetCommandLineArgs() | Select-Object -Skip 6 | " +
                                  "ForEach-Object { '<' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_)) + '>' }");
        try
        {
            var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
            args.AddRange(hostile);
            var lines = new ConcurrentQueue<string>();
            var result = await _runner.RunAsync(new ProcessCommand(AllowedTool.WinGet, args, TimeSpan.FromSeconds(60)),
                l => { if (!l.IsError) lines.Enqueue(l.Text); });

            Assert.Equal(ProcessRunOutcome.Exited, result.Outcome);
            Assert.Equal(hostile.Select(h => "<" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(h)) + ">"), lines.Where(l => l.StartsWith('<')));
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1978335189)] // winget: no applicable update
    public async Task ExitCode_IsReported(int code)
    {
        var result = await _runner.RunAsync(RealTools.Script($"exit {code}"));
        Assert.Equal(ProcessRunOutcome.Exited, result.Outcome);
        Assert.Equal(code, result.ExitCode);
        Assert.Equal(code == 0, result.ExitedWithAny(0));
    }

    [Fact]
    public async Task Output_IsStreamed_WithErrorsMarked()
    {
        var lines = new ConcurrentQueue<ProcessOutputLine>();
        await _runner.RunAsync(RealTools.Script("Write-Output 'to stdout'; [Console]::Error.WriteLine('to stderr')"), lines.Enqueue);
        Assert.Contains(new ProcessOutputLine("to stdout", false), lines);
        Assert.Contains(new ProcessOutputLine("to stderr", true), lines);
    }

    [Fact]
    public async Task Stdin_IsClosed_SoAPromptCannotHang()
    {
        var watch = Stopwatch.StartNew();
        var result = await _runner.RunAsync(RealTools.Script("if ($null -eq [Console]::In.ReadLine()) { exit 7 } else { exit 1 }", TimeSpan.FromSeconds(30)));
        Assert.Equal(7, result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Timeout_EndsTheRun_AndReportsTimedOut()
    {
        var pid = new TaskCompletionSource<int>();
        var watch = Stopwatch.StartNew();
        var result = await _runner.RunAsync(RealTools.Script("Write-Output $PID; Start-Sleep -Seconds 60", TimeSpan.FromSeconds(3)),
            l => { if (int.TryParse(l.Text, out var p)) pid.TrySetResult(p); });

        Assert.Equal(ProcessRunOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
        Assert.False(RealTools.IsRunning(await pid.Task));
    }

    [Fact]
    public async Task Cancel_EndsTheWholeTree_ButNotAnUnrelatedProcess()
    {
        // An unrelated process of the same program, started outside the runner, must survive.
        using var bystander = Process.Start(new ProcessStartInfo(RealTools.PowerShell, "-NoProfile -Command Start-Sleep -Seconds 120")
        { UseShellExecute = false, CreateNoWindow = true })!;
        var pids = new ConcurrentQueue<int>();
        var bothReported = new TaskCompletionSource();
        try
        {
            // The run starts a grandchild (as winget starts an installer) and prints both ids.
            var script = "$child = Start-Process -FilePath '" + RealTools.PowerShell + "' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -WindowStyle Hidden -PassThru; " +
                         "Write-Output $PID; Write-Output $child.Id; Start-Sleep -Seconds 120";
            using var cancel = new CancellationTokenSource();
            var run = _runner.RunAsync(RealTools.Script(script, TimeSpan.FromMinutes(2)), l =>
            {
                if (int.TryParse(l.Text, out var p)) { pids.Enqueue(p); if (pids.Count == 2) bothReported.TrySetResult(); }
            }, cancel.Token);

            await bothReported.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(pids, p => Assert.True(RealTools.IsRunning(p)));

            cancel.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(ProcessRunOutcome.Cancelled, result.Outcome);
            await WaitUntilAsync(() => pids.All(p => !RealTools.IsRunning(p)));
            Assert.All(pids, p => Assert.False(RealTools.IsRunning(p), $"process {p} of the cancelled run is still running"));
            Assert.False(bystander.HasExited, "an unrelated process was killed");
        }
        finally
        {
            foreach (var p in pids) RealTools.KillQuietly(p);
            try { bystander.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// An installer bootstrapper that starts the real setup and exits leaves an orphan: no parent
    /// chain leads to it, so only the run's job object can find it. Cancelling must end it too.
    /// </summary>
    [Fact]
    public async Task Cancel_EndsAnOrphanedGrandchild_ThatATreeWalkCannotFind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AxePower_Orphan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pidFile = Path.Combine(dir, "pid.txt");
        var middle = Path.Combine(dir, "middle.ps1");
        File.WriteAllText(middle,
            $"$g = Start-Process -FilePath '{RealTools.PowerShell}' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -WindowStyle Hidden -PassThru\n" +
            $"Set-Content -Path '{pidFile}' -Value $g.Id\n");
        int orphan = 0;
        var reported = new TaskCompletionSource();
        try
        {
            var outer = $"$m = Start-Process -FilePath '{RealTools.PowerShell}' -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','{middle}' -WindowStyle Hidden -PassThru; $m.WaitForExit(); " +
                        $"Write-Output (Get-Content -Path '{pidFile}'); Start-Sleep -Seconds 120";
            using var cancel = new CancellationTokenSource();
            var run = _runner.RunAsync(RealTools.Script(outer, TimeSpan.FromMinutes(2)), l =>
            {
                if (int.TryParse(l.Text, out var p)) { orphan = p; reported.TrySetResult(); }
            }, cancel.Token);

            await reported.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(RealTools.IsRunning(orphan), "the orphan did not start");

            cancel.Cancel();
            Assert.Equal(ProcessRunOutcome.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(20))).Outcome);
            await WaitUntilAsync(() => !RealTools.IsRunning(orphan));
            Assert.False(RealTools.IsRunning(orphan), "the orphaned grandchild outlived the cancelled run");
        }
        finally
        {
            if (orphan != 0) RealTools.KillQuietly(orphan);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NormalExit_LeavesAProgramTheRunOpenedOnPurposeRunning()
    {
        int child = 0;
        try
        {
            var script = "$c = Start-Process -FilePath '" + RealTools.PowerShell + "' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; Write-Output $c.Id";
            var result = await _runner.RunAsync(RealTools.Script(script), l => { if (int.TryParse(l.Text, out var p)) child = p; });

            Assert.Equal(ProcessRunOutcome.Exited, result.Outcome);
            Assert.NotEqual(0, child);
            await Task.Delay(500);
            Assert.True(RealTools.IsRunning(child), "a program started by a successful run was killed when the run's job closed");
        }
        finally
        {
            if (child != 0) RealTools.KillQuietly(child);
        }
    }

    [Fact]
    public async Task AlreadyCancelled_StartsNothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = await _runner.RunAsync(RealTools.Script("exit 0"), null, cancel.Token);
        Assert.Equal(ProcessRunOutcome.Cancelled, result.Outcome);
    }

    [Theory]
    [InlineData(null)]                         // not on the list
    [InlineData("powershell.exe")]             // bare name: would be a PATH lookup
    [InlineData("WindowsPowerShell\\v1.0\\powershell.exe")] // relative
    [InlineData("C:\\definitely\\missing\\winget.exe")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")] // not an .exe
    [InlineData("\\\\server\\share\\winget.exe")]
    public async Task ToolOffTheAllowList_IsNotRun(string? path)
    {
        var allow = path is null
            ? new ExecutableAllowList(new Dictionary<AllowedTool, Func<string?>>())
            : ExecutableAllowList.FromPaths(new Dictionary<AllowedTool, string> { [AllowedTool.WinGet] = path });
        var result = await new AllowListedProcessRunner(allow).RunAsync(new ProcessCommand(AllowedTool.WinGet, new[] { "list" }, TimeSpan.FromSeconds(5)));
        Assert.Equal(ProcessRunOutcome.NotAllowed, result.Outcome);
    }

    [Fact]
    public void RealAllowList_ResolvesSystemToolsToFixedPaths_AndWinGetNeverFromPathLookup()
    {
        var allow = ExecutableAllowList.Windows();
        Assert.Equal(RealTools.PowerShell, allow.Resolve(AllowedTool.PowerShell), ignoreCase: true);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), allow.Resolve(AllowedTool.Explorer), ignoreCase: true);

        var winget = allow.Resolve(AllowedTool.WinGet);
        if (winget != null) Assert.True(Path.IsPathFullyQualified(winget));

        // Elevated: only the package folder under Program Files, never the per-user alias.
        var elevated = ExecutableAllowList.Windows(() => true).Resolve(AllowedTool.WinGet);
        if (elevated != null) Assert.DoesNotContain(@"\AppData\", elevated, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }
}
