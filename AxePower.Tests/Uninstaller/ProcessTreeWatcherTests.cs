using System.IO;
using System.Diagnostics;
using AxePower.Core.Services.Uninstaller;

namespace AxePower.Tests.Uninstaller;

/// <summary>
/// D3: an NSIS uninstaller starts a copy of itself and exits at once. These tests use cmd.exe and
/// ping.exe (harmless, short-lived) to reproduce that shape.
/// </summary>
public class ProcessTreeWatcherTests
{
    private static readonly ProcessWatchOptions FastOptions =
        new(false, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300));

    private static Process Start(string file, string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    [Fact]
    public async Task WaitsForAnOrphanedGrandchild_AfterTheLauncherExits()
    {
        var launched = DateTime.UtcNow;
        // cmd exits immediately; ping keeps running for about two seconds, like an NSIS temp copy
        using var cmd = Start("cmd.exe", "/c start \"\" /b ping -n 3 127.0.0.1 >nul");

        var result = await new ProcessTreeWatcher().WaitAsync(cmd.Id, launched, FastOptions, CancellationToken.None);

        Assert.Equal(ProcessTreeStatus.Exited, result.Status);
        Assert.True(result.ProcessCount >= 2, $"Expected the ping child to be tracked, saw {result.ProcessCount} process(es)");
        Assert.True(result.Elapsed >= TimeSpan.FromSeconds(1.5), $"Returned after {result.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task ReportsTheRootExitCode()
    {
        var launched = DateTime.UtcNow;
        using var cmd = Start("cmd.exe", "/c exit 3");

        var result = await new ProcessTreeWatcher().WaitAsync(cmd.Id, launched, FastOptions, CancellationToken.None);

        Assert.Equal(ProcessTreeStatus.Exited, result.Status);
        Assert.Equal(3, result.RootExitCode ?? (cmd.HasExited ? cmd.ExitCode : null));
    }

    [Fact]
    public async Task Cancellation_AbandonsTheWait_AndKillsNothing()
    {
        var launched = DateTime.UtcNow;
        using var ping = Start("ping.exe", "-n 30 127.0.0.1");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));

        try
        {
            var result = await new ProcessTreeWatcher().WaitAsync(ping.Id, launched, FastOptions, cts.Token);

            Assert.Equal(ProcessTreeStatus.WaitAbandoned, result.Status);
            Assert.False(ping.HasExited, "Cancelling must never kill the uninstaller");
        }
        finally
        {
            try { ping.Kill(); } catch { }
        }
    }

    [Fact]
    public async Task KillToken_StopsTheTree_OnlyWhenAsked()
    {
        var launched = DateTime.UtcNow;
        using var ping = Start("ping.exe", "-n 30 127.0.0.1");
        using var kill = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try
        {
            var result = await new ProcessTreeWatcher().WaitAsync(ping.Id, launched,
                FastOptions with { KillToken = kill.Token }, CancellationToken.None);

            Assert.Equal(ProcessTreeStatus.Killed, result.Status);
            Assert.True(ping.WaitForExit(5000));
        }
        finally
        {
            try { ping.Kill(); } catch { }
        }
    }

    [Fact]
    public async Task StallCallback_CanStopASilentUninstaller()
    {
        var launched = DateTime.UtcNow;
        using var ping = Start("ping.exe", "-n 30 127.0.0.1");
        var asked = 0;

        try
        {
            var result = await new ProcessTreeWatcher().WaitAsync(ping.Id, launched,
                FastOptions with
                {
                    StallAfter = TimeSpan.FromMilliseconds(400),
                    OnStalled = (_, _) => Task.FromResult(++asked >= 1),
                },
                CancellationToken.None);

            Assert.Equal(1, asked);
            Assert.Equal(ProcessTreeStatus.Killed, result.Status);
        }
        finally
        {
            try { ping.Kill(); } catch { }
        }
    }
}
