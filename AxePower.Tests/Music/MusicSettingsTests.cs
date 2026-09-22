using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;

namespace AxePower.Tests.Music;

public class MusicSettingsTests
{
    // The same options SettingsService uses to read and write settings.json
    private static readonly JsonSerializerOptions ReadOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Defaults_MatchThePlan()
    {
        var s = new MusicSettings();

        Assert.Empty(s.LibraryFolders);
        Assert.True(s.ScanOnStartup);
        Assert.True(s.WatchFolders);
        Assert.Equal(100, s.MinimumFileSizeKb);
        Assert.False(s.SplitArtistsOnAmpersandAndComma);
        Assert.False(s.ResumePlaybackOnStartup);
        Assert.False(s.AllowVolumeBoost);
        Assert.Null(s.OutputDeviceId);
        Assert.True(s.PauseOnDeviceRemoved);
        Assert.False(s.ExclusiveMode);
        Assert.True(s.ExclusiveMatchSourceRate);
        Assert.Equal(ReplayGainMode.Off, s.ReplayGainMode);
        Assert.Equal(0, s.ReplayGainPreampDb);
        Assert.True(s.ReplayGainPreventClipping);
        Assert.Equal(0, s.ReplayGainFallbackDb);
        Assert.Equal(0, s.CrossfadeSeconds);
        Assert.True(s.CrossfadeSkipSameAlbum);
        Assert.True(s.ShowVisualizer);
        Assert.True(s.AccentFromArtwork);
        Assert.True(s.UniversalControllerEnabled);
        Assert.True(s.RemoteControlEnabled);
        Assert.True(s.LyricsFromFiles);
        Assert.False(s.ShowTrackChangeNotification);

        Assert.False(s.MiniPlayer.ShowOnStartup);
        Assert.Equal("Ctrl+Shift+M", s.MiniPlayer.Hotkey);
        Assert.Equal(MiniPlayerCorner.BottomRight, s.MiniPlayer.Corner);
        Assert.Empty(s.MiniPlayer.PositionByMonitor);
        Assert.Equal(0.95, s.MiniPlayer.Opacity);
        Assert.False(s.MiniPlayer.Expanded);
        Assert.False(s.MiniPlayer.ClickThrough);
        Assert.True(s.MiniPlayer.ShowExternalMedia);

        Assert.Equal(GameDuckMode.Duck, s.Automation.GameDuckMode);
        Assert.Equal(30, s.Automation.DuckLevelPercent);
        Assert.True(s.Automation.UseFullscreenDetection);
        Assert.True(s.Automation.PowerProfilesEnabled);
        Assert.False(s.Automation.PauseOnLowBattery);
        Assert.Equal(10, s.Automation.SleepFadeSeconds);
        Assert.Equal(SleepTimerAfterAction.Stop, s.Automation.SleepDefaultAction);
    }

    [Fact]
    public void Defaults_KeepEveryOnlineFeatureOff()
    {
        var s = new MusicSettings();

        Assert.False(s.LyricsOnline);
        Assert.False(s.Integrations.DiscordRpc);
        Assert.False(s.Integrations.LastFmEnabled);
        Assert.Null(s.Integrations.LastFmUsername);
        Assert.False(s.Integrations.SpotifyEnabled);
        Assert.Null(s.Integrations.SpotifyClientId);
    }

    [Fact]
    public void Clone_CopiesEveryValue()
    {
        var original = CreateNonDefault();

        var clone = original.Clone();

        // Serialising both is a complete property-by-property comparison, including nested
        // sections — a property missing from Clone() shows up as a difference here.
        Assert.Equal(JsonSerializer.Serialize(original, WriteOptions), JsonSerializer.Serialize(clone, WriteOptions));
    }

    [Fact]
    public void Clone_IsDeep_MutatingTheCloneLeavesTheOriginalAlone()
    {
        var original = CreateNonDefault();
        var clone = original.Clone();

        clone.LibraryFolders.Add(@"E:\More music");
        clone.MiniPlayer.PositionByMonitor["\\\\.\\DISPLAY1"][0] = 0.99;
        clone.MiniPlayer.PositionByMonitor["\\\\.\\DISPLAY9"] = new[] { 0.1, 0.1 };
        clone.MiniPlayer.Hotkey = "Alt+M";
        clone.Automation.DuckLevelPercent = 5;
        clone.Integrations.LastFmUsername = "someone-else";

        Assert.Single(original.LibraryFolders);
        Assert.Equal(0.25, original.MiniPlayer.PositionByMonitor["\\\\.\\DISPLAY1"][0]);
        Assert.Single(original.MiniPlayer.PositionByMonitor);
        Assert.Equal("Ctrl+Alt+P", original.MiniPlayer.Hotkey);
        Assert.Equal(55, original.Automation.DuckLevelPercent);
        Assert.Equal("listener", original.Integrations.LastFmUsername);
    }

