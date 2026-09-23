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
