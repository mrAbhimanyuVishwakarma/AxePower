namespace AxePower.Core.Models.Music;

public enum RepeatMode
{
    Off,
    All,
    One
}

/// <summary>
/// Which ReplayGain value the player applies. <see cref="Auto"/> uses the album gain when the
/// queue came from an album or playlist opened in album order, otherwise the track gain.
/// </summary>
public enum ReplayGainMode
{
    Off,
    Track,
    Album,
    Auto
}

/// <summary>What AxePower Music does when a game or fullscreen app starts.</summary>
public enum GameDuckMode
{
    Off,
    Duck,
    Pause
}

/// <summary>What happens after the sleep timer has faded the music out.</summary>
public enum SleepTimerAfterAction
{
    Stop,
    SleepPc,
    HibernatePc
}

/// <summary>Whose playback a surface (mini player, tray, dashboard, phone) is showing.</summary>
public enum MusicSource
{
    None,
    AxePower,
    External
}

public enum MiniPlayerCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public enum TrackSortField
{
    Title,
    Artist,
    Album,
    Year,
    DateAdded,
    Duration
}

public enum AlbumSortField
{
    Title,
    Artist,
    Year
}
