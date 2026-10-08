using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AxePower.App.Services;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Diagnostics;
using AxePower.Core.Services.Music;
using AxePower.Core.Services.Operations;
using AxePower.Tests.SoftwareHub;
using Xunit;

namespace AxePower.Tests.Trust;

/// <summary>The Trust page against real services in temporary folders: state is live, and every control asks first.</summary>
public class TrustViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePower_Trust_" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfirm _confirm = new();
    private readonly FakeLauncher _launcher = new();
    private readonly List<IDisposable> _dispose = new();

    public TrustViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _dispose) d.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Secrets : ISecretStore
    {
        private string? _value;
        public string? Get(string key) => _value;
        public void Set(string key, string? value) => _value = value;
    }

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    }

    private (TrustViewModel Vm, LanDropService LanDrop, PortalAccessToken Token, LogService Log, RecentlyPlayedService Plays, RecentSearchesService Searches) Create()
    {
        var log = new LogService(new DiagnosticsStore(Path.Combine(_dir, "diag")));
        var token = new PortalAccessToken(new Secrets());
        var downloads = Path.Combine(_dir, "received");
        Directory.CreateDirectory(downloads);
        var lanDrop = new LanDropService(log, token, downloadDirectory: downloads) { DownloadDirectory = downloads };
        _dispose.Add(lanDrop);
        var paths = new MusicStoragePaths(Path.Combine(_dir, "music"));
        var plays = new RecentlyPlayedService(paths, new Clock());
        _dispose.Add(plays);
        var searches = new RecentSearchesService(paths, new Clock());
        var vm = new TrustViewModel(new SettingsService(new LogService(), Path.Combine(_dir, "settings")), log, _confirm,
            lanDrop: lanDrop, recentPlays: plays, recentSearches: searches, shell: _launcher, startTimer: false);
        _dispose.Add(vm);
        return (vm, lanDrop, token, log, plays, searches);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task Get(int port, string path)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: TestPhone\r\nConnection: close\r\n\r\n"));
        var buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer) > 0) { } } catch (IOException) { }
    }

    [Fact]
    public async Task TheLanDropListener_IsShownLive_WithItsConnectedDevices()
    {
        var (vm, lanDrop, token, _, _, _) = Create();
        var row = vm.Listeners.Single(l => l.Id == "landrop");
        Assert.False(row.IsOpen);
        Assert.Equal("Closed", row.State);

        int port = FreePort();
        Assert.True(await lanDrop.StartServerAsync("127.0.0.1", port));
        await Get(port, $"/api/status?t={token.Value}");
        vm.Refresh();

        Assert.True(row.IsOpen);
        Assert.Contains(port.ToString(), row.Address);
        Assert.DoesNotContain(token.Value, row.Address); // the page never shows the access token
        Assert.Equal("Open · 1 device(s) connected", row.State);
        Assert.Equal("127.0.0.1", Assert.Single(row.Clients).Address);
        Assert.True(row.CanStop && row.CanRotate);
    }

    [Fact]
    public async Task StoppingTheServer_AsksFirst_AndDecliningLeavesItRunning()
    {
        var (vm, lanDrop, _, _, _, _) = Create();
        Assert.True(await lanDrop.StartServerAsync("127.0.0.1", FreePort()));
        vm.Refresh();
        var row = vm.Listeners.Single(l => l.Id == "landrop");

        _confirm.Answer = false;
        await vm.StopListenerCommand.ExecuteAsync(row);
        Assert.True(lanDrop.IsRunning);

        _confirm.Answer = true;
        await vm.StopListenerCommand.ExecuteAsync(row);
        Assert.False(lanDrop.IsRunning);
        Assert.False(row.IsOpen);
        var asked = _confirm.Asked.Last();
        Assert.Equal("Stop the Phone Hub / LAN Drop server", asked.Action);
        Assert.Equal("Stop the server", asked.ConfirmLabel);
    }

    [Fact]
    public async Task RotatingAccess_ChangesTheToken_AndSaysTheOldLinkStopsWorking()
    {
        var (vm, lanDrop, token, _, _, _) = Create();
        Assert.True(await lanDrop.StartServerAsync("127.0.0.1", FreePort()));
        vm.Refresh();
        var before = token.Value;
        _confirm.Answer = true;

        vm.RotateAccessCommand.Execute(vm.Listeners.Single(l => l.Id == "landrop"));

        Assert.NotEqual(before, token.Value);
        Assert.Equal(Reversibility.Irreversible, _confirm.Asked.Single().Reversibility);
        Assert.True(lanDrop.IsRunning);
    }

    [Fact]
    public async Task DeletingMusicHistory_GoesThroughTheServices_SoNothingWritesItBack()
    {
        var (vm, _, _, _, plays, searches) = Create();
        searches.Add("lofi", Array.Empty<AxePower.Core.Models.Music.Track>());
        Assert.Single(searches.Get());
        var store = vm.Stores.Single(s => s.Id == "music-history");

        _confirm.Answer = false;
        await vm.DeleteStoreCommand.ExecuteAsync(store);
        Assert.Single(searches.Get());

        _confirm.Answer = true;
        await vm.DeleteStoreCommand.ExecuteAsync(store);
        Assert.Empty(searches.Get());
        Assert.Empty(new RecentSearchesService(new MusicStoragePaths(Path.Combine(_dir, "music")), new Clock()).Get());
    }

    [Fact]
    public async Task DeletingDiagnostics_EmptiesTheStore()
    {
        var (vm, _, _, log, _, _) = Create();
        log.LogInfo("[Trust] something to delete");
        _confirm.Answer = true;

        await vm.DeleteStoreCommand.ExecuteAsync(vm.Stores.Single(s => s.Id == "diagnostics"));

        Assert.Empty(log.Store!.Files());
        Assert.Equal(RiskLevel.Medium, _confirm.Asked.Single().Risk);
    }

    [Fact]
    public async Task ReceivedFiles_CanBeOpened_ButNeverDeletedFromHere()
    {
        var (vm, _, _, _, _, _) = Create();
        var received = vm.Stores.Single(s => s.Id == "received");
        Assert.Equal(Path.Combine(_dir, "received"), received.Path); // the LAN Drop folder actually in use

        vm.OpenStoreCommand.Execute(received);
        await vm.DeleteStoreCommand.ExecuteAsync(received);

        Assert.Equal(("folder", Path.Combine(_dir, "received")), _launcher.Launches.Single());
        Assert.Empty(_confirm.Asked);
        Assert.True(Directory.Exists(received.Path));
    }

    [Fact]
    public void SignedOut_TheAccountSectionSaysNothingIsInTheCloud()
    {
        var (vm, _, _, _, _, _) = Create();

        Assert.False(vm.IsSignedIn);
        var fact = Assert.Single(vm.AccountFacts);
        Assert.Equal("Not signed in", fact.Value);
        Assert.Contains("nothing about you is stored", fact.Detail);
        Assert.Equal(6, vm.CloudData.Count);
    }

    [Fact]
    public void TheAppAndBuildFacts_AreReadFromThisProcess()
    {
        var (vm, _, _, _, _, _) = Create();

        var rights = vm.AppFacts.Single(f => f.Label == "Administrator rights");
        Assert.Equal(Environment.IsPrivilegedProcess ? "Running as administrator" : "Running without administrator rights", rights.Value);
        Assert.Equal(AxePower.Core.Services.Trust.BuildInfo.Version, vm.BuildFacts.Single(f => f.Label == "Version").Value);
        Assert.Equal(AxePower.Core.Services.Trust.BuildInfo.Commit, vm.BuildFacts.Single(f => f.Label == "Build commit").Value);
        Assert.Contains("AxePower_Download", vm.BuildFacts.Single(f => f.Label == "Update source").Value);

        // Without administrator rights Windows hides the task index: that is "unknown", never "absent".
        var startup = vm.AppFacts.Single(f => f.Label == "Start with Windows");
        if (!Environment.IsPrivilegedProcess)
            Assert.Contains("can only be checked with administrator rights", startup.Detail);
    }

    private sealed class FakeConfirm : IConfirmationPrompts
    {
        public bool Answer { get; set; }
        public List<ActionConfirmation> Asked { get; } = new();

        public bool Confirm(ActionConfirmation confirmation)
        {
            Asked.Add(confirmation);
            return Answer;
        }
    }
}
