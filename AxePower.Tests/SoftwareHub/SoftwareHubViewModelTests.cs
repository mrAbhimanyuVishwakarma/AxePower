using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Processes;
using Xunit;

namespace AxePower.Tests.SoftwareHub;

/// <summary>
/// The Software Hub page's view model on top of the real application service wired to fakes:
/// commands and state only, installs through the service, and cancel buttons that reach the
/// running process.
/// </summary>
[Collection(WpfCollection.Name)]
public class SoftwareHubViewModelTests
{
    private readonly WpfApplicationFixture _wpf;

    // On the dispatcher, as in the app, so Progress<T> callbacks arrive in order.
    public SoftwareHubViewModelTests(WpfApplicationFixture wpf) => _wpf = wpf;
    private sealed class RecordingTerminalService : TerminalService
    {
        public List<string> Commands { get; } = new();
        public override void SendCommand(string command) => Commands.Add(command);
    }

    /// <summary>Answers the standard confirmation (Prompt 27) and records what it was asked.</summary>
    internal sealed class Confirmations : AxePower.App.Services.IConfirmationPrompts
    {
        public bool Answer { get; set; } = true;
        public List<AxePower.Core.Services.Operations.ActionConfirmation> Asked { get; } = new();

        public bool Confirm(AxePower.Core.Services.Operations.ActionConfirmation confirmation)
        {
            Asked.Add(confirmation);
            return Answer;
        }
    }

    private static (SoftwareHubViewModel Vm, RecordingTerminalService Terminal) Build(HubRig rig, Confirmations? confirm = null)
    {
        var log = new LogService();
        var terminal = new RecordingTerminalService();
        var vm = new SoftwareHubViewModel(rig.Hub, new WindowsUpdateShieldService(log), new DriverUpdateService(log),
            new FirmwareUpdateService(log, new FakeLauncher(), new FakeRunner()), new NotificationService(), log,
            new GraphicsDriverService(log, new FakeLauncher()), terminal, new RestorePointService(log, new FakeRunner()),
            confirm ?? new Confirmations());
        return (vm, terminal);
    }

    [Fact]
    public Task BulkInstall_AsksFirst_NamingEveryApp_AndDecliningInstallsNothing() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig();
        var confirm = new Confirmations { Answer = false };
        var (vm, _) = Build(rig, confirm);
        Card(vm, "vlc").IsSelected = true;
        Card(vm, "7zip").IsSelected = true;

        await vm.InstallSelectedDirectCommand.ExecuteAsync(null);

