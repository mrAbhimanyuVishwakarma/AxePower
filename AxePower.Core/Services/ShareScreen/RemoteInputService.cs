using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AxePower.Core.Models.ShareScreen;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>What a viewer is allowed to do to the host.</summary>
public enum RemoteControlState
{
    /// <summary>Nothing is accepted. The default, and where every session starts.</summary>
    Disabled,

    /// <summary>The host has turned control on and is waiting for someone to take it.</summary>
    Offered,

    /// <summary>A viewer holds control and their input is reaching the desktop.</summary>
    Active
}

/// <summary>
/// Injects a remote viewer's mouse and keyboard input into this PC.
///
/// This hands someone else real control of the machine, so the gate is deliberately blunt: every
/// event passes <see cref="IsAccepting"/> first, that flag is false unless the host has turned
/// control on for this session, and <see cref="RevokeAll"/> both closes it and releases anything
/// still held down. Nothing here enables itself -- the only way in is an explicit host action.
///
/// Keys and buttons that arrive are tracked so a viewer who disconnects mid-chord cannot leave a
/// modifier stuck down on the host's desktop.
/// </summary>
public sealed class RemoteInputService
{
    private readonly LogService _logService;
    private readonly object _sync = new();

    private readonly HashSet<ushort> _keysDown = new();
    private readonly HashSet<int> _buttonsDown = new();

    private CaptureDisplay? _target;
    private volatile bool _isAccepting;

    /// <summary>True only while the host has granted control for the current session.</summary>
    public bool IsAccepting => _isAccepting;

    /// <summary>Raised the first time input actually arrives, so the UI can say control is in use.</summary>
    public event EventHandler? InputReceived;

    private bool _reportedFirstInput;

    public RemoteInputService(LogService logService) => _logService = logService;

    /// <summary>
    /// Opens the gate and binds input to the monitor being shared, so a click lands where the
    /// viewer saw it rather than on whatever happens to be the primary screen.
    /// </summary>
    public void Allow(CaptureDisplay target)
    {
        lock (_sync)
        {
            _target = target;
            _reportedFirstInput = false;
            _isAccepting = true;
        }

        _logService.LogWarning(
            $"[ShareScreen] Remote control ENABLED for '{target.FriendlyName}'. " +
            "A viewer can now move the mouse and type on this PC.");
    }

    /// <summary>
    /// Shuts the gate and lets go of everything currently held.
    ///
    /// Releasing matters more than closing: if the viewer was holding Alt when control was pulled,
    /// leaving it down would wedge the host's own desktop.
    /// </summary>
    public void RevokeAll()
    {
        bool wasAccepting;
        List<ushort> keys;
        List<int> buttons;

        lock (_sync)
        {
            wasAccepting = _isAccepting;
            _isAccepting = false;
            keys = new List<ushort>(_keysDown);
            buttons = new List<int>(_buttonsDown);
            _keysDown.Clear();
            _buttonsDown.Clear();
            _target = null;
        }

        foreach (var key in keys) SendKey(key, false, force: true);
        foreach (var button in buttons) SendMouseButton(button, false, force: true);

        if (wasAccepting) _logService.LogWarning("[ShareScreen] Remote control REVOKED.");
    }

    /// <summary>
    /// Lets go of every key and button currently held, without closing the gate.
    ///
    /// Used when the viewer's window loses focus or their socket drops mid-chord: the browser
    /// stops sending key-up events at that point, so without this a modifier stays wedged down on
    /// the host's desktop and the next thing they type comes out as a shortcut.
    /// </summary>
    public void ReleaseHeld()
    {
        List<ushort> keys;
        List<int> buttons;

        lock (_sync)
        {
            keys = new List<ushort>(_keysDown);
            buttons = new List<int>(_buttonsDown);
            _keysDown.Clear();
            _buttonsDown.Clear();
        }

        foreach (var key in keys) SendKey(key, false, force: true);
        foreach (var button in buttons) SendMouseButton(button, false, force: true);
    }

    // -- Input entry points --------------------------------------------------

    /// <summary>Moves the pointer to a point given as a fraction of the shared picture.</summary>
    public void MoveMouse(double fractionX, double fractionY)
    {
        CaptureDisplay? target;
        lock (_sync)
        {
            if (!_isAccepting) return;
            target = _target;
        }
        if (target == null) return;

        NoteInput();

        var (x, y) = target.FractionToDesktop(fractionX, fractionY);
        var (normalizedX, normalizedY) = ToVirtualDesktopUnits(x, y);

        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                }
            }
        });
    }

    public void MouseButton(int button, bool pressed)
    {
        lock (_sync)
        {
            if (!_isAccepting) return;
            if (pressed) _buttonsDown.Add(button); else _buttonsDown.Remove(button);
        }

        NoteInput();
        SendMouseButton(button, pressed, force: false);
    }

    public void Scroll(int delta)
    {
        if (!_isAccepting) return;

        NoteInput();
        Send(new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { mouseData = delta, dwFlags = MOUSEEVENTF_WHEEL } }
        });
    }

    public void Key(ushort virtualKey, bool pressed)
    {
        if (virtualKey == 0) return;

        lock (_sync)
        {
            if (!_isAccepting) return;
            if (pressed) _keysDown.Add(virtualKey); else _keysDown.Remove(virtualKey);
        }

        NoteInput();
        SendKey(virtualKey, pressed, force: false);
    }

    private void NoteInput()
    {
        if (_reportedFirstInput) return;
        _reportedFirstInput = true;
        InputReceived?.Invoke(this, EventArgs.Empty);
    }

    // -- SendInput -----------------------------------------------------------

    private void SendMouseButton(int button, bool pressed, bool force)
    {
        if (!force && !_isAccepting) return;

        var flags = button switch
        {
            1 => pressed ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => pressed ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => pressed ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
        };

        Send(new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } });
    }

    private void SendKey(ushort virtualKey, bool pressed, bool force)
    {
        if (!force && !_isAccepting) return;

        var flags = pressed ? 0u : KEYEVENTF_KEYUP;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        Send(new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = flags } }
        });
    }

    /// <summary>
    /// Arrow keys, navigation and the right-hand modifiers live on the extended scan-code set;
    /// without the flag Windows delivers them as numeric-keypad presses instead.
    /// </summary>
    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or          // PageUp, PageDown, End, Home
        0x25 or 0x26 or 0x27 or 0x28 or          // Left, Up, Right, Down
        0x2D or 0x2E or                          // Insert, Delete
        0x5B or 0x5C or 0x5D or                  // LWin, RWin, Apps
        0xA3 or 0xA5 or                          // RControl, RMenu
        0x6F or 0x90;                            // Divide, NumLock

    private void Send(INPUT input)
    {
        try
        {
            var inputs = new[] { input };
            if (SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 0)
            {
                _logService.LogError($"[ShareScreen] SendInput was rejected (error {Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not inject remote input", ex);
        }
    }

    /// <summary>
    /// Converts a desktop pixel into the 0-65535 range SendInput wants, relative to the whole
    /// virtual desktop rather than the primary monitor.
    /// </summary>
    private static (int X, int Y) ToVirtualDesktopUnits(int x, int y)
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        return ((int)Math.Round((x - left) * 65535.0 / width),
                (int)Math.Round((y - top) * 65535.0 / height));
    }

    // -- Win32 ---------------------------------------------------------------

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
