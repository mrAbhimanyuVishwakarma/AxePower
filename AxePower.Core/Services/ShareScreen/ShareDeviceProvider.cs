using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using AxePower.Core.Models.ShareScreen;
using NAudio.CoreAudioApi;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Reads the monitor list from GDI and the audio endpoint list from WASAPI.
///
/// Monitors are ordered by their adapter device name, which is the order DXGI reports outputs in
/// on the primary adapter, so the ordinal here is the one the capture filter expects.
/// </summary>
public sealed class ShareDeviceProvider : IShareDeviceProvider
{
    private readonly LogService _logService;

    public ShareDeviceProvider(LogService logService) => _logService = logService;

    // -- Displays ------------------------------------------------------------

    public IReadOnlyList<CaptureDisplay> GetDisplays()
    {
        var displays = new List<CaptureDisplay>();
        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0)) break;

                // Mirroring pseudo-adapters and detached outputs cannot be duplicated.
                if ((adapter.StateFlags & DisplayDeviceStateFlags.AttachedToDesktop) == 0) continue;
                if ((adapter.StateFlags & DisplayDeviceStateFlags.MirroringDriver) != 0) continue;

                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) continue;

                displays.Add(new CaptureDisplay(
                    OutputIndex: displays.Count,
                    DeviceName: adapter.DeviceName,
                    FriendlyName: GetMonitorName(adapter.DeviceName, displays.Count),
                    Width: mode.dmPelsWidth,
                    Height: mode.dmPelsHeight,
                    RefreshHz: mode.dmDisplayFrequency,
                    IsPrimary: (adapter.StateFlags & DisplayDeviceStateFlags.PrimaryDevice) != 0,
                    // Where this monitor sits in the virtual desktop. Negative values are normal
                    // for a screen placed left of or above the primary one.
                    Left: mode.dmPositionX,
                    Top: mode.dmPositionY));
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not enumerate displays", ex);
        }

        if (displays.Count == 0)
        {
            // Better a usable default than an empty picker: output 0 is the primary by definition.
            displays.Add(new CaptureDisplay(0, @"\\.\DISPLAY1", "Primary display", 1920, 1080, 60, true));
        }

        return displays;
    }

    /// <summary>The monitor's own name rather than the adapter's, when Windows knows it.</summary>
    private static string GetMonitorName(string adapterDeviceName, int ordinal)
    {
        try
        {
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (EnumDisplayDevices(adapterDeviceName, 0, ref monitor, 0) &&
                !string.IsNullOrWhiteSpace(monitor.DeviceString))
            {
                return $"Display {ordinal + 1} - {monitor.DeviceString.Trim()}";
            }
        }
        catch
        {
            // Fall through to the generic name.
        }

        return $"Display {ordinal + 1}";
    }

    // -- Audio ---------------------------------------------------------------

    public IReadOnlyList<ShareAudioDevice> GetRenderDevices() => GetEndpoints(DataFlow.Render);

    public IReadOnlyList<ShareAudioDevice> GetCaptureDevices() => GetEndpoints(DataFlow.Capture);

    private IReadOnlyList<ShareAudioDevice> GetEndpoints(DataFlow flow)
    {
        var devices = new List<ShareAudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (COMException)
            {
                // No endpoint of this kind is present - a PC with no microphone, for instance.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new ShareAudioDevice(
                        Id: device.ID,
                        Name: device.FriendlyName,
                        IsDefault: device.ID == defaultId,
                        IsCapture: flow == DataFlow.Capture));
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"[ShareScreen] Could not enumerate {flow} audio endpoints", ex);
        }

        return devices.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    // -- Win32 ---------------------------------------------------------------

    private const int ENUM_CURRENT_SETTINGS = -1;

    [Flags]
    private enum DisplayDeviceStateFlags
    {
        AttachedToDesktop = 0x01,
        PrimaryDevice = 0x04,
        MirroringDriver = 0x08
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public DisplayDeviceStateFlags StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
}
