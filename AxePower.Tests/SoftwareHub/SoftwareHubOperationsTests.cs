using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;
using AxePower.Core.Services.Processes;
using AxePower.Core.Services.SoftwareHub;
using Xunit;

namespace AxePower.Tests.SoftwareHub;

/// <summary>
/// Running plans with fakes in place of winget, the shell and the network: exit codes, fallbacks,
/// timeouts, cancellation, partial batch failure, launches with the right rights, and downloads.
/// Nothing here installs, opens or downloads anything for real.
/// </summary>
public class SoftwareHubOperationsTests
{
    // -- Launches ---------------------------------------------------------------------------------

    private static (UserShellLauncher Launcher, List<ProcessStartInfo> Started) Launcher(bool elevated, bool explorerMissing = false, Exception? startThrows = null)
    {
        var started = new List<ProcessStartInfo>();
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var allow = ExecutableAllowList.FromPaths(new Dictionary<AllowedTool, string>
        {
            [AllowedTool.Explorer] = explorerMissing ? "C:\\missing\\explorer.exe" : explorer
        });
        var launcher = new UserShellLauncher(null, allow, () => elevated, psi =>
        {
            if (startThrows != null) throw startThrows;
            started.Add(psi);
        });
        return (launcher, started);
    }

    [Fact]
    public void Elevated_OpensPagesAndProgramsThroughExplorer_AsOneArgument()
    {
        var (launcher, started) = Launcher(elevated: true);

        Assert.True(launcher.OpenUri("https://ninite.com/a?b=c&d=e").Started);
        Assert.True(launcher.OpenProgram(RealTools.PowerShell).Started);

        Assert.All(started, psi =>
        {
            Assert.EndsWith("explorer.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.False(psi.UseShellExecute);
            Assert.True(string.IsNullOrEmpty(psi.Arguments));
        });
        Assert.Equal("https://ninite.com/a?b=c&d=e", Assert.Single(started[0].ArgumentList));
        Assert.Equal(RealTools.PowerShell, Assert.Single(started[1].ArgumentList));
    }

    [Fact]
    public void NotElevated_OpensDirectly_AndInstallersAlwaysRunWithAxePowersRights()
    {
        var (launcher, started) = Launcher(elevated: false);
        launcher.OpenUri("steam://");
        Assert.Equal("steam://", started[0].FileName);
        Assert.True(started[0].UseShellExecute);

        var (elevatedLauncher, elevatedStarted) = Launcher(elevated: true);
        elevatedLauncher.RunInstaller(RealTools.PowerShell);
        Assert.Equal(RealTools.PowerShell, elevatedStarted[0].FileName); // direct, not via Explorer
    }

    [Fact]
    public void Elevated_WhenExplorerIsMissing_NothingStartsElevatedInstead()
    {
        var (launcher, started) = Launcher(elevated: true, explorerMissing: true);
        var result = launcher.OpenUri("https://ninite.com/");
        Assert.Equal(ShellLaunchStatus.Failed, result.Status);
        Assert.Empty(started);
    }

    [Fact]
    public void LaunchFailure_IsReported_NotThrown()
    {
        var (launcher, _) = Launcher(elevated: false, startThrows: new Win32Exception(2, "not found"));
        Assert.Equal(ShellLaunchStatus.Failed, launcher.OpenUri("https://ninite.com/").Status);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=x")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("https://example.com/a,b")]       // Explorer reads a comma as a switch separator
    [InlineData("https://example.com/\"x")]
    [InlineData("https://exa mple.com/")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("/select,C:\\Windows")]
    [InlineData("")]
    public void HostileUri_IsRefused_AndNothingStarts(string target)
    {
        var (launcher, started) = Launcher(elevated: true);
        Assert.Equal(ShellLaunchStatus.Refused, launcher.OpenUri(target).Status);
        Assert.Empty(started);
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.bat")]
    [InlineData("C:\\missing\\tool.exe")]
    [InlineData("\\\\server\\share\\tool.exe")]
    [InlineData("C:\\Program Files\\a,b\\tool.exe")]
    [InlineData("C:\\x\\\"tool.exe")]
    public void HostileProgramPath_IsRefused_AndNothingStarts(string path)
    {
        var (launcher, started) = Launcher(elevated: true);
        Assert.Equal(ShellLaunchStatus.Refused, launcher.OpenProgram(path).Status);
        Assert.Equal(ShellLaunchStatus.Refused, launcher.RunInstaller(path).Status);
        Assert.Empty(started);
    }

    [Fact]
    public void EveryCatalogProtocol_IsOnTheLauncherList()
    {
        foreach (var item in SoftwareCatalog.CreateDefault().Items.Where(i => i.LaunchProtocolOrExe.Contains("://")))
            Assert.True(UserShellLauncher.TryValidateUri(item.LaunchProtocolOrExe, UserShellLauncher.DefaultSchemes, out _, out var reason), item.Id + ": " + reason);
    }

    // -- Running plans ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0, PackageOutcome.Succeeded)]
    [InlineData(-1978335189, PackageOutcome.Succeeded)]
    [InlineData(1, PackageOutcome.Failed)]
    [InlineData(-1978335212, PackageOutcome.Failed)]
    public async Task Install_ExitCodeDecidesTheOutcome(int exitCode, PackageOutcome expected)
    {
        using var rig = new HubRig(new FakeRunner(_ => new ProcessRunResult(ProcessRunOutcome.Exited, exitCode)));
        var sink = new LineSink();

        var result = await rig.Hub.RunAsync(PackageAction.Install, "vlc", sink);

        Assert.Equal(expected, result.Outcome);
        Assert.Contains($"[DONE] Process exited with code: {exitCode}", sink.Lines);
        Assert.Equal(expected == PackageOutcome.Succeeded, rig.Catalog.Find("vlc")!.IsInstalled);
        // The official page opens only when winget failed and the disk check did not find it.
        Assert.Equal(expected == PackageOutcome.Failed, rig.Launcher.Launches.Any(l => l.Kind == "uri"));
    }

    [Fact]
    public async Task Install_FailedExitButFoundOnDisk_CountsAsInstalled_AndOpensNoPage()
    {
        using var rig = new HubRig(new FakeRunner(_ => new ProcessRunResult(ProcessRunOutcome.Exited, 1)));
        rig.Discovery.InstalledIds.Add("vlc");

        var result = await rig.Hub.RunAsync(PackageAction.Install, "vlc", new LineSink());

        Assert.True(result.Succeeded);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task Uninstall_TriesInteractiveOnlyWhenSilentFails()
    {
        using var failing = new HubRig(new FakeRunner(c => new ProcessRunResult(ProcessRunOutcome.Exited, c.Arguments.Contains("--silent") ? 1 : 0)));
        Assert.True((await failing.Hub.RunAsync(PackageAction.Uninstall, "vlc", new LineSink())).Succeeded);
        Assert.Equal(2, failing.Runner.Commands.Count);

        using var working = new HubRig();
        Assert.True((await working.Hub.RunAsync(PackageAction.Uninstall, "vlc", new LineSink())).Succeeded);
        Assert.Single(working.Runner.Commands);
    }

    [Fact]
    public async Task Timeout_IsReported_AndCountsAsFailure()
    {
        using var rig = new HubRig(new FakeRunner(_ => new ProcessRunResult(ProcessRunOutcome.TimedOut)));
        var sink = new LineSink();
        var result = await rig.Hub.RunAsync(PackageAction.Upgrade, "vlc", sink);
        Assert.Equal(PackageOutcome.Failed, result.Outcome);
        Assert.Contains(sink.Lines, l => l.StartsWith("[TIMEOUT] Command timed out after 300s."));
    }

    [Fact]
    public async Task MissingWinGet_IsReported_AndCountsAsFailure()
    {
        using var rig = new HubRig(new FakeRunner(_ => new ProcessRunResult(ProcessRunOutcome.NotAllowed, Error: "WinGet is not installed in its expected location.")));
        var sink = new LineSink();
        Assert.Equal(PackageOutcome.Failed, (await rig.Hub.RunAsync(PackageAction.Upgrade, "vlc", sink)).Outcome);
        Assert.Contains(sink.Lines, l => l.StartsWith("[EXCEPTION] Failed to run winget"));
    }

    [Fact]
    public async Task Cancel_StopsTheRun_AndSkipsTheFallbacks()
    {
        var started = new TaskCompletionSource();
        using var rig = new HubRig(FakeRunner.Hanging(started));
        using var cancel = new CancellationTokenSource();

        var run = rig.Hub.RunAsync(PackageAction.Install, "vlc", new LineSink(), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PackageOutcome.Cancelled, result.Outcome);
        Assert.Empty(rig.Launcher.Launches);           // no "opening the website" after a cancel
        Assert.False(rig.Catalog.Find("vlc")!.IsInstalled);
    }

    [Fact]
    public async Task MaliciousId_ReachesNoProcess()
    {
        using var rig = new HubRig();
        var sink = new LineSink();
        var result = await rig.Hub.RunAsync(PackageAction.Install, "Valve.Steam --override \"/S\"", sink);
        Assert.Equal(PackageOutcome.Refused, result.Outcome);
        Assert.Empty(rig.Runner.Commands);
        Assert.Contains(sink.Lines, l => l.Contains("[Refused]"));
    }

    [Fact]
    public async Task Batch_OneFailureDoesNotStopTheRest()
    {
        using var rig = new HubRig(new FakeRunner(c => new ProcessRunResult(ProcessRunOutcome.Exited, c.Arguments[2] == "OBSProject.OBSStudio" ? 1 : 0)));
        var progress = new List<PackageBatchProgress>();

        var result = await rig.Hub.InstallManyAsync(new[] { "7zip", "obs", "vlc", "--evil" }, new LineSink(), new SyncProgress<PackageBatchProgress>(progress.Add));

        Assert.Equal(new[] { PackageOutcome.Succeeded, PackageOutcome.Failed, PackageOutcome.Succeeded, PackageOutcome.Refused },
            result.Items.Select(i => i.Result.Outcome));
        Assert.Equal(2, result.SucceededCount);
        Assert.False(result.WasCancelled);
        Assert.Equal(8, progress.Count); // a start and an end for each
        Assert.Equal(3, rig.Runner.Commands.Count);
    }

    [Fact]
    public async Task Batch_ExceptionInOnePackage_IsContained()
    {
        using var rig = new HubRig(new FakeRunner(c => c.Arguments[2] == "OBSProject.OBSStudio" ? throw new IOException("disk full") : new ProcessRunResult(ProcessRunOutcome.Exited, 0)));
        var result = await rig.Hub.InstallManyAsync(new[] { "obs", "vlc" }, new LineSink());
        Assert.Equal(new[] { PackageOutcome.Failed, PackageOutcome.Succeeded }, result.Items.Select(i => i.Result.Outcome));
    }

    [Fact]
    public async Task Batch_Cancel_StopsTheCurrentOneAndSkipsTheRest()
    {
        var secondStarted = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        using var rig = new HubRig(new FakeRunner(async (c, ct) =>
        {
            if (c.Arguments[2] != "OBSProject.OBSStudio") return new ProcessRunResult(ProcessRunOutcome.Exited, 0);
            secondStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return new ProcessRunResult(ProcessRunOutcome.Cancelled);
        }));

        var batch = rig.Hub.InstallManyAsync(new[] { "7zip", "obs", "vlc" }, new LineSink(), null, cancel.Token);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var result = await batch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { PackageOutcome.Succeeded, PackageOutcome.Cancelled, PackageOutcome.Skipped }, result.Items.Select(i => i.Result.Outcome));
        Assert.True(result.WasCancelled);
        Assert.Equal(2, rig.Runner.Commands.Count); // vlc never started
    }

    [Fact]
    public async Task ConcurrentRuns_KeepTheirOwnResults()
    {
        using var rig = new HubRig(new FakeRunner(async (c, _) =>
        {
            await Task.Delay(c.Arguments[2] == "VideoLAN.VLC" ? 200 : 10);
            return new ProcessRunResult(ProcessRunOutcome.Exited, c.Arguments[2] == "VideoLAN.VLC" ? 0 : 1);
        }));

        var vlc = rig.Hub.RunAsync(PackageAction.Upgrade, "vlc", new LineSink());
        var obs = rig.Hub.RunAsync(PackageAction.Upgrade, "obs", new LineSink());

        Assert.True((await vlc).Succeeded);
        Assert.False((await obs).Succeeded);
    }

    [Fact]
    public async Task PortableTool_IsDownloadedIntoTheToolsFolder_AndStartedAsTheUser()
    {
        using var rig = new HubRig();
        var result = await rig.Hub.RunAsync(PackageAction.Install, "dlss5swapper", new LineSink());

        Assert.True(result.Succeeded);
        var folder = Assert.Single(rig.Downloader.Folders);
        Assert.Equal(Path.Combine(rig.ToolsRoot, PortableTools.Dlss5SwapperFolder), folder);
        Assert.Equal(("program", Path.Combine(folder, "tool.exe")), Assert.Single(rig.Launcher.Launches));
        Assert.Empty(rig.Runner.Commands);
    }

    [Fact]
    public async Task PortableTool_FailedDownload_StartsNothing()
    {
        using var rig = new HubRig();
        rig.Downloader.Result = _ => null;
        Assert.Equal(PackageOutcome.Failed, (await rig.Hub.RunAsync(PackageAction.Install, "dlss5swapper", new LineSink())).Outcome);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task ToolFolderUninstall_RemovesOnlyItsFolder()
    {
        using var rig = new HubRig();
        var dlss = Directory.CreateDirectory(Path.Combine(rig.ToolsRoot, PortableTools.Dlss5SwapperFolder));
        File.WriteAllText(Path.Combine(dlss.FullName, "x.exe"), "MZ");
        var sibling = Directory.CreateDirectory(Path.Combine(rig.ToolsRoot, "Other"));

        Assert.True((await rig.Hub.RunAsync(PackageAction.Uninstall, "dlss5swapper", new LineSink())).Succeeded);
        Assert.False(Directory.Exists(dlss.FullName));
        Assert.True(Directory.Exists(sibling.FullName));
        Assert.Empty(rig.Runner.Commands);
    }

    [Fact]
    public async Task CamStudioUninstall_WithoutLocalFiles_FallsBackToWinGet()
    {
        using var rig = new HubRig();
        Assert.True((await rig.Hub.RunAsync(PackageAction.Uninstall, "camstudio", new LineSink())).Succeeded);
        Assert.Equal("CamStudio.CamStudio", Assert.Single(rig.Runner.Commands).Arguments[2]);
    }

    [Fact]
    public async Task CamStudioInstall_OpensItsPage_AndReportsNotInstalled()
    {
        using var rig = new HubRig();
        var result = await rig.Hub.RunAsync(PackageAction.Install, "camstudio", new LineSink());
        Assert.Equal(PackageOutcome.Failed, result.Outcome);
        Assert.Equal(("uri", "https://camstudio.org/"), Assert.Single(rig.Launcher.Launches));
        Assert.Empty(rig.Runner.Commands);
    }

    [Fact]
    public void OpenInstalled_RoutesProtocolsAndProgramsToTheLauncher()
    {
        using var rig = new HubRig();
        Assert.True(rig.Hub.OpenInstalled(rig.Catalog.Find("steam")!).Started);
        Assert.True(rig.Hub.OpenInstalled(rig.Catalog.Find("vlc")!).Started);
        Assert.Equal(new[] { ("uri", "steam://"), ("program", rig.Catalog.Find("vlc")!.LaunchProtocolOrExe) }, rig.Launcher.Launches);

        var nothing = new SoftwarePackageItem { Id = "x", Name = "X" };
        Assert.Equal(ShellLaunchStatus.Failed, rig.Hub.OpenInstalled(nothing).Status);
    }

    [Fact]
    public void MissingBundledInstaller_OffersItsWebPage()
    {
        using var rig = new HubRig();
        var result = rig.Hub.LaunchLocalInstaller(rig.Catalog.Find("ninite")!);
        Assert.Equal(PackageOutcome.Failed, result.Outcome);
        Assert.Equal("https://ninite.com/", result.OfferedWebUrl);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task Refresh_UsesTheRunnerForWinGetList_AndMatchesItsOutput()
    {
        var runner = new FakeRunner();
        var discovery = new PackageDiscovery(runner, null, Path.Combine(Path.GetTempPath(), "AxePower_NoTools_" + Guid.NewGuid().ToString("N")));
        var items = new[] { new SoftwarePackageItem { Id = "x", Name = "Some Tool", WinGetId = "Vendor.SomeTool" } };

        await discovery.RefreshAsync(items);

        Assert.Equal(new[] { "list" }, Assert.Single(runner.Commands).Arguments);
        PackageDiscovery.ApplyWinGetList(items, "Name   Id   Version\nSome Tool   Vendor.SomeTool   1.0");
        Assert.True(items[0].IsInstalled);
    }

    // -- Downloads --------------------------------------------------------------------------------

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Answer(request));
        }
    }

    private static byte[] FakeExe() => Encoding.ASCII.GetBytes("MZ" + new string('x', 200));

    [Theory]
    [InlineData("https://evil.example/DLSS5-Swapper-portable.exe")]
    [InlineData("http://github.com/x/DLSS5-Swapper-portable.exe")]
    [InlineData("https://github.com/x/..%2F..%2Fportable.exe")]
    [InlineData("https://github.com.evil.example/portable.exe")]
    public async Task Download_IgnoresAReleaseAddressOffTheAllowedHosts(string hostileAsset)
    {
        var http = new FakeHttp();
        http.Answer = r => r.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"tag_name\":\"v9\",\"assets\":[{{\"name\":\"DLSS5-Swapper-portable.exe\",\"browser_download_url\":\"{hostileAsset}\"}}]}}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeExe()) };
        var folder = Path.Combine(Path.GetTempPath(), "AxePower_Dl_" + Guid.NewGuid().ToString("N"));
        try
        {
            var exe = await new GitHubPortableToolDownloader(null, http).DownloadAsync(PortableTools.Dlss5Swapper, folder, new LineSink());

            Assert.Equal(Path.Combine(folder, PortableTools.Dlss5SwapperExe), exe);
            Assert.Equal(new Uri(PortableTools.Dlss5Swapper.FallbackDownloadUrl), http.Requests.Last());
            Assert.DoesNotContain(http.Requests, u => u.Host.Contains("evil"));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public async Task Download_WhenTheKnownReleaseIsOffTheAllowedHostsToo_DownloadsNothing()
    {
        // HttpClient accepts a null address, so before Prompt 22 an off-list fallback reached
        // GetAsync(null) and failed with an uncaught InvalidOperationException.
        var http = new FakeHttp(); // the release lookup answers 404, so the fallback is used
        var spec = PortableTools.Dlss5Swapper with { FallbackDownloadUrl = "https://evil.example/DLSS5-Swapper-portable.exe" };
        var folder = Path.Combine(Path.GetTempPath(), "AxePower_Dl_" + Guid.NewGuid().ToString("N"));
        var log = new LineSink();
        try
        {
            var exe = await new GitHubPortableToolDownloader(null, http).DownloadAsync(spec, folder, log);

            Assert.Null(exe);
            Assert.DoesNotContain(http.Requests, u => u.Host.Contains("evil"));
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public async Task Download_ThatIsNotAProgram_IsDiscarded_AndKeepsTheWorkingCopy()
    {
        var http = new FakeHttp { Answer = r => r.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not an exe</html>") } };
        var folder = Path.Combine(Path.GetTempPath(), "AxePower_Dl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var existing = Path.Combine(folder, PortableTools.Dlss5SwapperExe);
        File.WriteAllBytes(existing, FakeExe());
        try
        {
            var exe = await new GitHubPortableToolDownloader(null, http).DownloadAsync(PortableTools.Dlss5Swapper, folder, new LineSink());
            Assert.Null(exe);
            Assert.Equal(FakeExe(), File.ReadAllBytes(existing));
            Assert.Equal(new[] { PortableTools.Dlss5SwapperExe }, Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public async Task Download_Cancelled_LeavesNoPartialFile()
    {
        using var cancel = new CancellationTokenSource();
        var http = new FakeHttp { Answer = r =>
        {
            if (r.RequestUri!.Host != "api.github.com") cancel.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeExe()) };
        } };
        var folder = Path.Combine(Path.GetTempPath(), "AxePower_Dl_" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new GitHubPortableToolDownloader(null, http).DownloadAsync(PortableTools.Dlss5Swapper, folder, new LineSink(), cancel.Token));
            Assert.Empty(Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>());
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    // -- The page's other services --------------------------------------------------------------

    [Theory]
    [InlineData("https://download.nvidia.com.evil.example/572.70-desktop.exe")]
    [InlineData("http://us.download.nvidia.com/Windows/572.70/x.exe")]
    [InlineData("https://us.download.nvidia.com/Windows/572.70/..%2F..%2Fx.exe")]
    [InlineData("https://us.download.nvidia.com/Windows/572.70/driver.msi")]
    public async Task NvidiaDriver_FromAnywhereButNvidia_IsRefusedBeforeDownloading(string url)
    {
        var launcher = new FakeLauncher();
        var service = new GraphicsDriverService(new LogService(), launcher);
        var (ok, message) = await service.DownloadAndRunNvidiaDriverAsync(url, "572.70");
        Assert.False(ok);
        Assert.Equal("Invalid download URL.", message);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task RestorePoint_RunsAllowListedPowerShell_WithTheScriptEncoded()
    {
        var runner = new FakeRunner();
        var ok = await new RestorePointService(new LogService(), runner).CreateRestorePointAsync("AxePower O'Brien; Remove-Item C:\\", new LineSink());

        Assert.True(ok);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(AllowedTool.PowerShell, command.Tool);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" }, command.Arguments.Take(5));
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[5]));
        Assert.Contains("'AxePower O''Brien; Remove-Item C:\\'", script);
    }

    [Fact]
    public void DriverUpdater_WhenElevated_CarriesTheScriptInline_WithNoFileToSwap()
    {
        var psi = DriverUpdateService.BuildLaunch(RealTools.PowerShell, "Write-Host 'hi'", elevated: true);
        Assert.Equal(RealTools.PowerShell, psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.True(string.IsNullOrEmpty(psi.Arguments));
        var encoded = psi.ArgumentList[psi.ArgumentList.IndexOf("-EncodedCommand") + 1];
        Assert.Equal("Write-Host 'hi'", Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
        Assert.DoesNotContain("-File", psi.ArgumentList);
    }

    /// <summary>Reports synchronously, unlike Progress&lt;T&gt;.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public SyncProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
