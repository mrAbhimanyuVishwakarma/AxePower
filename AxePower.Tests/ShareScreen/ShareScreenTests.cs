using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Models;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services.ShareScreen;

namespace AxePower.Tests.ShareScreen;

public class ShareScreenSettingsTests
{
    // The same options SettingsService uses to read and write settings.json
    private static readonly JsonSerializerOptions ReadOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Defaults_AreTheBalancedProfile()
    {
        var s = new ShareScreenSettings();

        Assert.Equal(0, s.DisplayIndex);
        Assert.Equal(ShareQuality.Balanced1080, s.Quality);
        Assert.Equal(ShareVideoCodec.H264, s.Codec);
        Assert.Null(s.PreferredEncoder);
        Assert.False(s.HideCursor);
        Assert.Equal(ShareAudioMode.SystemAudio, s.AudioMode);
        Assert.Equal(8554, s.RtspPort);
        Assert.Equal(8889, s.WebrtcPort);
        Assert.True(s.AdaptiveQuality);
    }

    [Fact]
    public void Clone_CarriesEveryProperty()
    {
        var original = new ShareScreenSettings
        {
            DisplayIndex = 2,
            Quality = ShareQuality.High1080,
            Codec = ShareVideoCodec.Hevc,
            PreferredEncoder = "hevc_nvenc",
            HideCursor = true,
            AudioMode = ShareAudioMode.Microphone,
            SystemAudioDeviceId = "render-1",
            MicrophoneDeviceId = "capture-1",
            MicrophoneGain = 1.5,
            RtspPort = 9554,
            WebrtcPort = 9889,
            ViewerHostOverride = "100.64.0.1",
            AdaptiveQuality = false,
            KeepAwakeWhileSharing = false
        };

        var copy = original.Clone();

        // A property added without a Clone line silently loses its value on the next save.
        foreach (var property in typeof(ShareScreenSettings).GetProperties().Where(p => p.CanRead))
        {
            Assert.Equal(property.GetValue(original), property.GetValue(copy));
        }
    }

    [Fact]
    public void AppSettings_RoundTripsTheShareScreenSection()
    {
        var settings = new AppSettings();
        settings.ShareScreen.Quality = ShareQuality.Smooth720;
        settings.ShareScreen.ViewerHostOverride = "100.64.0.1";

        var json = JsonSerializer.Serialize(settings, WriteOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);

        Assert.NotNull(restored);
        Assert.Equal(ShareQuality.Smooth720, restored!.ShareScreen.Quality);
        Assert.Equal("100.64.0.1", restored.ShareScreen.ViewerHostOverride);
    }

    [Fact]
    public void AppSettings_WithoutTheSection_LoadsDefaults()
    {
        var restored = JsonSerializer.Deserialize<AppSettings>("""{"schemaVersion":1}""", ReadOptions);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.ShareScreen);
        Assert.Equal(ShareQuality.Balanced1080, restored.ShareScreen.Quality);
    }
}

public class QualityPresetTests
{
    [Fact]
    public void EveryRung_Targets60Fps()
    {
        // The core design rule: bandwidth trouble costs resolution, never frame rate.
        Assert.All(QualityPreset.All, p => Assert.Equal(60, p.Fps));
    }

    [Fact]
    public void Ladder_StepsDownThroughResolution_AndStopsAtTheBottom()
    {
        Assert.Equal(QualityPreset.Quad1440, QualityPreset.Ultra2160.StepDown());
        Assert.Equal(QualityPreset.Balanced1080, QualityPreset.Quad1440.StepDown());
        Assert.Equal(QualityPreset.Balanced1080, QualityPreset.High1080.StepDown());
        Assert.Equal(QualityPreset.Smooth720, QualityPreset.Balanced1080.StepDown());
        Assert.Null(QualityPreset.Smooth720.StepDown());
    }

