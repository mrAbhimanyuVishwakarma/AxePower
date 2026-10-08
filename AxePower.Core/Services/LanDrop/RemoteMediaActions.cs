using System;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services.LanDrop;

/// <summary>What the phone portal's remote buttons can ask for.</summary>
public enum RemoteMediaAction
{
    PlayPause,
    Next,
    Previous,
    VolumeUp,
    VolumeDown,
    Mute,
    Lock
}

/// <summary>
/// Carries out a remote action on this PC. Behind an interface because the real one presses
/// global media keys and can lock the workstation, which no test may do.
/// </summary>
public interface IRemoteMediaActions
{
    void Perform(RemoteMediaAction action);
}

public static class RemoteMediaActionParser
{
    /// <summary>The action named by the portal's request body (<c>play-pause</c>, <c>next</c>, …), or null for anything else.</summary>
    public static RemoteMediaAction? Parse(string body) => body.Trim().ToLowerInvariant() switch
    {
        "play-pause" => RemoteMediaAction.PlayPause,
        "next" => RemoteMediaAction.Next,
        "prev" => RemoteMediaAction.Previous,
        "vol-up" => RemoteMediaAction.VolumeUp,
        "vol-down" => RemoteMediaAction.VolumeDown,
        "mute" => RemoteMediaAction.Mute,
        "lock" => RemoteMediaAction.Lock,
        _ => null
    };
}

/// <summary>
/// Global media keys through <c>keybd_event</c>, so they reach whichever app owns the Windows media
/// session (not necessarily AxePower; see MUSIC_FIX_PLAN P2), and <c>LockWorkStation</c>.
/// </summary>
public sealed class WindowsRemoteMediaActions : IRemoteMediaActions
{
    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public void Perform(RemoteMediaAction action)
    {
        switch (action)
        {
            case RemoteMediaAction.PlayPause: Press(VK_MEDIA_PLAY_PAUSE); break;
            case RemoteMediaAction.Next: Press(VK_MEDIA_NEXT_TRACK); break;
            case RemoteMediaAction.Previous: Press(VK_MEDIA_PREV_TRACK); break;
            case RemoteMediaAction.VolumeUp: Press(VK_VOLUME_UP); break;
            case RemoteMediaAction.VolumeDown: Press(VK_VOLUME_DOWN); break;
            case RemoteMediaAction.Mute: Press(VK_VOLUME_MUTE); break;
            case RemoteMediaAction.Lock: LockWorkStation(); break;
        }
    }

    private static void Press(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { /* Best-effort: keybd_event has no failure path we can act on; the phone sees the result in the next state update. */ }
    }
}

/// <summary>This PC's battery level and charging state, for the portal's status line.</summary>
internal static class HostPowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    /// <summary>Percent and charging; a desktop without a battery reads as 100% and charging.</summary>
    public static (int Percent, bool Charging) Read()
    {
        int percent = 100;
        bool charging = true;
        try
        {
            if (GetSystemPowerStatus(out var sps))
            {
                if (sps.BatteryLifePercent != 255) percent = sps.BatteryLifePercent;
                charging = sps.ACLineStatus == 1;
            }
        }
        catch { /* Unknown power state: reported as charging at 100 %, the neutral default. */ }
        return (percent, charging);
    }
}