        Assert.Empty(rig.Runner.Commands);
        var asked = Assert.Single(confirm.Asked);
        Assert.Equal("Install 2 apps with WinGet", asked.Action);
        Assert.Equal("Install 2 apps", asked.ConfirmLabel);
        Assert.Equal(2, asked.Affected.Count);
        Assert.False(Card(vm, "vlc").IsInstalled);
    });

    private static PackageCardViewModel Card(SoftwareHubViewModel vm, string id) => vm.AllPackages.Single(p => p.Id == id);

    [Fact]
    public Task InstallInTerminal_ShowsTheTerminal_ButTypesNoScript_AndRunsTheSafeInstalls() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig();
        var (vm, terminal) = Build(rig);
        bool navigated = false;
        terminal.NavigateToTerminalRequested += () => navigated = true;

        Card(vm, "vlc").IsSelected = true;
        Card(vm, "7zip").IsSelected = true;
        await vm.InstallSelectedViaTerminalCommand.ExecuteAsync(null);

        Assert.True(navigated);
        Assert.Empty(terminal.Commands);
        Assert.Equal(new[] { "VideoLAN.VLC", "7zip.7zip" }.OrderBy(x => x), rig.Runner.Commands.Select(c => c.Arguments[2]).OrderBy(x => x));
        Assert.All(rig.Runner.Commands, c => Assert.Equal(AllowedTool.WinGet, c.Tool));
        Assert.True(Card(vm, "vlc").IsInstalled);
        Assert.False(Card(vm, "vlc").IsSelected); // installed ones are deselected
    });

    [Fact]
    public Task CardCancel_StopsTheRunningInstall() => _wpf.RunAsync(async () =>
    {
        var started = new TaskCompletionSource();
        using var rig = new HubRig(FakeRunner.Hanging(started));
        var (vm, _) = Build(rig);
        var card = Card(vm, "vlc");

        var install = card.InstallCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(card.IsBusy);

        card.CancelCommand.Execute(null);
        await install.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(card.IsBusy);
        Assert.False(card.IsInstalled);
        Assert.Single(rig.Runner.Commands);
        Assert.Empty(rig.Launcher.Launches); // no fallback page after a cancel
    });

    [Fact]
    public Task BatchCancel_StopsTheCurrentInstall_AndSkipsTheRest() => _wpf.RunAsync(async () =>
    {
        var started = new TaskCompletionSource();
        using var rig = new HubRig(FakeRunner.Hanging(started));
        var (vm, _) = Build(rig);
        foreach (var id in new[] { "vlc", "7zip", "obs" }) Card(vm, id).IsSelected = true;

        var batch = vm.InstallSelectedDirectCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBatchInstalling);

        vm.CancelBatchCommand.Execute(null);
        await batch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBatchInstalling);
        Assert.Single(rig.Runner.Commands);
        Assert.All(new[] { "vlc", "7zip", "obs" }, id => Assert.False(Card(vm, id).IsInstalled));
        Assert.All(new[] { "vlc", "7zip", "obs" }, id => Assert.False(Card(vm, id).IsBusy));
    });

    [Fact]
    public Task Batch_PartialFailure_InstallsTheRest_AndKeepsTheFailureSelected() => _wpf.RunAsync(async () =>
    {
        using var rig = new HubRig(new FakeRunner(c => new ProcessRunResult(ProcessRunOutcome.Exited, c.Arguments[2] == "OBSProject.OBSStudio" ? 1 : 0)));
        var (vm, _) = Build(rig);
        foreach (var id in new[] { "vlc", "obs", "7zip" }) Card(vm, id).IsSelected = true;

        await vm.InstallSelectedDirectCommand.ExecuteAsync(null);

        Assert.True(Card(vm, "vlc").IsInstalled);
        Assert.True(Card(vm, "7zip").IsInstalled);
        Assert.False(Card(vm, "obs").IsInstalled);
        Assert.True(Card(vm, "obs").IsSelected);
        Assert.Equal(1, vm.SelectedCount);
    });

    [Fact]
    public Task LinksAndOpen_GoThroughTheService() => _wpf.RunAsync(() =>
    {
        using var rig = new HubRig();
        var (vm, _) = Build(rig);

        vm.ShowWebLinkWarning("t", "m", "https://ninite.com/");
        vm.ConfirmOpenWebLinkCommand.Execute(null);
        Card(vm, "steam").OpenAppCommand.Execute(null);

        Assert.Equal(new[] { ("uri", "https://ninite.com/"), ("uri", "steam://") }, rig.Launcher.Launches);
        return Task.CompletedTask;
    });

    /// <summary>
    /// A ratchet on the exit criterion "UI code does not construct shell command lines": the Software
    /// Hub's view model and view code-behind must not start processes or spell out commands.
    /// </summary>
    [Fact]
    public void SoftwareHubUiCode_BuildsNoCommandLines()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "AxePower.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var forbidden = new[] { "ProcessStartInfo", "Process.Start", "Arguments", "ArgumentList", "powershell", "cmd.exe", "Start-Process", "winget install", "winget upgrade", "winget uninstall", "SendCommand" };
        foreach (var file in new[] { "AxePower.App/ViewModels/SoftwareHubViewModel.cs", "AxePower.App/Views/SoftwareHubView.xaml.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root!, file));
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{file} contains '{word}'");
        }
    }
}
