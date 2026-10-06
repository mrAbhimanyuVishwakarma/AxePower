using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services;

public class DisplayService : IRefreshRateController
{
    private readonly LogService _logService;

    #region Win32 GDI Structures & P/Invoke
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DEVMODE
    {
        public const int CCHDEVICENAME = 32;
        public const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
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

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int CDS_GLOBAL = 0x08;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_BITSPERPEL = 0x00040000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
    #endregion

    #region Win32 CCD (Connecting and Configuring Displays) Structures & P/Invoke
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)]
        public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(0)]
        public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType; // 1 = SOURCE, 2 = TARGET
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    // HDR / wide-gamut state for a display target
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;              // bit 0 advancedColorSupported, bit 1 advancedColorEnabled
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        ref uint numPathArrayElements,
        ref uint numModeInfoArrayElements);
    #endregion

    /// <summary>Primary display state shown on the dashboard status bar.</summary>
    public class DisplayInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int CurrentRefreshRate { get; set; }

        /// <summary>"On", "Off" or "Not supported".</summary>
        public string HdrStatus { get; set; } = "Not supported";

        public string ResolutionDisplay => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "Unknown";

        /// <summary>The rate the display is actually running at.</summary>
        public string RefreshDisplay => CurrentRefreshRate > 0 ? $"{CurrentRefreshRate}Hz" : string.Empty;
    }

    /// <summary>Reads the primary display's real resolution, refresh rates and HDR state.</summary>
    public DisplayInfo GetDisplayInfo()
    {
        var info = new DisplayInfo();

        try
        {
            var mode = new DEVMODE();
            mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode))
            {
                info.Width = mode.dmPelsWidth;
                info.Height = mode.dmPelsHeight;
                info.CurrentRefreshRate = mode.dmDisplayFrequency;
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[Display] Could not read current display mode: {ex.Message}");
        }

        info.HdrStatus = GetHdrStatus();
        return info;
    }

    /// <summary>
    /// Queries the active display paths for HDR support. Returns "On" when any display has
    /// it enabled, "Off" when supported but disabled, otherwise "Not supported".
    /// </summary>
    private string GetHdrStatus()
    {
        try
        {
            uint pathCount = 0, modeCount = 0;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ref pathCount, ref modeCount) != 0)
                return "Not supported";

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return "Not supported";

            bool supported = false;
            for (int i = 0; i < pathCount; i++)
            {
                var request = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                        size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO)),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref request) != 0)
                    continue;

                bool advancedColorSupported = (request.value & 0x1) != 0;
                bool advancedColorEnabled = (request.value & 0x2) != 0;

                if (advancedColorEnabled) return "On";
                if (advancedColorSupported) supported = true;
            }

            return supported ? "Off" : "Not supported";
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[Display] HDR query failed: {ex.Message}");
            return "Not supported";
        }
    }

    public DisplayService(LogService logService)
    {
        _logService = logService;
    }

    /// <summary>
    /// Gets all supported refresh rates for the primary display.
    /// </summary>
    public List<int> GetSupportedRefreshRates()
    {
        var supportedRefreshRates = new HashSet<int>();

        try
        {
            DEVMODE currentMode = new DEVMODE();
            currentMode.dmSize = (short)Marshal.SizeOf(currentMode);

            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref currentMode))
            {
                int currentWidth = currentMode.dmPelsWidth;
                int currentHeight = currentMode.dmPelsHeight;
                int currentBits = currentMode.dmBitsPerPel;

                if (currentMode.dmDisplayFrequency > 0)
                {
                    supportedRefreshRates.Add(currentMode.dmDisplayFrequency);
                }

                int i = 0;
                DEVMODE mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf(mode);

                while (EnumDisplaySettings(null, i, ref mode))
                {
                    if (mode.dmPelsWidth == currentWidth && mode.dmPelsHeight == currentHeight && mode.dmBitsPerPel == currentBits && mode.dmDisplayFrequency > 0)
                    {
                        supportedRefreshRates.Add(mode.dmDisplayFrequency);
                    }
                    i++;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Error enumerating GDI refresh rates", ex);
        }

        // Standard laptop/monitor rates fallback if none enumerated
        if (!supportedRefreshRates.Any())
        {
            supportedRefreshRates.Add(60);
            supportedRefreshRates.Add(120);
            supportedRefreshRates.Add(144);
            supportedRefreshRates.Add(165);
        }
        else if (supportedRefreshRates.Count == 1 && supportedRefreshRates.Contains(60))
        {
            // If only 60 detected, also add higher standard rates for selection
            supportedRefreshRates.Add(120);
            supportedRefreshRates.Add(144);
            supportedRefreshRates.Add(165);
        }

        return supportedRefreshRates.OrderBy(r => r).ToList();
    }

    /// <summary>
    /// Gets the current refresh rate of the primary display in Hz.
    /// </summary>
    public int GetCurrentRefreshRate()
    {
        try
        {
            DEVMODE currentMode = new DEVMODE();
            currentMode.dmSize = (short)Marshal.SizeOf(currentMode);

            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref currentMode) && currentMode.dmDisplayFrequency > 0)
            {
                return currentMode.dmDisplayFrequency;
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Error getting current refresh rate", ex);
        }
        return 60;
    }

    /// <summary>
    /// Sets the primary display's refresh rate to a specific frequency in Hz, through GDI
    /// (ChangeDisplaySettingsEx on the primary display only). A DisplayConfig (CCD) path ran first
    /// until 2026-10-06, but its first QueryDisplayConfig call passed null arrays and always failed
    /// with ERROR_INVALID_PARAMETER, so every change in practice came from GDI. It was removed rather
    /// than repaired, because repairing it would start rewriting the saved display configuration.
    /// </summary>
    public bool SetRefreshRate(int targetFrequency)
    {
        if (targetFrequency <= 0) return false;

        _logService.LogInfo($"Attempting to set display refresh rate to {targetFrequency}Hz...");

        bool success = TrySetRefreshRateGDI(targetFrequency);
        if (success)
        {
            _logService.LogInfo($"Successfully applied display refresh rate to {targetFrequency}Hz.");
        }
        else
        {
            _logService.LogError($"Failed to change display refresh rate to {targetFrequency}Hz.", null);
        }

        return success;
    }

    /// <summary>
    /// Attempts to set the primary display's refresh rate.
    /// Uses 60Hz if useLowest is true (the lowest rate when the panel has no 60Hz), otherwise the
    /// highest available refresh rate. Does nothing when the display is already there, because
    /// every change blanks the screen for a moment and this runs on each power-status event.
    /// </summary>
    public void SetSmartRefreshRate(bool useLowest)
    {
        var rates = GetSupportedRefreshRates();
        if (!rates.Any()) return;

        int targetHz = useLowest ? (rates.Contains(60) ? 60 : rates.Min()) : rates.Max();
        if (GetCurrentRefreshRate() == targetHz) return;

        SetRefreshRate(targetHz);
    }

    private bool TrySetRefreshRateGDI(int targetFrequency)
    {
        try
        {
            DEVMODE currentMode = new DEVMODE();
            currentMode.dmSize = (short)Marshal.SizeOf(currentMode);

            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref currentMode))
            {
                return false;
            }

            if (currentMode.dmDisplayFrequency == targetFrequency)
            {
                return true;
            }

            currentMode.dmDisplayFrequency = targetFrequency;
            currentMode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;

            // Apply dynamically and save
            int result1 = ChangeDisplaySettingsEx(null, ref currentMode, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_GLOBAL, IntPtr.Zero);
            int result2 = ChangeDisplaySettingsEx(null, ref currentMode, IntPtr.Zero, 0, IntPtr.Zero);

            return result1 == DISP_CHANGE_SUCCESSFUL || result2 == DISP_CHANGE_SUCCESSFUL;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error in TrySetRefreshRateGDI ({targetFrequency}Hz)", ex);
            return false;
        }
    }
}
