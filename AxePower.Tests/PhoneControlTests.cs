using System;
using System.IO;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using Xunit;

namespace AxePower.Tests;

public class PhoneControlTests
{
    [Fact]
    public void AndroidControlOptions_BuildsCorrectArguments()
    {
        var options = new AndroidControlOptions
        {
            StayAwake = true,
            TurnScreenOff = true,
            ForwardAudio = false,
            MaxFps = 60,
            MaxSize = 1080,
            BitRateMbps = 12,
            SerialOrIp = "192.168.1.50:5555"
        };

        var args = options.BuildArguments();
        Assert.Contains("--stay-awake", args);
        Assert.Contains("--turn-screen-off", args);
        Assert.Contains("--no-audio", args);
        Assert.Contains("--max-fps=60", args);
        Assert.Contains("--max-size=1080", args);
        Assert.Contains("--video-bit-rate=12M", args);
        Assert.Contains("-s \"192.168.1.50:5555\"", args);
    }

    [Fact]
    public void PhoneHubViewModel_InitializesWithDefaultStateAndTabs()
    {
        var log = new LogService();
        var lan = new LanDropService(log);
        var android = new AndroidControlService(log);
        var notif = new NotificationService();

        var vm = new PhoneHubViewModel(lan, android, notif);

        Assert.Equal("ScreenMirror", vm.ActiveTab);
        Assert.False(vm.IsServerRunning);
        Assert.NotEmpty(vm.AvailableResolutions);
        Assert.NotEmpty(vm.AvailableFps);

        vm.SetTab("LanDrop");
        Assert.Equal("LanDrop", vm.ActiveTab);

        vm.SetTab("ScreenMirror");
        Assert.Equal("ScreenMirror", vm.ActiveTab);
    }

    [Fact]
    public async Task LanDrop_ServerCanStartAndStopGracefully()
    {
        var log = new LogService();
        var lan = new LanDropService(log, new PortalAccessToken(new InMemorySecretStore()));

        bool started = await lan.StartServerAsync("127.0.0.1", 5058);
        Assert.True(started);
        Assert.True(lan.IsRunning);
        Assert.Equal("http://127.0.0.1:5058/", lan.ServerUrl);

        lan.StopServer();
        Assert.False(lan.IsRunning);
    }

    [Fact]
    public async Task LanDrop_RefusesToListenWithoutAnAccessToken()
    {
        // The portal binds to every interface, so starting it with no way to tell an intended
        // request from any other would expose file upload and the remote to the whole network.
        var lan = new LanDropService(new LogService());

        bool started = await lan.StartServerAsync("127.0.0.1", 5059);

        Assert.False(started);
        Assert.False(lan.IsRunning);
    }

    [Fact]
    public async Task LanDrop_PortalLinkCarriesTheAccessToken()
    {
        var token = new PortalAccessToken(new InMemorySecretStore());
        var lan = new LanDropService(new LogService(), token);

        await lan.StartServerAsync("127.0.0.1", 5060);
        try
        {
            // The QR encodes this link, so the phone that scans it is authorised by opening it.
            Assert.Equal($"http://127.0.0.1:5060/?t={token.Value}", lan.PortalUrl);
            Assert.DoesNotContain(token.Value, lan.ServerUrl);
        }
        finally
        {
            lan.StopServer();
        }
    }

    /// <summary>Credentials in memory, so no test writes the real DPAPI secret file.</summary>
    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values = new();

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    [Fact]
    public void PhoneHubViewModel_SpaceDeskCommand_InitializesAndIsExposed()
    {
        var log = new LogService();
        var lan = new LanDropService(log);
        var android = new AndroidControlService(log);
        var notif = new NotificationService();

        var vm = new PhoneHubViewModel(lan, android, notif);

        Assert.NotNull(vm.SpaceDeskActionCommand);
        Assert.False(vm.IsSpaceDeskBusy);
    }

    [Theory]
    [InlineData("android", "https://play.google.com/store/apps/details?id=ph.spacedesk.beta")]
    [InlineData("ios", "https://apps.apple.com/in/app/spacedesk-usb-display-for-pc/id1069217220")]
    [InlineData("phonelink_android", "https://play.google.com/store/apps/details?id=com.microsoft.appmanager")]
    [InlineData("phonelink_ios", "https://apps.apple.com/in/app/link-to-windows/id6443686328")]
    public void PhoneHubViewModel_ShowDownloadQr_SetsOfficialStoreUrls(string platform, string expectedUrl)
    {
        var log = new LogService();
        var lan = new LanDropService(log);
        var android = new AndroidControlService(log);
        var notif = new NotificationService();

        var vm = new PhoneHubViewModel(lan, android, notif);
        vm.ShowDownloadQr(platform);

        Assert.True(vm.IsQrModalOpen);
        Assert.Equal(expectedUrl, vm.QrModalUrl);
    }
}