    [Fact]
    public void BitrateBounds_BracketTheTarget()
    {
        Assert.All(QualityPreset.All, p =>
        {
            Assert.True(p.MinBitrateKbps < p.TargetBitrateKbps, $"{p.DisplayName} min");
            Assert.True(p.MaxBitrateKbps > p.TargetBitrateKbps, $"{p.DisplayName} max");
        });
    }

    [Fact]
    public void For_FallsBackToBalanced_WhenTheValueIsUnknown()
    {
        Assert.Equal(QualityPreset.Balanced1080, QualityPreset.For((ShareQuality)999));
    }

    [Fact]
    public void ShortLabel_ReadsAsResolutionAndRate()
    {
        Assert.Equal("1080p60", QualityPreset.Balanced1080.ShortLabel);
        Assert.Equal("720p60", QualityPreset.Smooth720.ShortLabel);
    }
}

public class ShareRoomTests
{
    [Fact]
    public void Create_MintsDistinctSecrets()
    {
        var a = ShareRoomFactory.Create();
        var b = ShareRoomFactory.Create();

        Assert.Equal(6, a.Code.Length);
        Assert.All(a.Code, c => Assert.True(char.IsDigit(c)));

        // The path is the real secret, so it must be long and never repeat between sessions.
        Assert.StartsWith("axe", a.PathName);
        Assert.Equal(23, a.PathName.Length);
        Assert.NotEqual(a.PathName, b.PathName);
        Assert.NotEqual(a.PublisherToken, b.PublisherToken);
        Assert.NotEqual(a.PublisherToken, a.ViewerToken);
    }

    [Fact]
    public void FormattedCode_IsGroupedForReadingAloud()
    {
        var room = new ShareRoom("482731", "axe0000000000000000000", "p", "v");
        Assert.Equal("482-731", room.FormattedCode);
    }

    [Fact]
    public void PublishUrl_StaysOnLoopback_AndCarriesThePublisherToken()
    {
        var room = new ShareRoom("482731", "axepath", "pub-token", "view-token");

        var url = room.PublishUrl(8554);

        Assert.Equal("rtsp://publisher:pub-token@127.0.0.1:8554/axepath", url);
        // A publish endpoint reachable from the network would let anyone hijack the room.
        Assert.Contains("127.0.0.1", url);
    }

    [Fact]
    public void ViewerUrl_PointsAtTheGivenHost()
    {
        var room = new ShareRoom("482731", "axepath", "pub", "view");

        Assert.StartsWith("http://192.168.1.10:8889/axepath/", room.ViewerUrl("192.168.1.10", 8889));
    }
}

public class FfmpegArgumentBuilderTests
{
    private static readonly CaptureDisplay Display1440 =
        new(0, @"\\.\DISPLAY1", "Display 1", 2560, 1440, 60, true);

    private static readonly CaptureDisplay Display1080 =
        new(1, @"\\.\DISPLAY2", "Display 2", 1920, 1080, 60, false);

    private static readonly ShareEncoder Nvenc =
        new("h264_nvenc", ShareEncoderVendor.Nvidia, ShareVideoCodec.H264, "NVIDIA NVENC - H.264");

    private static readonly ShareRoom Room = new("482731", "axepath", "pub", "view");

    [Fact]
    public void CaptureFilter_AddsScale_OnlyWhenTheSizeDiffers()
    {
        var settings = new ShareScreenSettings();

        var scaled = FfmpegArgumentBuilder.BuildCaptureFilter(Display1440, QualityPreset.Balanced1080, settings);
        Assert.Contains("scale=1920:1080", scaled);

        // Scaling a display that is already the target size is pure wasted CPU.
        var native = FfmpegArgumentBuilder.BuildCaptureFilter(Display1080, QualityPreset.Balanced1080, settings);
        Assert.DoesNotContain("scale=", native);
    }

