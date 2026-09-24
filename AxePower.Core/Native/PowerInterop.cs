using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

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

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr RootPowerKey, ref Guid SourceSchemeGuid, ref IntPtr DestinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteFriendlyName(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        IntPtr SubGroupOfPowerSettingsGuid,
        IntPtr PowerSettingGuid,
        byte[] Buffer,
        uint BufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDescription(
        IntPtr RootPowerKey,
        ref Guid SchemeGuid,
        IntPtr SubGroupOfPowerSettingsGuid,
        IntPtr PowerSettingGuid,
        byte[] Buffer,
        uint BufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr RootPowerKey, ref Guid SchemeGuid);

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

    /// <summary>
    /// Copies <paramref name="source"/> into a new scheme with the caller's own GUID. Passing a
    /// non-null destination pointer makes powrprof use that GUID instead of generating one.
    /// </summary>
    public static bool DuplicateScheme(Guid source, Guid destination)
    {
        IntPtr pDestination = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(destination, pDestination, false);
            IntPtr passed = pDestination;
            return PowerDuplicateScheme(IntPtr.Zero, ref source, ref passed) == ERROR_SUCCESS;
        }
        finally
        {
            Marshal.FreeHGlobal(pDestination);
        }
    }

    /// <summary>Fails for the active scheme; Windows will not delete the plan in use.</summary>
    public static bool DeleteScheme(Guid scheme) => PowerDeleteScheme(IntPtr.Zero, ref scheme) == ERROR_SUCCESS;

    public static bool WriteFriendlyName(Guid scheme, string name)
    {
        byte[] buffer = Encoding.Unicode.GetBytes(name + "\0");
        return PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, (uint)buffer.Length) == ERROR_SUCCESS;
    }

    public static bool WriteDescription(Guid scheme, string description)
    {
        byte[] buffer = Encoding.Unicode.GetBytes(description + "\0");
        return PowerWriteDescription(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, (uint)buffer.Length) == ERROR_SUCCESS;
    }

    public static bool WriteValueIndex(Guid scheme, Guid subgroup, Guid setting, uint acValue, uint dcValue)
    {
        bool ac = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, acValue) == ERROR_SUCCESS;
        bool dc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dcValue) == ERROR_SUCCESS;
        return ac && dc;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
