using System;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services;

public interface IAlertSoundService
{
    void PlayLowBatteryAlert();
}

/// <summary>Plays the warning sound selected in the user's Windows sound scheme.</summary>
public sealed class WindowsAlertSoundService : IAlertSoundService
{
    private const uint MbIconExclamation = 0x00000030;
    private readonly LogService _log;

    public WindowsAlertSoundService(LogService log)
    {
        _log = log;
    }

    public void PlayLowBatteryAlert()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (!MessageBeep(MbIconExclamation))
                _log.LogWarning("[Battery] Windows could not play the low-battery warning sound.");
        }
        catch (Exception ex)
        {
            // A missing or disabled sound device must never interrupt the power-mode transition.
            _log.LogWarning($"[Battery] Low-battery warning sound was unavailable: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);
}
