using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AxePower.App;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AxePower.Tests.Shell;

/// <summary>
/// The measurements behind docs/engineering/PERFORMANCE.md (Prompt 29), re-runnable:
/// <c>set AXEPOWER_BENCHMARKS=1</c>, then
/// <c>dotnet test -c Release --filter FullyQualifiedName~PerformanceBenchmarks</c>.
/// The real app graph runs in the test host with the main window off-screen. Timings depend on
/// the machine and what else runs, so this reports numbers instead of failing on them; the
/// stable rules (what may run when) are enforced by <see cref="BackgroundActivityTests"/>.
/// Without the variable the test returns at once.
/// </summary>
[Collection(WpfCollection.Name)]
public class PerformanceBenchmarks
{
    private readonly WpfApplicationFixture _wpf;
    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarks(WpfApplicationFixture wpf, ITestOutputHelper output)
    {
        _wpf = wpf;
        _output = output;
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("AXEPOWER_BENCHMARKS") == "1";

    private static (double CpuMs, int Handles, int Threads, double PrivateMb) Snap()
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        return (p.TotalProcessorTime.TotalMilliseconds, p.HandleCount, p.Threads.Count, p.PrivateMemorySize64 / 1048576.0);
    }

    private static void FullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>CPU per second (median of 1-second windows), handle swing, and threads/private memory at the end.</summary>
    private static async Task<string> Idle(string name, int seconds)
    {
        FullGc();
        var perSecond = new List<double>();
        var last = Snap();
        int minH = last.Handles, maxH = last.Handles;
        for (int s = 0; s < seconds; s++)
        {
            await Task.Delay(1000);
            var now = Snap();
            perSecond.Add(now.CpuMs - last.CpuMs);
            minH = Math.Min(minH, now.Handles);
            maxH = Math.Max(maxH, now.Handles);
            last = now;
        }
        return $"{name,-28} CPU median {Median(perSecond),5:0.0} ms/s (mean {perSecond.Average(),5:0.0}); handles {minH}-{maxH}; threads {last.Threads}; private {last.PrivateMb:0} MB";
    }

    [Fact]
    public Task Measure() => _wpf.RunAsync(async () =>
    {
        if (!Enabled) return;
        var r = new StringBuilder();
        r.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.ProcessorCount} logical CPUs, Release test host, window off-screen");

        var start = Stopwatch.StartNew();
        ShellTestHost.UseAppResources();
        var settings = TestServiceContainer.NewSettingsDirectory();
        var provider = TestServiceContainer.Create(settings).BuildServiceProvider();
        var vm = provider.GetRequiredService<MainViewModel>();
        double graphMs = start.Elapsed.TotalMilliseconds;
        var telemetry = provider.GetRequiredService<SystemTelemetryService>();
        var window = new MainWindow(vm, provider.GetRequiredService<ThemeService>(), provider.GetRequiredService<SettingsService>(), telemetry)
        {
            Width = 1280, Height = 840, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000,
            ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        r.AppendLine($"Start-up: services and view models {graphMs:0} ms; window shown and idle {start.Elapsed.TotalMilliseconds:0} ms");

        // Page switches: first visit, then the median of five more
        var pages = new[] { "GameMode", "Battery", "Music", "SoftwareHub", "SystemCare", "Settings", "Trust", "Terminal", "Dashboard" };
        foreach (var page in pages)
        {
            var times = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                var sw = Stopwatch.StartNew();
                vm.NavigateCommand.Execute(page);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                times.Add(sw.Elapsed.TotalMilliseconds);
                vm.NavigateCommand.Execute(page == "Logs" ? "Dashboard" : "Logs");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
            r.AppendLine($"Switch to {page,-12} first {times[0],5:0} ms; after {Median(times.Skip(1)),5:0} ms");
        }

        vm.NavigateCommand.Execute("Dashboard");
        await Task.Delay(3000);
        r.AppendLine(await Idle("Idle on Dashboard", 30));
        vm.NavigateCommand.Execute("Settings");
        await Task.Delay(2000);
        r.AppendLine(await Idle("Idle on Settings", 30));
        window.Hide();
        await Task.Delay(2000);
        r.AppendLine(await Idle("Idle in the tray", 30));

        window.DisposeTrayIcon();
        window.AllowClose();
        window.Close();
        var shutdown = Stopwatch.StartNew();
        var steps = AppShutdown.Run(provider, vm, hardExit: null);
        r.AppendLine($"Shutdown: {shutdown.Elapsed.TotalMilliseconds:0} ms, {steps.Count(s => s.Error != null)} step(s) failed; slowest: " +
                     string.Join(", ", steps.OrderByDescending(s => s.Took).Take(3).Select(s => $"{s.Name} {s.Took.TotalMilliseconds:0} ms")));
        try { Directory.Delete(settings, recursive: true); } catch { }

        var report = r.ToString();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "axepower-benchmarks.txt"), report);
        _output.WriteLine(report);
    });
}
