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

    [JsonPropertyName("playlistFolder")]
    public string? PlaylistFolder { get; set; }

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

    /// <summary>
    /// Whether the compact Now Playing card is open on the Dashboard ("Music ▾" toggles it there).
    /// Other pages always start with it closed. Replaces the old "headerPlayerExpanded", which
    /// was saved from any page, so that key is deliberately not read.
    /// </summary>
    [JsonPropertyName("dashboardPlayerExpanded")]
    public bool DashboardPlayerExpanded { get; set; } = true;

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

    // ── Sources (providers) ─────────────────────────────────────────────────

    /// <summary>Which providers are switched on, which one is active per kind, and how streams are cached.</summary>
    [JsonPropertyName("providers")]
    public ProviderSettings Providers { get; set; } = new();

    // ── Shortcuts ───────────────────────────────────────────────────

    /// <summary>Keyboard shortcuts for playback, and whether they work outside AxePower.</summary>
    [JsonPropertyName("shortcuts")]
    public ShortcutSettings Shortcuts { get; set; } = new();

    public MusicSettings Clone()
    {
        return new MusicSettings
        {
            LibraryFolders = new List<string>(LibraryFolders ?? new List<string>()),
            PlaylistFolder = PlaylistFolder,
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
            DashboardPlayerExpanded = DashboardPlayerExpanded,
            MiniPlayer = (MiniPlayer ?? new MiniPlayerSettings()).Clone(),
            UniversalControllerEnabled = UniversalControllerEnabled,
            RemoteControlEnabled = RemoteControlEnabled,
            Automation = (Automation ?? new AutomationSettings()).Clone(),
            LyricsFromFiles = LyricsFromFiles,
            LyricsOnline = LyricsOnline,
            ShowTrackChangeNotification = ShowTrackChangeNotification,
            Integrations = (Integrations ?? new IntegrationSettings()).Clone(),
            Providers = (Providers ?? new ProviderSettings()).Clone(),
            Shortcuts = (Shortcuts ?? new ShortcutSettings()).Clone()
        };
    }
}

/// <summary>
/// Which music sources are available and which one answers each job. Provider ids are stable
/// strings ("local", "youtube", "spotify"); an id that no longer exists is ignored on load.
/// </summary>
public class ProviderSettings
{
    /// <summary>Providers the user has switched on. "local" is always on and cannot be removed.</summary>
    [JsonPropertyName("enabled")]
    public List<string> Enabled { get; set; } = new() { "local", "youtube" };

    /// <summary>The provider that supplies audio for tracks that are not local files.</summary>
    [JsonPropertyName("activeStreaming")]
    public string ActiveStreaming { get; set; } = "youtube";

    /// <summary>The provider that answers searches and lists playlists.</summary>
    [JsonPropertyName("activeMetadata")]
    public string ActiveMetadata { get; set; } = "youtube";

    /// <summary>How long a resolved remote URL is trusted before it is fetched again.</summary>
    [JsonPropertyName("streamExpiryMinutes")]
    public int StreamExpiryMinutes { get; set; } = 60;

    /// <summary>How many candidates to try before giving up on a track.</summary>
    [JsonPropertyName("streamResolutionRetries")]
    public int StreamResolutionRetries { get; set; } = 3;

    /// <summary>
    /// Download each streamed track to the cache before playing instead of streaming it
    /// progressively. Slower to start, but survives a flaky connection.
    /// </summary>
    [JsonPropertyName("cacheStreamsToDisk")]
    public bool CacheStreamsToDisk { get; set; } = false;

    /// <summary>Upper bound for the streamed-audio cache. Oldest files are pruned first.</summary>
    [JsonPropertyName("streamCacheMaxMb")]
    public int StreamCacheMaxMb { get; set; } = 1024;

    /// <summary>Keep the queue going by appending recommendations when it runs out.</summary>
    [JsonPropertyName("discoveryEnabled")]
    public bool DiscoveryEnabled { get; set; } = false;

    /// <summary>0 = stay with the artists playing, 100 = look furthest afield.</summary>
    [JsonPropertyName("discoveryVariety")]
    public int DiscoveryVariety { get; set; } = 50;

    /// <summary>How many tracks to append each time the queue runs out.</summary>
    [JsonPropertyName("discoveryBatchSize")]
    public int DiscoveryBatchSize { get; set; } = 10;

    public ProviderSettings Clone() => new()
    {
        Enabled = new List<string>(Enabled ?? new List<string>()),
        ActiveStreaming = ActiveStreaming,
        ActiveMetadata = ActiveMetadata,
        StreamExpiryMinutes = StreamExpiryMinutes,
        StreamResolutionRetries = StreamResolutionRetries,
        CacheStreamsToDisk = CacheStreamsToDisk,
        StreamCacheMaxMb = StreamCacheMaxMb,
        DiscoveryEnabled = DiscoveryEnabled,
        DiscoveryVariety = DiscoveryVariety,
        DiscoveryBatchSize = DiscoveryBatchSize
    };
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
