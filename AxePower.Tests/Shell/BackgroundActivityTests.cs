using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AxePower.Tests.Shell;

/// <summary>
/// Idle work and shutdown (Prompt 29). Periodic work runs only while its page is on screen: the
/// Dashboard's 2-second and the Battery page's 3-second refresh, the 1-second sampler (about 10 ms
/// CPU, a WMI query and a burst of handles per sample) for Dashboard and Game Mode, and the
/// latency ping for the Dashboard alone. On exit every resource is released within a limit.
/// </summary>
[Collection(WpfCollection.Name)]
public class BackgroundActivityTests
{
    private readonly WpfApplicationFixture _wpf;

    public BackgroundActivityTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private sealed class Shell : IDisposable
    {
        public ServiceProvider Provider { get; }
        public MainViewModel Vm { get; }
        public SystemTelemetryService Telemetry { get; }
        private readonly string _settings;

        public Shell()
        {
            ShellTestHost.UseAppResources();
            _settings = TestServiceContainer.NewSettingsDirectory();
            Provider = TestServiceContainer.Create(_settings).BuildServiceProvider();
            Vm = Provider.GetRequiredService<MainViewModel>();
            Telemetry = Provider.GetRequiredService<SystemTelemetryService>();
            Vm.AttachTelemetry(Telemetry);
        }

        public void Dispose()
        {
            Vm.IsOnScreen = false;
            Telemetry.Dispose();
            try { Directory.Delete(_settings, recursive: true); } catch { }
        }
    }

    [Fact]
    public Task NothingRuns_UntilTheWindowIsOnScreen() => _wpf.RunAsync(() =>
    {
        using var shell = new Shell();
        Assert.False(shell.Telemetry.IsSampling);
        Assert.False(shell.Vm.DashboardVm.IsLive);
        Assert.False(shell.Vm.BatteryVm.IsLive);
        Assert.False(shell.Provider.GetRequiredService<TerminalService>().HasSession); // ~74 MB of PowerShell, not started
        return Task.CompletedTask;
    });

    [Fact]
    public Task TerminalShell_StartsWhenItsPageIsShown_AndStopsAtExit() => _wpf.RunAsync(() =>
    {
        var shell = new Shell();
        var terminal = shell.Provider.GetRequiredService<TerminalService>();
        shell.Vm.IsOnScreen = true;
        Assert.False(terminal.HasSession);

        shell.Vm.NavigateCommand.Execute("Terminal");
        Assert.True(terminal.HasSession);

        AppShutdown.Run(shell.Provider, shell.Vm, hardExit: null);
        Assert.False(terminal.HasSession);
        shell.Dispose();
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Dashboard", true, true, true, false)]
    [InlineData("GameMode", true, false, false, false)]
    [InlineData("Battery", false, false, false, true)]
    [InlineData("Settings", false, false, false, false)]
    [InlineData("Logs", false, false, false, false)]
    public Task OnlyThePageOnScreen_Refreshes(string page, bool sampling, bool latency, bool dashboard, bool battery) => _wpf.RunAsync(() =>
    {
        using var shell = new Shell();
        shell.Vm.IsOnScreen = true;
        shell.Vm.NavigateCommand.Execute(page);

        Assert.Equal(sampling, shell.Telemetry.IsSampling);
        Assert.Equal(latency, shell.Telemetry.MeasureLatency);
        Assert.Equal(dashboard, shell.Vm.DashboardVm.IsLive);
        Assert.Equal(battery, shell.Vm.BatteryVm.IsLive);
        return Task.CompletedTask;
    });

    [Fact]
    public Task HiddenOrMinimised_StopsEverything_AndComingBackRestartsIt() => _wpf.RunAsync(() =>
    {
        using var shell = new Shell();
        shell.Vm.IsOnScreen = true;
        Assert.True(shell.Telemetry.IsSampling); // starts on the Dashboard

        shell.Vm.IsOnScreen = false;
        Assert.False(shell.Telemetry.IsSampling);
        Assert.False(shell.Telemetry.MeasureLatency);
        Assert.False(shell.Vm.DashboardVm.IsLive);

        shell.Vm.IsOnScreen = true;
        Assert.True(shell.Telemetry.IsSampling);
        Assert.True(shell.Vm.DashboardVm.IsLive);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Shutdown_ReleasesEverything_WithinTheLimit_AndLeavesNoChildProcess() => _wpf.RunAsync(async () =>
    {
        var shell = new Shell();
        shell.Vm.IsOnScreen = true;
        await Task.Delay(1500); // let the sampler run (it may start nvidia-smi)
        bool hardExit = false;

        var sw = Stopwatch.StartNew();
        var steps = AppShutdown.Run(shell.Provider, shell.Vm, hardExit: () => hardExit = true);
        sw.Stop();

        Assert.False(hardExit);
        Assert.True(sw.Elapsed < AppShutdown.ReleaseLimit + TimeSpan.FromSeconds(3), $"Shutdown took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(steps.All(s => s.Error == null), string.Join("\n", steps.Where(s => s.Error != null).Select(s => $"{s.Name}: {s.Error}")));
        Assert.Contains(steps, s => s.Name == "Remaining services");
        Assert.False(shell.Telemetry.IsSampling);
        Assert.False(shell.Vm.DashboardVm.IsLive);

        await Task.Delay(2000); // a sampler mid-tick finishes its last child process
        Assert.DoesNotContain(ChildProcesses(), name => name is not ("conhost.exe" or "dotnet.exe"));
        shell.Dispose(); // only deletes the temp settings folder now: everything else is released
    });

    [Fact]
    public void Shutdown_EndsTheProcess_WhenReleasingHangs()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Hangs>();
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<Hangs>();
        using var fired = new ManualResetEventSlim();

        AppShutdown.Run(provider, shell: null, hardExit: fired.Set, limit: TimeSpan.FromMilliseconds(200));

        Assert.True(fired.IsSet, "The hard exit did not fire for a release that took 1.5 s against a 0.2 s limit.");
    }

    private sealed class Hangs : IDisposable
    {
        public void Dispose() => Thread.Sleep(1500);
    }

    private static string[] ChildProcesses()
    {
        int me = Environment.ProcessId;
        using var searcher = new ManagementObjectSearcher($"SELECT Name FROM Win32_Process WHERE ParentProcessId = {me}");
        return searcher.Get().Cast<ManagementObject>().Select(o => o["Name"]?.ToString() ?? "").ToArray();
    }
}
