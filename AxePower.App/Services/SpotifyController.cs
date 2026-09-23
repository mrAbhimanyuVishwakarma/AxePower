using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace AxePower.App.Services;

/// <summary>
/// Controls Spotify and external media sessions (e.g. YouTube Music) even while minimized or backgrounded.
/// Uses Windows SMTC (GlobalSystemMediaTransportControlsSessionManager) with Win32 media key fallback.
/// </summary>
public class SpotifyController
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const int SW_MINIMIZE = 6;
    private const int SW_SHOWMINNOACTIVE = 7;

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_STOP = 0xB2;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public bool IsSpotifyRunning
    {
        get
        {
            try
            {
                return Process.GetProcessesByName("Spotify").Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Launches Spotify (if not already running) and ensures it remains minimized.
    /// </summary>
    public async Task<bool> LaunchAndMinimizeSpotifyAsync()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string path1 = Path.Combine(appData, "Spotify", "Spotify.exe");
            string path2 = Path.Combine(localAppData, "Microsoft", "WindowsApps", "Spotify.exe");

            string? exePath = File.Exists(path1) ? path1 : (File.Exists(path2) ? path2 : null);

            if (!IsSpotifyRunning)
            {
                if (exePath != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    };
                    Process.Start(psi);
                }
                else
                {
                    // Fallback to spotify: protocol
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "spotify:",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                }
            }

            // Ensure Spotify window is minimized
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(400);
                MinimizeSpotifyWindows();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Minimizes any open Spotify window.
    /// </summary>
    public void MinimizeSpotifyWindows()
    {
        try
        {
            var processes = Process.GetProcessesByName("Spotify");
            foreach (var p in processes)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SW_SHOWMINNOACTIVE);
                        ShowWindow(p.MainWindowHandle, SW_MINIMIZE);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Opens YouTube Music in browser or desktop app.
    /// </summary>
    public void LaunchYouTubeMusic()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ytm1 = Path.Combine(localAppData, "Programs", "youtube-music", "YouTube Music.exe");
            string ytm2 = Path.Combine(localAppData, "Programs", "YTMDesktop", "YouTube Music.exe");

            if (File.Exists(ytm1))
            {
                Process.Start(new ProcessStartInfo { FileName = ytm1, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            }
            else if (File.Exists(ytm2))
            {
                Process.Start(new ProcessStartInfo { FileName = ytm2, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://music.youtube.com",
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// Toggles play/pause for Spotify or active media player while minimized.
    /// </summary>
    public async Task TogglePlayPauseAsync()
    {
        if (await TrySmtcCommandAsync("PlayPause", "Spotify"))
            return;

        SendMediaKey(VK_MEDIA_PLAY_PAUSE);
    }

    /// <summary>
    /// Skips to next track for Spotify or active media player while minimized.
    /// </summary>
    public async Task NextTrackAsync()
    {
        if (await TrySmtcCommandAsync("Next", "Spotify"))
            return;

        SendMediaKey(VK_MEDIA_NEXT_TRACK);
    }

    /// <summary>
    /// Skips to previous track for Spotify or active media player while minimized.
    /// </summary>
    public async Task PreviousTrackAsync()
    {
        if (await TrySmtcCommandAsync("Previous", "Spotify"))
            return;

        SendMediaKey(VK_MEDIA_PREV_TRACK);
    }

    /// <summary>
    /// Retrieves current playing track title & artist from Spotify window or SMTC.
    public async Task<(string title, string artist, bool isPlaying)?> GetSpotifyTrackInfoAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            foreach (var session in sessions)
            {
                if (session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    if (props != null && !string.IsNullOrWhiteSpace(props.Title))
                    {
                        var info = session.GetPlaybackInfo();
                        bool isPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        return (props.Title, props.Artist ?? "Spotify", isPlaying);
                    }
                }
            }
        }
        catch { }

        // Window title fallback: Spotify sets window title to "Artist - Title" when playing
        try
        {
            var processes = Process.GetProcessesByName("Spotify");
            foreach (var p in processes)
            {
                string title = p.MainWindowTitle;
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Equals("Spotify", StringComparison.OrdinalIgnoreCase) &&
                    !title.StartsWith("Spotify Free", StringComparison.OrdinalIgnoreCase) &&
                    !title.StartsWith("Spotify Premium", StringComparison.OrdinalIgnoreCase))
                {
                    int sep = title.IndexOf(" - ", StringComparison.Ordinal);
                    if (sep > 0)
                    {
                        return (title[(sep + 3)..].Trim(), title[..sep].Trim(), true);
                    }
                    return (title, "Spotify", true);
                }
            }
        }
        catch { }

        return null;
    }

    private static async Task<bool> TrySmtcCommandAsync(string cmd, string appKeyword)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            foreach (var session in sessions)
            {
                if (session.SourceAppUserModelId.Contains(appKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return cmd switch
                    {
                        "PlayPause" => await session.TryTogglePlayPauseAsync(),
                        "Next" => await session.TrySkipNextAsync(),
                        "Previous" => await session.TrySkipPreviousAsync(),
                        _ => false
                    };
                }
            }

            var current = manager.GetCurrentSession();
            if (current != null)
            {
                return cmd switch
                {
                    "PlayPause" => await current.TryTogglePlayPauseAsync(),
                    "Next" => await current.TrySkipNextAsync(),
                    "Previous" => await current.TrySkipPreviousAsync(),
                    _ => false
                };
            }
        }
        catch { }

        return false;
    }

    private static void SendMediaKey(byte vkCode)
    {
        keybd_event(vkCode, 0, 0, UIntPtr.Zero);
        keybd_event(vkCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
