using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AxePower.Core.Services;

/// <summary>Where the touchpad's real on/off state is read from, and how it is flipped.</summary>
public interface ITouchpadPlatform
{
    /// <summary>True/false when Windows reports a precision touchpad; null when there is none.</summary>
    bool? ReadEnabled();

    /// <summary>Flips the touchpad the same way the Windows Settings switch does.</summary>
    void Toggle();
}

/// <summary>
/// The real Windows touchpad switch. The state is what Windows publishes under
/// PrecisionTouchPad\Status (Enabled = 1/0), the value the Settings page shows. It is changed with
/// Ctrl+Win+F24, the shortcut Windows reserves for laptop touchpad keys, so AxePower and Windows
/// Settings always agree. Writing registry values instead has no effect on the running touchpad.
/// </summary>
public sealed class WindowsTouchpadPlatform : ITouchpadPlatform
{
    private const string StatusKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";

    public bool? ReadEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StatusKey);
        return key?.GetValue("Enabled") is int value ? value != 0 : null;
    }

    public void Toggle()
    {
        const byte VK_LCONTROL = 0xA2, VK_LWIN = 0x5B, VK_F24 = 0x87;
        const uint KEYUP = 0x0002;

        // Windows drops the shortcut when all six events arrive at once; a real key press is
        // never that fast, so each event gets a short gap
        void Key(byte vk, uint flags)
        {
            keybd_event(vk, 0, flags, UIntPtr.Zero);
            Thread.Sleep(KeyGapMs);
        }

        Key(VK_LCONTROL, 0);
        Key(VK_LWIN, 0);
        Key(VK_F24, 0);
        Key(VK_F24, KEYUP);
        Key(VK_LWIN, KEYUP);
        Key(VK_LCONTROL, KEYUP);
    }

    private const int KeyGapMs = 30;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}

/// <summary>
/// Turns the touchpad on or off and confirms Windows actually did it. Windows ignores a second
/// toggle that arrives too soon after the first, so a missed change is retried once after a pause.
/// </summary>
public sealed class TouchpadService
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(1200);

    private readonly ITouchpadPlatform _platform;
    private readonly LogService? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TouchpadService(ITouchpadPlatform platform, LogService? log = null)
    {
        _platform = platform;
        _log = log;
    }

    /// <summary>The touchpad's current state, or null when this PC has no precision touchpad.</summary>
    public bool? IsEnabled()
    {
        try { return _platform.ReadEnabled(); }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Touchpad] Could not read its state: {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns true once Windows reports the requested state.</summary>
    public async Task<bool> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = IsEnabled();
            if (current is null) return false;
            if (current == enabled) return true;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) await Task.Delay(RetryPause, ct);
                if (IsEnabled() == enabled) return true;   // something else already flipped it

                _platform.Toggle();
                if (await WaitForAsync(enabled, ct)) return true;
            }

            _log?.LogWarning($"[Touchpad] Windows did not turn the touchpad {(enabled ? "on" : "off")}.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> WaitForAsync(bool enabled, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ConfirmTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);
            if (IsEnabled() == enabled) return true;
        }
        return false;
    }
}
