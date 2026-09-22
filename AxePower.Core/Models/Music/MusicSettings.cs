using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Music;

/// <summary>
/// Persistent AxePower Music preferences, stored under the <c>music</c> key of settings.json.
///
/// Session values (volume, mute, shuffle, repeat, speed, pitch) are deliberately not here: they
/// live in playback-state.json so a slider drag never rewrites settings.json or raises
/// <c>SettingsService.OnSettingsChanged</c> for every listener.
///
/// Every property needs a default and a line in <see cref="Clone"/>, or its value is lost.
/// </summary>
public class MusicSettings
{
    // ── Library ─────────────────────────────────────────────────────────────

    [JsonPropertyName("libraryFolders")]
    public List<string> LibraryFolders { get; set; } = new();

    [JsonPropertyName("scanOnStartup")]
    public bool ScanOnStartup { get; set; } = true;

    [JsonPropertyName("watchFolders")]
    public bool WatchFolders { get; set; } = true;

    [JsonPropertyName("minimumFileSizeKb")]
    public int MinimumFileSizeKb { get; set; } = 100;

    /// <summary>Off by default so names like "Simon &amp; Garfunkel" stay one artist.</summary>
    [JsonPropertyName("splitArtistsOnAmpersandAndComma")]
    public bool SplitArtistsOnAmpersandAndComma { get; set; } = false;

    // ── Playback ────────────────────────────────────────────────────────────

    /// <summary>When off, the last session is restored paused.</summary>
    [JsonPropertyName("resumePlaybackOnStartup")]
    public bool ResumePlaybackOnStartup { get; set; } = false;

    [JsonPropertyName("allowVolumeBoost")]
    public bool AllowVolumeBoost { get; set; } = false;

