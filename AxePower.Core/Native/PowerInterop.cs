using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AxePower.Core.Native;

internal static class PowerInterop
{
    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_MORE_DATA = 234;
    private const uint ERROR_NO_MORE_ITEMS = 259;

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerEnumerate(
        IntPtr RootPowerKey,
        IntPtr SchemeGuid,
        IntPtr SubGroupOfPowerSettingsGuid,
        uint AccessFlags,
        uint Index,
        out Guid Buffer,
        ref uint BufferSize);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        IntPtr SubGroupOfPowerSettingsGuid,
        IntPtr PowerSettingGuid,
        IntPtr Buffer,
        ref uint BufferSize);

    public const int ACCESS_SCHEME = 16;

    public static Guid? GetActiveScheme()
    {
        IntPtr pGuid = IntPtr.Zero;
        try
        {
            uint res = PowerGetActiveScheme(IntPtr.Zero, out pGuid);
            if (res == ERROR_SUCCESS && pGuid != IntPtr.Zero)
            {
                return Marshal.PtrToStructure<Guid>(pGuid);
            }
            return null;
        }
        finally
        {
            if (pGuid != IntPtr.Zero)
            {
                // Documentation says to free using LocalFree, but usually CoTaskMemFree works or LocalFree 
                // powrprof allocates with LocalAlloc
                LocalFree(pGuid);
            }
        }
    }

    public static bool SetActiveScheme(Guid schemeGuid)
    {
        uint res = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
        return res == ERROR_SUCCESS;
    }

    public static List<Guid> EnumerateSchemes()
    {
        var schemes = new List<Guid>();
        uint index = 0;
        uint bufferSize = (uint)Marshal.SizeOf<Guid>();
        Guid buffer;

        while (true)
        {
            uint res = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, out buffer, ref bufferSize);
            if (res == ERROR_SUCCESS)
            {
                schemes.Add(buffer);
                index++;
            }
            else if (res == ERROR_NO_MORE_ITEMS)
            {
                break;
            }
            else
            {
                break;
            }
        }

        return schemes;
    }

    public static string? ReadFriendlyName(Guid schemeGuid)
    {
        uint bufferSize = 0;
        uint res = PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref bufferSize);
        if (res == ERROR_SUCCESS || res == ERROR_MORE_DATA)
        {
            IntPtr pBuffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                res = PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, pBuffer, ref bufferSize);
                if (res == ERROR_SUCCESS)
                {
                    return Marshal.PtrToStringUni(pBuffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBuffer);
            }
        }
        return null;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