    [Fact]
    public void AppSettingsClone_DeepCopiesTheMusicSection()
    {
        var app = new AppSettings { Music = CreateNonDefault() };

        var clone = app.Clone();
        clone.Music.LibraryFolders.Clear();
        clone.Music.Automation.GameDuckMode = GameDuckMode.Off;

        Assert.NotSame(app.Music, clone.Music);
        Assert.Single(app.Music.LibraryFolders);
        Assert.Equal(GameDuckMode.Pause, app.Music.Automation.GameDuckMode);
    }

    [Fact]
    public void SettingsFileWithoutMusicKey_LoadsMusicDefaults()
    {
        // A settings.json written by v1.8, before the music section existed
        const string legacyJson = """
        {
          "schemaVersion": 1,
          "autoSwitchEnabled": false,
          "pluggedInMode": "Beast",
          "theme": "Dark",
          "lowBatteryThresholdPercent": 20
        }
        """;

        var loaded = JsonSerializer.Deserialize<AppSettings>(legacyJson, ReadOptions)!;

        Assert.False(loaded.AutoSwitchEnabled);
        Assert.Equal(PowerMode.Beast, loaded.PluggedInMode);
        Assert.NotNull(loaded.Music);
        Assert.Equal(JsonSerializer.Serialize(new MusicSettings(), WriteOptions), JsonSerializer.Serialize(loaded.Music, WriteOptions));
    }

    [Fact]
    public void ExplicitNullMusicSection_BecomesDefaultsWhenCloned()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "music": null }""", ReadOptions)!;

        var clone = loaded.Clone();

        Assert.NotNull(clone.Music);
        Assert.True(clone.Music.ScanOnStartup);
    }

    [Fact]
    public void PartialMusicSection_KeepsDefaultsForMissingKeys()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("""{ "music": { "allowVolumeBoost": true, "automation": { "duckLevelPercent": 40 } } }""", ReadOptions)!;

        Assert.True(loaded.Music.AllowVolumeBoost);
        Assert.Equal(40, loaded.Music.Automation.DuckLevelPercent);
        Assert.Equal(GameDuckMode.Duck, loaded.Music.Automation.GameDuckMode);
        Assert.Equal("Ctrl+Shift+M", loaded.Music.MiniPlayer.Hotkey);
    }

    [Fact]
    public void RoundTrip_PreservesValues_AndWritesEnumsAsStrings()
    {
        var app = new AppSettings { Music = CreateNonDefault() };

        string json = JsonSerializer.Serialize(app, WriteOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions)!;

        Assert.Contains("\"music\":", json);
        Assert.Contains("\"gameDuckMode\": \"Pause\"", json);
        Assert.Contains("\"replayGainMode\": \"Album\"", json);
        Assert.Contains("\"corner\": \"TopLeft\"", json);
        Assert.Equal(JsonSerializer.Serialize(app.Music, WriteOptions), JsonSerializer.Serialize(restored.Music, WriteOptions));
    }

    private static MusicSettings CreateNonDefault() => new()
    {
        LibraryFolders = new List<string> { @"D:\Music" },
        ScanOnStartup = false,
        WatchFolders = false,
        MinimumFileSizeKb = 250,
        SplitArtistsOnAmpersandAndComma = true,
        ResumePlaybackOnStartup = true,
        AllowVolumeBoost = true,
        ReplayGainMode = ReplayGainMode.Album,
        ReplayGainPreampDb = -3.5,
        ReplayGainPreventClipping = false,
        ReplayGainFallbackDb = -6,
        CrossfadeSeconds = 4,
        CrossfadeSkipSameAlbum = false,
        ShowVisualizer = false,
        AccentFromArtwork = false,
        OutputDeviceId = "{0.0.0.00000000}.{device}",
        PauseOnDeviceRemoved = false,
        ExclusiveMode = true,
        ExclusiveMatchSourceRate = false,
        UniversalControllerEnabled = false,
        RemoteControlEnabled = false,
        LyricsFromFiles = false,
        LyricsOnline = true,
        ShowTrackChangeNotification = true,
        MiniPlayer = new MiniPlayerSettings
        {
            ShowOnStartup = true,
            Hotkey = "Ctrl+Alt+P",
            Corner = MiniPlayerCorner.TopLeft,
            PositionByMonitor = new Dictionary<string, double[]> { ["\\\\.\\DISPLAY1"] = new[] { 0.25, 0.75 } },
            Opacity = 0.8,
            Expanded = true,
            ClickThrough = true,
            ShowExternalMedia = false
        },
        Automation = new AutomationSettings
        {
            GameDuckMode = GameDuckMode.Pause,
            DuckLevelPercent = 55,
            UseFullscreenDetection = false,
            PowerProfilesEnabled = false,
            PauseOnLowBattery = true,
            SleepFadeSeconds = 25,
            SleepDefaultAction = SleepTimerAfterAction.HibernatePc
        },
        Integrations = new IntegrationSettings
        {
            DiscordRpc = true,
            LastFmEnabled = true,
            LastFmUsername = "listener",
            SpotifyEnabled = true,
            SpotifyClientId = "abc123"
        }
    };
}