    [Fact]
    public void CaptureFilter_TargetsTheChosenOutput_AndHonoursTheCursorSetting()
    {
        var shown = FfmpegArgumentBuilder.BuildCaptureFilter(
            Display1080, QualityPreset.Balanced1080, new ShareScreenSettings { HideCursor = false });
        Assert.Contains("ddagrab=output_idx=1", shown);
        Assert.Contains("draw_mouse=1", shown);

        var hidden = FfmpegArgumentBuilder.BuildCaptureFilter(
            Display1080, QualityPreset.Balanced1080, new ShareScreenSettings { HideCursor = true });
        Assert.Contains("draw_mouse=0", hidden);
    }

    [Fact]
    public void CaptureFilter_LeavesTheGpu_BecauseTheEncoderMayBeOnAnotherAdapter()
    {
        var filter = FfmpegArgumentBuilder.BuildCaptureFilter(Display1080, QualityPreset.Balanced1080, new ShareScreenSettings());

        // On a laptop the panel hangs off the iGPU while NVENC sits on the discrete card;
        // handing those textures straight to NVENC fails with "no encode device".
        Assert.Contains("hwdownload", filter);
    }

    [Fact]
    public void EncoderArguments_DisableBFrames_ForEveryVendor()
    {
        var vendors = new[]
        {
            Nvenc,
            new ShareEncoder("h264_amf", ShareEncoderVendor.Amd, ShareVideoCodec.H264, "AMD"),
            new ShareEncoder("h264_qsv", ShareEncoderVendor.Intel, ShareVideoCodec.H264, "Intel"),
            new ShareEncoder("libx264", ShareEncoderVendor.Software, ShareVideoCodec.H264, "Software")
        };

        // B-frames reorder output and add a frame of latency for no benefit in realtime.
        Assert.All(vendors, e =>
            Assert.Contains("-bf 0", FfmpegArgumentBuilder.BuildVideoEncoderArguments(QualityPreset.Balanced1080, e)));
    }

    [Fact]
    public void EncoderArguments_KeyframeEverySecond_AndBoundedBuffer()
    {
        var args = FfmpegArgumentBuilder.BuildVideoEncoderArguments(QualityPreset.Balanced1080, Nvenc);

        Assert.Contains("-g 60", args);              // one second at 60 FPS, so late joiners start fast
        Assert.Contains("-b:v 16000k", args);
        Assert.Contains("-maxrate 22000k", args);
        Assert.Contains("-bufsize 8000k", args);     // half a second: absorbs a cut without being felt
    }

    [Fact]
    public void H264_IsPinnedToMainProfile_ForBrowserCompatibility()
    {
        // Hardware encoders default to High, which Firefox's OpenH264 decoder cannot generally
        // handle — a viewer there gets a black player rather than an error.
        var vendors = new[]
        {
            Nvenc,
            new ShareEncoder("h264_amf", ShareEncoderVendor.Amd, ShareVideoCodec.H264, "AMD"),
            new ShareEncoder("h264_qsv", ShareEncoderVendor.Intel, ShareVideoCodec.H264, "Intel"),
            new ShareEncoder("libx264", ShareEncoderVendor.Software, ShareVideoCodec.H264, "Software")
        };

        Assert.All(vendors, e =>
            Assert.Contains("-profile:v main", FfmpegArgumentBuilder.BuildVideoEncoderArguments(QualityPreset.Balanced1080, e)));
    }

    [Fact]
    public void NonH264_KeepsItsOwnProfileNames()
    {
        // HEVC and AV1 do not have a profile called "main" in the same sense; forcing one errors.
        var hevc = new ShareEncoder("hevc_nvenc", ShareEncoderVendor.Nvidia, ShareVideoCodec.Hevc, "NVIDIA HEVC");

        Assert.DoesNotContain("-profile:v", FfmpegArgumentBuilder.BuildVideoEncoderArguments(QualityPreset.Balanced1080, hevc));
    }

    [Fact]
    public void LevelIsLeftToTheEncoder_Because1080p60Needs4point2()
    {
        // H.264 level 4.0 tops out around 1080p30; pinning it would refuse the mode outright.
        var args = FfmpegArgumentBuilder.BuildVideoEncoderArguments(QualityPreset.Balanced1080, Nvenc);

        Assert.DoesNotContain("-level", args);
    }

