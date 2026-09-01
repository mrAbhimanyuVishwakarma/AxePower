using System;
using System.IO;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
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

        Assert.Equal("LanDrop", vm.ActiveTab);
        Assert.False(vm.IsServerRunning);
        Assert.NotEmpty(vm.AvailableResolutions);
        Assert.NotEmpty(vm.AvailableFps);

        vm.SetTab("ScreenMirror");
        Assert.Equal("ScreenMirror", vm.ActiveTab);

        vm.SetTab("LanDrop");
        Assert.Equal("LanDrop", vm.ActiveTab);
    }

    [Fact]
    public async Task LanDrop_ServerCanStartAndStopGracefully()
    {
        var log = new LogService();
        var lan = new LanDropService(log);

        bool started = await lan.StartServerAsync("127.0.0.1", 5058);
        Assert.True(started);
        Assert.True(lan.IsRunning);
        Assert.Equal("http://127.0.0.1:5058/", lan.ServerUrl);

        lan.StopServer();
        Assert.False(lan.IsRunning);
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
