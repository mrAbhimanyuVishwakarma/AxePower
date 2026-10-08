using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services;

public enum ControllerNavigationState
{
    Running,
    Stopped,
    Failed
}

public enum ConnectedGamepadType
{
    None,
    Xbox,
    PlayStation5
}

/// <summary>
/// Authentic Gopher360 Engine directly ported into C# for AxePower.
/// Provides 100% native, offline, 150 FPS sub-pixel smoothed controller-to-mouse and keyboard navigation.
/// </summary>
public class ControllerNavigationService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private readonly LogService _logService;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _isRunning;

    // --- Win32 Native Structs & P/Invoke ---

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    // Mouse flags
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    // Keyboard flags
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // XInput Button Constants
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
    private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
    private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
    private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    private const ushort XINPUT_GAMEPAD_START = 0x0010;
    private const ushort XINPUT_GAMEPAD_BACK = 0x0020;
    private const ushort XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
    private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
    private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
    private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    private const ushort XINPUT_GAMEPAD_A = 0x1000;
    private const ushort XINPUT_GAMEPAD_B = 0x2000;
    private const ushort XINPUT_GAMEPAD_X = 0x4000;
    private const ushort XINPUT_GAMEPAD_Y = 0x8000;

    // Virtual Keys
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_UP = 0x26;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_DOWN = 0x28;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_BROWSER_BACK = 0xA6;
    private const ushort VK_BROWSER_FORWARD = 0xA7;

    // Gopher360 Exact Math Constants
    private const int DEAD_ZONE = 7500;
    private const int SCROLL_DEAD_ZONE = 8500;
    private const int TRIGGER_DEAD_ZONE = 40;
    private const int SCROLL_SPEED = 28;
    private const int FPS = 150;
    private const int SLEEP_AMOUNT = 1000 / FPS; // ~6.6 ms

    private const float SPEED_LOW = 0.032f;
    private const float SPEED_MED = 0.048f;
    private const float SPEED_HIGH = 0.075f;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    private delegate int XInputGetStateDelegate(int dwUserIndex, ref XINPUT_STATE pState);
    private delegate int XInputSetStateDelegate(int dwUserIndex, ref XINPUT_VIBRATION pVibration);

    private XInputGetStateDelegate? _xinputGetState;
    private XInputSetStateDelegate? _xinputSetState;

    public ControllerNavigationService(LogService logService)
    {
        _logService = logService;
        InitializeXInput();
    }

    private void InitializeXInput()
    {
        string[] dlls = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" };
        foreach (var dll in dlls)
        {
            var hModule = LoadLibrary(dll);
            if (hModule != IntPtr.Zero)
            {
                var getProc = GetProcAddress(hModule, "XInputGetState");
                var setProc = GetProcAddress(hModule, "XInputSetState");
                if (getProc != IntPtr.Zero)
                {
                    _xinputGetState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(getProc);
                    if (setProc != IntPtr.Zero)
                    {
                        _xinputSetState = Marshal.GetDelegateForFunctionPointer<XInputSetStateDelegate>(setProc);
                    }
                    _logService.LogInfo($"[ControllerNavigation] Gopher360 Engine initialized with {dll}");
                    return;
                }
            }
        }
        _logService.LogWarning("[ControllerNavigation] XInput library not found.");
    }

    public bool IsRunning() => _isRunning;

    public async Task<ControllerNavigationState> StartAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (_isRunning) return ControllerNavigationState.Running;

        if (_xinputGetState == null)
        {
            InitializeXInput();
            if (_xinputGetState == null)
            {
                _logService.LogError("[ControllerNavigation] Cannot start: XInput unavailable.");
                return ControllerNavigationState.Failed;
            }
        }

        _cts = new CancellationTokenSource();
        _isRunning = true;
        _pollingTask = Task.Run(() => PollingLoop(_cts.Token), _cts.Token);

        _logService.LogInfo("[ControllerNavigation] Gopher360 native navigation engine active.");
        return ControllerNavigationState.Running;
    }

    public async Task<ControllerNavigationState> StopAsync()
    {
        _isRunning = false;
        var cts = _cts;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* Already disposed by an earlier stop. */ }
        }

        if (_pollingTask != null)
        {
            try { await _pollingTask; } catch { /* The polling loop ends by cancellation; its errors were logged inside it. */ }
            _pollingTask = null;
        }

        if (cts != null)
        {
            try { cts.Dispose(); } catch { /* Already disposed by an earlier stop. */ }
            _cts = null;
        }

        _logService.LogInfo("[ControllerNavigation] Gopher360 navigation stopped.");
        return ControllerNavigationState.Stopped;
    }

    public async Task<ControllerNavigationState> ToggleAsync(CancellationToken ct = default)
    {
        if (_isRunning)
        {
            return await StopAsync();
        }
        else
        {
            return await StartAsync(ct);
        }
    }

    public bool CheckXInputConnected()
    {
        if (_xinputGetState == null)
        {
            InitializeXInput();
        }

        if (_xinputGetState == null) return false;

        for (int i = 0; i < 4; i++)
        {
            var state = new XINPUT_STATE();
            if (_xinputGetState(i, ref state) == 0) // ERROR_SUCCESS == 0
                return true;
        }

        return false;
    }

    public ConnectedGamepadType DetectConnectedGamepad()
    {
        // 1. Check for Sony PlayStation DualSense / DualShock devices via RawInput
        try
        {
            uint deviceCount = 0;
            uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(IntPtr.Zero, ref deviceCount, structSize) == 0 && deviceCount > 0)
            {
                IntPtr pList = Marshal.AllocHGlobal((int)(structSize * deviceCount));
                try
                {
                    if (GetRawInputDeviceList(pList, ref deviceCount, structSize) != uint.MaxValue)
                    {
                        for (int i = 0; i < deviceCount; i++)
                        {
                            var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(pList + (int)(i * structSize));
                            if (item.dwType == RIM_TYPEHID)
                            {
                                uint nameLen = 0;
                                GetRawInputDeviceInfo(item.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameLen);
                                if (nameLen > 0)
                                {
                                    IntPtr pName = Marshal.AllocHGlobal((int)nameLen * 2);
                                    try
                                    {
                                        if (GetRawInputDeviceInfo(item.hDevice, RIDI_DEVICENAME, pName, ref nameLen) != uint.MaxValue)
                                        {
                                            string? devName = Marshal.PtrToStringAuto(pName);
                                            if (!string.IsNullOrEmpty(devName))
                                            {
                                                string upper = devName.ToUpperInvariant();
                                                if (upper.Contains("VID_054C") || upper.Contains("VID&0002054C") || upper.Contains("DUALSENSE") || upper.Contains("PLAYSTATION"))
                                                {
                                                    return ConnectedGamepadType.PlayStation5;
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.FreeHGlobal(pName);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pList);
                }
            }
        }
        catch { /* Raw-input device names are cosmetic; an unreadable list leaves the generic name. */ }

        // 2. Check for XInput / Xbox compatible controller
        if (CheckXInputConnected())
        {
            return ConnectedGamepadType.Xbox;
        }

        return ConnectedGamepadType.None;
    }

    private void PollingLoop(CancellationToken token)
    {
        float speed = SPEED_MED;
        float xRest = 0.0f;
        float yRest = 0.0f;

        bool lTriggerPrevious = false;
        bool rTriggerPrevious = false;

        var lastIteration = new Dictionary<ushort, bool>();
        var isDown = new Dictionary<ushort, bool>();
        var isUp = new Dictionary<ushort, bool>();

        void SetButtonState(ushort buttonMask, ushort currentButtons)
        {
            bool down = (currentButtons & buttonMask) != 0;
            if (!lastIteration.TryGetValue(buttonMask, out bool prev))
            {
                prev = false;
            }

            isDown[buttonMask] = down && !prev;
            isUp[buttonMask] = !down && prev;
            lastIteration[buttonMask] = down;
        }

        try
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                bool controllerFound = false;

                for (int i = 0; i < 4; i++)
                {
                    var state = new XINPUT_STATE();
                    int res = _xinputGetState!(i, ref state);
                    if (res == 0) // Controller connected
                    {
                        controllerFound = true;
                        var gp = state.Gamepad;
                        ushort buttons = gp.wButtons;

                        // 1. Mouse Movement (Left Thumbstick)
                        short tx = gp.sThumbLX;
                        short ty = gp.sThumbLY;

                        if (Math.Abs(tx) > DEAD_ZONE || Math.Abs(ty) > DEAD_ZONE)
                        {
                            if (GetCursorPos(out POINT cursor))
                            {
                                float x = cursor.X + xRest;
                                float y = cursor.Y + yRest;

                                float dx = GetDelta(tx, speed);
                                float dy = GetDelta(ty, speed);

                                x += dx;
                                y -= dy; // Invert Y axis for natural mouse feel

                                int finalX = (int)Math.Round(x);
                                int finalY = (int)Math.Round(y);

                                xRest = x - finalX;
                                yRest = y - finalY;

                                SetCursorPos(finalX, finalY);
                            }
                        }
                        else
                        {
                            xRest = 0.0f;
                            yRest = 0.0f;
                        }

                        // 2. Mouse Wheel Scroll (Right Thumbstick)
                        short ry = gp.sThumbRY;
                        if (ry > SCROLL_DEAD_ZONE)
                        {
                            SendMouseEvent(MOUSEEVENTF_WHEEL, (uint)SCROLL_SPEED);
                        }
                        else if (ry < -SCROLL_DEAD_ZONE)
                        {
                            SendMouseEvent(MOUSEEVENTF_WHEEL, unchecked((uint)-SCROLL_SPEED));
                        }

                        // 3. Update Button Edge Detection
                        SetButtonState(XINPUT_GAMEPAD_A, buttons);
                        SetButtonState(XINPUT_GAMEPAD_X, buttons);
                        SetButtonState(XINPUT_GAMEPAD_B, buttons);
                        SetButtonState(XINPUT_GAMEPAD_Y, buttons);
                        SetButtonState(XINPUT_GAMEPAD_LEFT_THUMB, buttons);
                        SetButtonState(XINPUT_GAMEPAD_RIGHT_THUMB, buttons);
                        SetButtonState(XINPUT_GAMEPAD_LEFT_SHOULDER, buttons);
                        SetButtonState(XINPUT_GAMEPAD_RIGHT_SHOULDER, buttons);
                        SetButtonState(XINPUT_GAMEPAD_DPAD_UP, buttons);
                        SetButtonState(XINPUT_GAMEPAD_DPAD_DOWN, buttons);
                        SetButtonState(XINPUT_GAMEPAD_DPAD_LEFT, buttons);
                        SetButtonState(XINPUT_GAMEPAD_DPAD_RIGHT, buttons);
                        SetButtonState(XINPUT_GAMEPAD_START, buttons);
                        SetButtonState(XINPUT_GAMEPAD_BACK, buttons);

                        // 4. Mouse Button Actions
                        // A Button -> Left Mouse Click
                        if (isDown[XINPUT_GAMEPAD_A]) SendMouseEvent(MOUSEEVENTF_LEFTDOWN);
                        if (isUp[XINPUT_GAMEPAD_A]) SendMouseEvent(MOUSEEVENTF_LEFTUP);

                        // X Button -> Right Mouse Click
                        if (isDown[XINPUT_GAMEPAD_X]) SendMouseEvent(MOUSEEVENTF_RIGHTDOWN);
                        if (isUp[XINPUT_GAMEPAD_X]) SendMouseEvent(MOUSEEVENTF_RIGHTUP);

                        // Left Stick Click -> Middle Mouse Click
                        if (isDown[XINPUT_GAMEPAD_LEFT_THUMB]) SendMouseEvent(MOUSEEVENTF_MIDDLEDOWN);
                        if (isUp[XINPUT_GAMEPAD_LEFT_THUMB]) SendMouseEvent(MOUSEEVENTF_MIDDLEUP);

                        // 5. Keyboard Navigation Mappings
                        // B Button -> Enter
                        if (isDown[XINPUT_GAMEPAD_B]) SendKeyboardEvent(VK_RETURN, 0);
                        if (isUp[XINPUT_GAMEPAD_B]) SendKeyboardEvent(VK_RETURN, KEYEVENTF_KEYUP);

                        // Y Button -> Open On-Screen Keyboard
                        if (isDown[XINPUT_GAMEPAD_Y]) OpenOnScreenKeyboard();

                        // Start Button -> Windows Key
                        if (isDown[XINPUT_GAMEPAD_START]) SendKeyboardEvent(VK_LWIN, 0);
                        if (isUp[XINPUT_GAMEPAD_START]) SendKeyboardEvent(VK_LWIN, KEYEVENTF_KEYUP);

                        // Shoulders -> Browser Back / Forward
                        if (isDown[XINPUT_GAMEPAD_LEFT_SHOULDER]) SendKeyboardEvent(VK_BROWSER_BACK, 0);
                        if (isUp[XINPUT_GAMEPAD_LEFT_SHOULDER]) SendKeyboardEvent(VK_BROWSER_BACK, KEYEVENTF_KEYUP);
                        if (isDown[XINPUT_GAMEPAD_RIGHT_SHOULDER]) SendKeyboardEvent(VK_BROWSER_FORWARD, 0);
                        if (isUp[XINPUT_GAMEPAD_RIGHT_SHOULDER]) SendKeyboardEvent(VK_BROWSER_FORWARD, KEYEVENTF_KEYUP);

                        // Triggers: Left Trigger = Space, Right Trigger = Backspace
                        bool lTrigger = gp.bLeftTrigger > TRIGGER_DEAD_ZONE;
                        if (lTrigger != lTriggerPrevious)
                        {
                            lTriggerPrevious = lTrigger;
                            SendKeyboardEvent(VK_SPACE, lTrigger ? 0 : KEYEVENTF_KEYUP);
                        }

                        bool rTrigger = gp.bRightTrigger > TRIGGER_DEAD_ZONE;
                        if (rTrigger != rTriggerPrevious)
                        {
                            rTriggerPrevious = rTrigger;
                            SendKeyboardEvent(VK_BACK, rTrigger ? 0 : KEYEVENTF_KEYUP);
                        }

                        // D-Pad -> Arrow Keys
                        if (isDown[XINPUT_GAMEPAD_DPAD_UP]) SendKeyboardEvent(VK_UP, 0);
                        if (isUp[XINPUT_GAMEPAD_DPAD_UP]) SendKeyboardEvent(VK_UP, KEYEVENTF_KEYUP);

                        if (isDown[XINPUT_GAMEPAD_DPAD_DOWN]) SendKeyboardEvent(VK_DOWN, 0);
                        if (isUp[XINPUT_GAMEPAD_DPAD_DOWN]) SendKeyboardEvent(VK_DOWN, KEYEVENTF_KEYUP);

                        if (isDown[XINPUT_GAMEPAD_DPAD_LEFT]) SendKeyboardEvent(VK_LEFT, 0);
                        if (isUp[XINPUT_GAMEPAD_DPAD_LEFT]) SendKeyboardEvent(VK_LEFT, KEYEVENTF_KEYUP);

                        if (isDown[XINPUT_GAMEPAD_DPAD_RIGHT]) SendKeyboardEvent(VK_RIGHT, 0);
                        if (isUp[XINPUT_GAMEPAD_DPAD_RIGHT]) SendKeyboardEvent(VK_RIGHT, KEYEVENTF_KEYUP);

                        // Speed cycle on Right Thumb Click
                        if (isDown[XINPUT_GAMEPAD_RIGHT_THUMB])
                        {
                            if (Math.Abs(speed - SPEED_LOW) < 0.001f) speed = SPEED_MED;
                            else if (Math.Abs(speed - SPEED_MED) < 0.001f) speed = SPEED_HIGH;
                            else speed = SPEED_LOW;

                            Vibrate(i, 20000, 150);
                        }

                        break; // Process primary active controller
                    }
                }

                if (token.WaitHandle.WaitOne(controllerFound ? SLEEP_AMOUNT : 50))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* Stopped. */ }
        catch (ObjectDisposedException) { /* Stopped while a poll was in flight. */ }
        catch (Exception ex)
        {
            _logService.LogError("[ControllerNavigation] PollingLoop exception", ex);
        }
    }

    private static float GetDelta(short t, float speed)
    {
        if (t > 32767) t = 0;
        if (t < -32768) t = 0;

        float delta = 0.0f;
        if (Math.Abs(t) > DEAD_ZONE)
        {
            int sign = (t > 0) ? 1 : -1;
            int adjusted = sign * (Math.Abs(t) - DEAD_ZONE);
            delta = speed * adjusted / (FPS / 10.0f);
        }
        return delta;
    }

    private static void SendMouseEvent(uint dwFlags, uint mouseData = 0)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUT_UNION
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = dwFlags,
                    mouseData = mouseData,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendKeyboardEvent(ushort wVk, uint dwFlags)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = wVk,
                    dwFlags = dwFlags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    private void Vibrate(int userIndex, ushort motorSpeed, int durationMs)
    {
        if (_xinputSetState == null) return;
        Task.Run(() =>
        {
            try
            {
                var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = motorSpeed, wRightMotorSpeed = motorSpeed };
                _xinputSetState(userIndex, ref vib);
                Thread.Sleep(durationMs);
                vib = new XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = 0 };
                _xinputSetState(userIndex, ref vib);
            }
            catch { /* Best-effort rumble; a controller without motors just does not vibrate. */ }
        });
    }

    private static void OpenOnScreenKeyboard()
    {
        try
        {
            var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
            if (File.Exists(tabTip))
            {
                Process.Start(new ProcessStartInfo { FileName = tabTip, UseShellExecute = true });
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = "osk.exe", UseShellExecute = true });
        }
        catch (Exception caught) when (ExpectedFailures.Process(caught)) { /* No on-screen keyboard available; the controller keeps working without it. */ }
    }
}