    [Fact]
    public void Build_WithoutAudio_MapsVideoOnly()
    {
        var args = FfmpegArgumentBuilder.Build(
            Display1080, QualityPreset.Balanced1080, Nvenc, new ShareScreenSettings(), Room, audioInputArguments: null);

        Assert.Contains("-map 0:v:0", args);
        Assert.DoesNotContain("-map 1:a:0", args);
        Assert.DoesNotContain("libopus", args);
        Assert.DoesNotContain("pipe:0", args);
    }

    [Fact]
    public void Build_WithAudio_AddsThePipeAndOpus()
    {
        var args = FfmpegArgumentBuilder.Build(
            Display1080, QualityPreset.Balanced1080, Nvenc, new ShareScreenSettings(), Room,
            audioInputArguments: "-f f32le -ar 48000 -ac 2 -i pipe:0");

        Assert.Contains("-i pipe:0", args);
        Assert.Contains("-map 1:a:0", args);
        // WebRTC requires Opus, and Opus is defined at 48 kHz.
        Assert.Contains("-c:a libopus", args);
        Assert.Contains("-ar 48000", args);
    }

    [Fact]
    public void Build_PublishesOverRtspTcp_ToTheSessionPath()
    {
        var args = FfmpegArgumentBuilder.Build(
            Display1080, QualityPreset.Balanced1080, Nvenc, new ShareScreenSettings(), Room, null);

        Assert.Contains("-f rtsp", args);
        Assert.Contains("-rtsp_transport tcp", args);
        Assert.Contains("rtsp://publisher:pub@127.0.0.1:8554/axepath", args);
    }

    [Fact]
    public void Build_AsksForMachineReadableProgress()
    {
        var args = FfmpegArgumentBuilder.Build(
            Display1080, QualityPreset.Balanced1080, Nvenc, new ShareScreenSettings(), Room, null);

        // Every number on the page comes from this stream, not from what was requested.
        Assert.Contains("-progress pipe:1", args);
        Assert.Contains("-nostats", args);
    }
}

public class ShareEncoderProbeSelectionTests
{
    private readonly ShareEncoderProbe _probe = new(new AxePower.Core.Services.LogService());

    private static readonly ShareEncoder Nvenc = new("h264_nvenc", ShareEncoderVendor.Nvidia, ShareVideoCodec.H264, "NVIDIA");
    private static readonly ShareEncoder Amf = new("h264_amf", ShareEncoderVendor.Amd, ShareVideoCodec.H264, "AMD");
    private static readonly ShareEncoder Qsv = new("h264_qsv", ShareEncoderVendor.Intel, ShareVideoCodec.H264, "Intel");
    private static readonly ShareEncoder Software = new("libx264", ShareEncoderVendor.Software, ShareVideoCodec.H264, "Software");
    private static readonly ShareEncoder HevcNvenc = new("hevc_nvenc", ShareEncoderVendor.Nvidia, ShareVideoCodec.Hevc, "NVIDIA HEVC");

    [Fact]
    public void PrefersNvidia_ThenAmd_ThenIntel_ThenSoftware()
    {
        var all = new[] { Software, Qsv, Amf, Nvenc };
        Assert.Equal(Nvenc, _probe.SelectBest(all, ShareVideoCodec.H264, null));

        Assert.Equal(Amf, _probe.SelectBest(new[] { Software, Qsv, Amf }, ShareVideoCodec.H264, null));
        Assert.Equal(Qsv, _probe.SelectBest(new[] { Software, Qsv }, ShareVideoCodec.H264, null));
        Assert.Equal(Software, _probe.SelectBest(new[] { Software }, ShareVideoCodec.H264, null));
    }

    [Fact]
    public void HonoursAnExplicitChoice()
    {
        var all = new[] { Nvenc, Qsv, Software };
        Assert.Equal(Qsv, _probe.SelectBest(all, ShareVideoCodec.H264, "h264_qsv"));
    }

