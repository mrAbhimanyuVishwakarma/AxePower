using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AxePower.Core.Services;

public class DisplayService
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
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;
    private const uint SDC_SAVE_TO_DATABASE = 0x00000200;

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

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        DISPLAYCONFIG_PATH_INFO[] pathArray,
        uint numModeInfoArrayElements,
        DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint flags);
    #endregion

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
    /// Sets the primary display's refresh rate to a specific frequency in Hz.
    /// Uses both modern Windows DisplayConfig (CCD) and GDI ChangeDisplaySettingsEx for maximum compatibility.
    /// </summary>
    public bool SetRefreshRate(int targetFrequency)
    {
        if (targetFrequency <= 0) return false;

        _logService.LogInfo($"Attempting to set display refresh rate to {targetFrequency}Hz...");

        bool ccdSuccess = TrySetRefreshRateCCD(targetFrequency);
        bool gdiSuccess = TrySetRefreshRateGDI(targetFrequency);

        bool success = ccdSuccess || gdiSuccess;
        if (success)
        {
            _logService.LogInfo($"Successfully applied display refresh rate to {targetFrequency}Hz (CCD: {ccdSuccess}, GDI: {gdiSuccess}).");
        }
        else
        {
            _logService.LogError($"Failed to change display refresh rate to {targetFrequency}Hz via both CCD and GDI.", null);
        }

        return success;
    }

    /// <summary>
    /// Attempts to set the primary display's refresh rate.
    /// Uses 60Hz if useLowest is true, otherwise uses the highest available refresh rate.
    /// </summary>
    public void SetSmartRefreshRate(bool useLowest)
    {
        var rates = GetSupportedRefreshRates();
        if (!rates.Any()) return;

        int targetHz = useLowest ? rates.Min() : rates.Max();
        if (useLowest && targetHz < 60 && rates.Contains(60))
        {
            targetHz = 60;
        }

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

    private bool TrySetRefreshRateCCD(int targetFrequency)
    {
        try
        {
            uint pathCount = 0;
            uint modeCount = 0;

            int queryResult = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, null!, ref modeCount, null!, IntPtr.Zero);
            if (queryResult != 0 || pathCount == 0 || modeCount == 0)
            {
                return false;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            queryResult = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (queryResult != 0)
            {
                return false;
            }

            for (int i = 0; i < pathCount; i++)
            {
                paths[i].targetInfo.refreshRate.Numerator = (uint)(targetFrequency * 1000);
                paths[i].targetInfo.refreshRate.Denominator = 1000;
            }

            for (int i = 0; i < modeCount; i++)
            {
                if (modes[i].infoType == 2) // 2 = TARGET
                {
                    modes[i].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq.Numerator = (uint)(targetFrequency * 1000);
                    modes[i].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq.Denominator = 1000;
                }
            }

            uint setFlags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE;
            int setResult = SetDisplayConfig(pathCount, paths, modeCount, modes, setFlags);

            if (setResult == 0)
            {
                return true;
            }

            // Retry with simple SDC_APPLY | SDC_ALLOW_CHANGES | SDC_USE_SUPPLIED_DISPLAY_CONFIG
            setFlags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;
            setResult = SetDisplayConfig(pathCount, paths, modeCount, modes, setFlags);
            return setResult == 0;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error in TrySetRefreshRateCCD ({targetFrequency}Hz)", ex);
            return false;
        }
    }
}