    [JsonPropertyName("replayGainMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ReplayGainMode ReplayGainMode { get; set; } = ReplayGainMode.Off;

    [JsonPropertyName("replayGainPreampDb")]
    public double ReplayGainPreampDb { get; set; } = 0;

    [JsonPropertyName("replayGainPreventClipping")]
    public bool ReplayGainPreventClipping { get; set; } = true;

    [JsonPropertyName("replayGainFallbackDb")]
    public double ReplayGainFallbackDb { get; set; } = 0;

    [JsonPropertyName("crossfadeSeconds")]
    public double CrossfadeSeconds { get; set; } = 0;

    [JsonPropertyName("crossfadeSkipSameAlbum")]
    public bool CrossfadeSkipSameAlbum { get; set; } = true;

    [JsonPropertyName("showVisualizer")]
    public bool ShowVisualizer { get; set; } = true;

    [JsonPropertyName("accentFromArtwork")]
    public bool AccentFromArtwork { get; set; } = true;

    // ── Audio output ────────────────────────────────────────────────────────

    /// <summary>Null means "Windows default (follows changes)".</summary>
    [JsonPropertyName("outputDeviceId")]
    public string? OutputDeviceId { get; set; }

    [JsonPropertyName("pauseOnDeviceRemoved")]
    public bool PauseOnDeviceRemoved { get; set; } = true;

    [JsonPropertyName("exclusiveMode")]
    public bool ExclusiveMode { get; set; } = false;

    [JsonPropertyName("exclusiveMatchSourceRate")]
    public bool ExclusiveMatchSourceRate { get; set; } = true;

    // ── Surfaces ────────────────────────────────────────────────────────────

    [JsonPropertyName("miniPlayer")]
    public MiniPlayerSettings MiniPlayer { get; set; } = new();

    [JsonPropertyName("universalControllerEnabled")]
    public bool UniversalControllerEnabled { get; set; } = true;

    [JsonPropertyName("remoteControlEnabled")]
    public bool RemoteControlEnabled { get; set; } = true;

    // ── Automations ─────────────────────────────────────────────────────────

    [JsonPropertyName("automation")]
    public AutomationSettings Automation { get; set; } = new();

    // ── Extras ──────────────────────────────────────────────────────────────

    [JsonPropertyName("lyricsFromFiles")]
    public bool LyricsFromFiles { get; set; } = true;

    /// <summary>Online lookups are opt-in: nothing leaves the PC until this is turned on.</summary>
    [JsonPropertyName("lyricsOnline")]
    public bool LyricsOnline { get; set; } = false;

    [JsonPropertyName("showTrackChangeNotification")]
    public bool ShowTrackChangeNotification { get; set; } = false;

    [JsonPropertyName("integrations")]
    public IntegrationSettings Integrations { get; set; } = new();

    public MusicSettings Clone()
    {
        return new MusicSettings
        {
            LibraryFolders = new List<string>(LibraryFolders ?? new List<string>()),
            ScanOnStartup = ScanOnStartup,
            WatchFolders = WatchFolders,
            MinimumFileSizeKb = MinimumFileSizeKb,
            SplitArtistsOnAmpersandAndComma = SplitArtistsOnAmpersandAndComma,
            ResumePlaybackOnStartup = ResumePlaybackOnStartup,
            AllowVolumeBoost = AllowVolumeBoost,
            ReplayGainMode = ReplayGainMode,
            ReplayGainPreampDb = ReplayGainPreampDb,
            ReplayGainPreventClipping = ReplayGainPreventClipping,
            ReplayGainFallbackDb = ReplayGainFallbackDb,
            CrossfadeSeconds = CrossfadeSeconds,
            CrossfadeSkipSameAlbum = CrossfadeSkipSameAlbum,
            ShowVisualizer = ShowVisualizer,
            AccentFromArtwork = AccentFromArtwork,
            OutputDeviceId = OutputDeviceId,
            PauseOnDeviceRemoved = PauseOnDeviceRemoved,
            ExclusiveMode = ExclusiveMode,
            ExclusiveMatchSourceRate = ExclusiveMatchSourceRate,
            MiniPlayer = (MiniPlayer ?? new MiniPlayerSettings()).Clone(),
            UniversalControllerEnabled = UniversalControllerEnabled,
            RemoteControlEnabled = RemoteControlEnabled,
            Automation = (Automation ?? new AutomationSettings()).Clone(),
            LyricsFromFiles = LyricsFromFiles,
            LyricsOnline = LyricsOnline,
            ShowTrackChangeNotification = ShowTrackChangeNotification,
            Integrations = (Integrations ?? new IntegrationSettings()).Clone()
        };
    }
}

public class MiniPlayerSettings
{
    [JsonPropertyName("showOnStartup")]
    public bool ShowOnStartup { get; set; } = false;

    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = "Ctrl+Shift+M";

    [JsonPropertyName("corner")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MiniPlayerCorner Corner { get; set; } = MiniPlayerCorner.BottomRight;

    /// <summary>
    /// Monitor device name → [x, y] as a fraction of that monitor's work area, so a position
    /// survives resolution and scaling changes.
    /// </summary>
    [JsonPropertyName("positionByMonitor")]
    public Dictionary<string, double[]> PositionByMonitor { get; set; } = new();

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.95;

    [JsonPropertyName("expanded")]
    public bool Expanded { get; set; } = false;

    [JsonPropertyName("clickThrough")]
    public bool ClickThrough { get; set; } = false;

    [JsonPropertyName("showExternalMedia")]
    public bool ShowExternalMedia { get; set; } = true;

    public MiniPlayerSettings Clone()
    {
        return new MiniPlayerSettings
        {
            ShowOnStartup = ShowOnStartup,
            Hotkey = Hotkey,
            Corner = Corner,
            PositionByMonitor = (PositionByMonitor ?? new Dictionary<string, double[]>())
                .ToDictionary(kv => kv.Key, kv => (double[])(kv.Value ?? System.Array.Empty<double>()).Clone()),
            Opacity = Opacity,
            Expanded = Expanded,
            ClickThrough = ClickThrough,
            ShowExternalMedia = ShowExternalMedia
        };
    }
}

public class AutomationSettings
{
    [JsonPropertyName("gameDuckMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameDuckMode GameDuckMode { get; set; } = GameDuckMode.Duck;

    [JsonPropertyName("duckLevelPercent")]
    public int DuckLevelPercent { get; set; } = 30;

    [JsonPropertyName("useFullscreenDetection")]
    public bool UseFullscreenDetection { get; set; } = true;

    [JsonPropertyName("powerProfilesEnabled")]
    public bool PowerProfilesEnabled { get; set; } = true;

    /// <summary>Uses <c>AppSettings.LowBatteryThresholdPercent</c>; there is no separate threshold.</summary>
    [JsonPropertyName("pauseOnLowBattery")]
    public bool PauseOnLowBattery { get; set; } = false;

    [JsonPropertyName("sleepFadeSeconds")]
    public int SleepFadeSeconds { get; set; } = 10;

    [JsonPropertyName("sleepDefaultAction")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SleepTimerAfterAction SleepDefaultAction { get; set; } = SleepTimerAfterAction.Stop;

    public AutomationSettings Clone()
    {
        return new AutomationSettings
        {
            GameDuckMode = GameDuckMode,
            DuckLevelPercent = DuckLevelPercent,
            UseFullscreenDetection = UseFullscreenDetection,
            PowerProfilesEnabled = PowerProfilesEnabled,
            PauseOnLowBattery = PauseOnLowBattery,
            SleepFadeSeconds = SleepFadeSeconds,
            SleepDefaultAction = SleepDefaultAction
        };
    }
}

/// <summary>
/// Online integrations. All are off by default. Credentials (Last.fm session, Spotify refresh
/// token) are never stored here; they go through the DPAPI secret store.
/// </summary>
public class IntegrationSettings
{
    [JsonPropertyName("discordRpc")]
    public bool DiscordRpc { get; set; } = false;

    [JsonPropertyName("lastFmEnabled")]
    public bool LastFmEnabled { get; set; } = false;

    /// <summary>Display only; the session key lives in the secret store.</summary>
    [JsonPropertyName("lastFmUsername")]
    public string? LastFmUsername { get; set; }

    [JsonPropertyName("spotifyEnabled")]
    public bool SpotifyEnabled { get; set; } = false;

    [JsonPropertyName("spotifyClientId")]
    public string? SpotifyClientId { get; set; }

    public IntegrationSettings Clone()
    {
        return new IntegrationSettings
        {
            DiscordRpc = DiscordRpc,
            LastFmEnabled = LastFmEnabled,
            LastFmUsername = LastFmUsername,
            SpotifyEnabled = SpotifyEnabled,
            SpotifyClientId = SpotifyClientId
        };
    }
}