    [Fact]
    public void IgnoresAnExplicitChoiceThatIsNotAvailable()
    {
        // The saved encoder can belong to a GPU that has since been removed or disabled.
        var all = new[] { Nvenc, Software };
        Assert.Equal(Nvenc, _probe.SelectBest(all, ShareVideoCodec.H264, "h264_amf"));
    }

    [Fact]
    public void FallsBackToH264_WhenTheRequestedCodecHasNoEncoder()
    {
        // H.264 is the compatibility baseline every browser can decode.
        var all = new[] { Nvenc, Software };
        Assert.Equal(Nvenc, _probe.SelectBest(all, ShareVideoCodec.Av1, null));
    }

    [Fact]
    public void UsesTheRequestedCodec_WhenItIsAvailable()
    {
        var all = new[] { Nvenc, HevcNvenc };
        Assert.Equal(HevcNvenc, _probe.SelectBest(all, ShareVideoCodec.Hevc, null));
    }

    [Fact]
    public void ReturnsNull_WhenNothingCanEncode()
    {
        Assert.Null(_probe.SelectBest(System.Array.Empty<ShareEncoder>(), ShareVideoCodec.H264, null));
    }
}

public class ShareTelemetryParsingTests
{
    [Theory]
    [InlineData("16000.4kbits/s", 16.0004)]
    [InlineData("8000kbits/s", 8.0)]
    [InlineData("2.5mbits/s", 2.5)]
    [InlineData("N/A", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseBitrate_HandlesEveryShapeFfmpegEmits(string? input, double expected)
    {
        Assert.Equal(expected, ShareEncodePipeline.ParseBitrateMbps(input), 4);
    }

    [Theory]
    [InlineData("1.01x", 1.01)]
    [InlineData("0.87x", 0.87)]
    [InlineData("N/A", 1.0)]
    [InlineData(null, 1.0)]
    public void ParseSpeed_TreatsUnknownAsHealthy(string? input, double expected)
    {
        // An unparseable speed must not be mistaken for an overloaded encoder.
        Assert.Equal(expected, ShareEncodePipeline.ParseSpeed(input), 4);
    }

    [Fact]
    public void EncoderOverload_NeedsARealDip()
    {
        Assert.False(new ShareTelemetry { Speed = 1.0 }.IsEncoderOverloaded);
        Assert.False(new ShareTelemetry { Speed = 0.95 }.IsEncoderOverloaded);
        Assert.True(new ShareTelemetry { Speed = 0.80 }.IsEncoderOverloaded);
    }
}

public class FriendsPageViewModelTests
{
    private static byte[] ProtectForTest(byte[] bytes) => bytes.Select(value => (byte)(value ^ 0xA5)).ToArray();

    private class FakeShareService : IShareScreenService
    {
        public ShareSessionStatus Status { get; set; } = ShareSessionStatus.Idle;
        public event System.EventHandler<ShareSessionStatus>? StatusChanged { add { } remove { } }
        public System.Threading.Tasks.Task<ShareSessionStatus> StartAsync(ShareScreenSettings settings, System.IProgress<string>? step, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(Status);
        public System.Threading.Tasks.Task<ShareSessionStatus> StartRoomAsync(ShareScreenSettings settings, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(Status);
        public System.Threading.Tasks.Task StopScreenShareAsync() => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task StopAsync() => System.Threading.Tasks.Task.CompletedTask;
        public string? SetRemoteControl(bool enabled) => null;
        public event System.EventHandler<ShareChatMessage>? ChatMessageReceived { add { } remove { } }
        public void SendChatMessage(ShareChatMessage message) { }
        public event System.EventHandler<byte[]>? VoicePacketReceived { add { } remove { } }
        public void SendVoicePacket(byte[] opusPacket) { }
    }

    private sealed class FakeRoomVoiceService : IRoomVoiceService
    {
        public bool IsRunning { get; private set; }
        public bool IsMuted { get; private set; }
        public bool IsDeafened { get; private set; }
        public event System.EventHandler<byte[]>? EncodedPacketReady { add { } remove { } }
        public bool Start() { IsRunning = true; return true; }
        public void Stop() => IsRunning = false;
        public void SetMuted(bool muted) => IsMuted = muted;
        public void SetDeafened(bool deafened) => IsDeafened = deafened;
        public void ReceivePacket(byte[] opusPacket) { }
        public void Dispose() => Stop();
    }

    private class FakeDeviceProvider : IShareDeviceProvider
    {
        public System.Collections.Generic.IReadOnlyList<CaptureDisplay> GetDisplays() => System.Array.Empty<CaptureDisplay>();
        public System.Collections.Generic.IReadOnlyList<ShareAudioDevice> GetRenderDevices() => System.Array.Empty<ShareAudioDevice>();
        public System.Collections.Generic.IReadOnlyList<ShareAudioDevice> GetCaptureDevices() => System.Array.Empty<ShareAudioDevice>();
    }

    private class FakeToolProvider : IShareToolProvider
    {
        public ShareToolStatus FindFfmpeg() => ShareToolStatus.Missing("ffmpeg");
        public ShareToolStatus FindMediaMtx() => ShareToolStatus.Missing("mediamtx");
        public System.Threading.Tasks.Task<ShareToolStatus> EnsureFfmpegAsync(System.IProgress<double>? progress, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(ShareToolStatus.Missing("ffmpeg"));
        public System.Threading.Tasks.Task<ShareToolStatus> EnsureMediaMtxAsync(System.IProgress<double>? progress, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(ShareToolStatus.Missing("mediamtx"));
    }

    private static (AxePower.App.ViewModels.ShareScreen.ShareScreenViewModel vm, string tempDir) CreateViewModel(
        FakeShareService? shareService = null)
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxePowerTests_" + System.Guid.NewGuid().ToString("N"));
        var log = new AxePower.Core.Services.LogService();
        var settingsService = new AxePower.Core.Services.SettingsService(log, tempDir);
        var settingsVm = new AxePower.App.ViewModels.ShareScreen.ShareScreenSettingsViewModel(
            new FakeDeviceProvider(), new FakeToolProvider(), settingsService, log);
        var chatStore = new AxePower.Core.Services.ShareScreen.ShareChatStore(
            System.IO.Path.Combine(tempDir, "Chat"), log, ProtectForTest, ProtectForTest);
        var vm = new AxePower.App.ViewModels.ShareScreen.ShareScreenViewModel(
            shareService ?? new FakeShareService(), settingsVm,
            new AxePower.Core.Services.NotificationService(), log, chatStore,
            voiceService: new FakeRoomVoiceService());
        return (vm, tempDir);
    }

    [Fact]
    public void FriendsPage_InitialState_IsPrivateAndReady()
    {
        var (vm, dir) = CreateViewModel();
        try
        {
            Assert.Equal("DEV", vm.ChannelName);
            Assert.False(vm.IsMicMuted);
            Assert.False(vm.IsFriendJoined);
            Assert.False(vm.IsInCall);
            Assert.False(vm.IsJoinModalOpen);
            Assert.False(vm.IsStageActive);
            Assert.Equal("Start call", vm.InviteButtonText);
            Assert.Equal("No active call", vm.PrivacyStatusText);
            Assert.Equal('\uE720', Assert.Single(vm.MicGlyph));
            Assert.Equal('\uE767', Assert.Single(vm.AudioGlyph));
            Assert.NotEmpty(vm.SelfAvatarInitial);
            Assert.Equal("F", vm.FriendAvatarInitial);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async System.Threading.Tasks.Task StartCall_TransitionsFromIdleToARealRoom()
    {
        var room = new ShareRoom("123456", "axe123", "publisher", "viewer-secret");
        var service = new FakeShareService
        {
            Status = new ShareSessionStatus(
                ShareSessionState.RoomOpen, "Private room open", room,
                "http://192.0.2.55:8890/axe123#v=viewer-secret",
                QualityPreset.Balanced1080, null, ShareTelemetry.Empty)
        };
        var (vm, dir) = CreateViewModel(service);
        try
        {
            await vm.StartOrCopyInviteCommand.ExecuteAsync(null);

            Assert.True(vm.IsInCall);
            Assert.True(vm.IsRoomOpen);
            Assert.Equal("Private call", vm.CallTitle);
            Assert.Equal("Copy invite", vm.InviteButtonText);
            Assert.Equal("1", vm.ParticipantCountText);
            Assert.StartsWith("http://127.0.0.1:8890/axe123", vm.LocalPreviewUrl);
            Assert.StartsWith("http://127.0.0.1:8890/axe123", vm.ActiveStreamUrl);
            Assert.StartsWith("http://192.0.2.55:8890/axe123", vm.ViewerUrl);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ConnectToRoom_WithValidUrl_SetsWatchingState()
    {
        var (vm, dir) = CreateViewModel();
        try
        {
            vm.JoinAddressInput = "100.64.1.5:8555/axe123456";
            vm.ConnectToRoomCommand.Execute(null);

            Assert.True(vm.IsFriendJoined);
            Assert.True(vm.IsWatchingFriend);
            Assert.True(vm.IsStageActive);
            Assert.Equal("http://100.64.1.5:8555/axe123456", vm.ActiveFriendWatchUrl);
            Assert.Equal("100.64.1.5", vm.FriendName);
            Assert.Equal("Connected to friend", vm.StatsLine);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ConnectToRoom_WithInvalidUrl_ShowsError()
    {
        var (vm, dir) = CreateViewModel();
        try
        {
            vm.JoinAddressInput = "   ";
            vm.ConnectToRoomCommand.Execute(null);

            Assert.False(vm.IsFriendJoined);
            Assert.NotEmpty(vm.JoinStatusMessage);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async System.Threading.Tasks.Task DisconnectAll_CleansUpState()
    {
        var (vm, dir) = CreateViewModel();
        try
        {
            vm.JoinAddressInput = "http://100.64.1.5:8555/axe123456";
            vm.ConnectToRoomCommand.Execute(null);
            Assert.True(vm.IsWatchingFriend);

            await vm.DisconnectAllCommand.ExecuteAsync(null);

            Assert.False(vm.IsWatchingFriend);
            Assert.False(vm.IsFriendJoined);
            Assert.Empty(vm.ActiveFriendWatchUrl);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void SavedFriends_RemembersFriendAndEnablesQuickConnect()
    {
        var (vm, dir) = CreateViewModel();
        try
        {
            vm.FriendNameInput = "Rahul";
            vm.JoinAddressInput = "100.64.0.5:8555/room-abc";
            vm.RememberFriend = true;

            vm.ConnectToRoomCommand.Execute(null);

            Assert.Single(vm.SavedFriends);
            Assert.Equal("Rahul", vm.SavedFriends[0].Name);
            Assert.Equal("http://100.64.0.5:8555/room-abc", vm.SavedFriends[0].Address);

            // Calling joins voice/chat without forcing the screen stage open.
            var savedFriend = vm.SavedFriends[0];
            vm.CallFriendCommand.Execute(savedFriend);

            Assert.False(vm.IsWatchingFriend);
            Assert.True(vm.IsFriendJoined);
            Assert.Equal("Rahul", vm.FriendName);

            // Watching is a separate, explicit action.
            vm.WatchFriendStreamCommand.Execute(savedFriend);
            Assert.True(vm.IsWatchingFriend);

            // Test Remove
            vm.RemoveFriendCommand.Execute(savedFriend);
            Assert.Empty(vm.SavedFriends);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async System.Threading.Tasks.Task InRoomChat_TracksMessagesAndUnreadCount()
    {
        var (vm, dir) = CreateViewModel();
        try
        {
            Assert.False(vm.IsChatOpen);
            Assert.Equal(0, vm.UnreadChatCount);
            Assert.False(vm.HasUnreadChat);

            vm.ChatInputText = "Hello from test!";
            await vm.SendMessageCommand.ExecuteAsync(null);

            Assert.Single(vm.Messages);
            Assert.Equal("Hello from test!", vm.Messages[0].Text);
            Assert.True(vm.Messages[0].IsSelf);
            Assert.Empty(vm.ChatInputText);

            // Toggle Chat open
            vm.ToggleChatCommand.Execute(null);
            Assert.True(vm.IsChatOpen);
            Assert.Equal(0, vm.UnreadChatCount);

            // Close chat
            vm.CloseChatCommand.Execute(null);
            Assert.False(vm.IsChatOpen);

            // Open chat directly (from top-right chat icon)
            vm.OpenChatCommand.Execute(null);
            Assert.True(vm.IsChatOpen);

            Assert.Equal("Message DEV", vm.ChannelPlaceholderText);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async System.Threading.Tasks.Task ReinstallOrUpdate_PreservesFriendAndChatHistory()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxePowerTests_" + System.Guid.NewGuid().ToString("N"));
        var log = new AxePower.Core.Services.LogService();
        try
        {
            // Session 1: User adds friend and chats
            var settingsService1 = new AxePower.Core.Services.SettingsService(log, tempDir);
            settingsService1.LoadSettings();
            var settingsVm1 = new AxePower.App.ViewModels.ShareScreen.ShareScreenSettingsViewModel(
                new FakeDeviceProvider(), new FakeToolProvider(), settingsService1, log);
            var chatStore1 = new AxePower.Core.Services.ShareScreen.ShareChatStore(
                System.IO.Path.Combine(tempDir, "Chat"), log, ProtectForTest, ProtectForTest);
            var vm1 = new AxePower.App.ViewModels.ShareScreen.ShareScreenViewModel(
                new FakeShareService(), settingsVm1, new AxePower.Core.Services.NotificationService(), log, chatStore1);

            // Add friend
            vm1.FriendNameInput = "Alex";
            vm1.JoinAddressInput = "http://100.64.1.20:8555/stream";
            vm1.RememberFriend = true;
            vm1.ConnectToRoomCommand.Execute(null);

            // Send messages
            vm1.ChatInputText = "Hey Alex!";
            await vm1.SendMessageCommand.ExecuteAsync(null);
            vm1.ChatInputText = "Can you see my screen?";
            await vm1.SendMessageCommand.ExecuteAsync(null);

            Assert.Single(vm1.SavedFriends);
            Assert.Equal(2, vm1.Messages.Count);

            // Simulate application shutdown / update / reinstall:
            // Binaries are updated, but user directory (tempDir) stays intact!
            vm1.Dispose();

            // Session 2: New application instance starts after update
            var settingsService2 = new AxePower.Core.Services.SettingsService(log, tempDir);
            settingsService2.LoadSettings();
            var settingsVm2 = new AxePower.App.ViewModels.ShareScreen.ShareScreenSettingsViewModel(
                new FakeDeviceProvider(), new FakeToolProvider(), settingsService2, log);
            var chatStore2 = new AxePower.Core.Services.ShareScreen.ShareChatStore(
                System.IO.Path.Combine(tempDir, "Chat"), log, ProtectForTest, ProtectForTest);
            var vm2 = new AxePower.App.ViewModels.ShareScreen.ShareScreenViewModel(
                new FakeShareService(), settingsVm2, new AxePower.Core.Services.NotificationService(), log, chatStore2);

            // Verify friend was preserved across restarts/updates/reinstalls
            Assert.Single(vm2.SavedFriends);
            Assert.Equal("Alex", vm2.SavedFriends[0].Name);
            Assert.Equal("http://100.64.1.20:8555/stream", vm2.SavedFriends[0].Address);

            // Verify chat was preserved across restarts/updates/reinstalls
            Assert.Equal(2, vm2.Messages.Count);
            Assert.Equal("Hey Alex!", vm2.Messages[0].Text);
            Assert.Equal("Can you see my screen?", vm2.Messages[1].Text);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }
}
