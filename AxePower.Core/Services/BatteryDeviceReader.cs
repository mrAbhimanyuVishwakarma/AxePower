using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AxePower.Core.Services;

/// <summary>What the battery itself reports, beyond what WMI publishes.</summary>
public sealed record BatteryDeviceDetails(
    string? SerialNumber,
    string? Chemistry,
    double? TemperatureC,
    DateTime? ManufactureDate,
    DateTime? FirstUseDate);

/// <summary>
/// Reads the battery the way the maker's own tools do. The serial and chemistry come from the
/// Windows battery driver (WMI's Win32_Battery.Name is the model, not the serial). Temperature and
/// the manufacture and first-use dates come from Lenovo's Energy driver, the record Lenovo
/// Vantage and Legion Toolkit read: Windows has no battery temperature on most laptops, and the
/// ACPI thermal zone it does expose is not the battery (it sits still under load).
/// </summary>
public static class BatteryDeviceReader
{
    public static BatteryDeviceDetails Read()
    {
        string? serial = null, chemistry = null;
        double? temperature = null;
        DateTime? manufactured = null, firstUse = null;

        try { (serial, chemistry, temperature, manufactured) = ReadWindowsBattery(); } catch { }
        try
        {
            var lenovo = ReadLenovoRecord();
            if (lenovo != null)
            {
                temperature = lenovo.Value.TemperatureC ?? temperature;
                manufactured = lenovo.Value.ManufactureDate ?? manufactured;
                firstUse = lenovo.Value.FirstUseDate;
            }
        }
        catch { }

        return new BatteryDeviceDetails(serial, chemistry, temperature, manufactured, firstUse);
    }

    // ── Lenovo Energy driver ───────────────────────────────────────────────

    private const uint IOCTL_ENERGY_BATTERY_INFORMATION = 0x83102138;
    private const int LenovoRecordLength = 83;

    /// <summary>
    /// Decodes Lenovo's battery record: temperature in tenths of a kelvin at byte 14, then the
    /// manufacture and first-use dates as DOS dates at 16 and 18. Unused battery slots come back
    /// as all zeros. Anything implausible is dropped rather than shown.
    /// </summary>
    public static (double? TemperatureC, DateTime? ManufactureDate, DateTime? FirstUseDate)? DecodeLenovoRecord(byte[] record)
    {
        if (record.Length < 20) return null;
        ushort rawTemperature = BitConverter.ToUInt16(record, 14);
        if (rawTemperature is 0 or ushort.MaxValue) return null;

        double celsius = (rawTemperature - 2731.6) / 10.0;
        double? temperature = celsius is > 0 and < 90 ? Math.Round(celsius, 1) : null;
        return (temperature, DecodeDosDate(BitConverter.ToUInt16(record, 16)), DecodeDosDate(BitConverter.ToUInt16(record, 18)));
    }

    /// <summary>A DOS date (years since 1980, month, day), or null when empty or implausible.</summary>
    public static DateTime? DecodeDosDate(ushort value)
    {
        if (value is 0 or ushort.MaxValue) return null;
        int year = (value >> 9) + 1980, month = (value >> 5) & 15, day = value & 31;
        if (year < 2010 || year > DateTime.Now.Year + 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        return new DateTime(year, month, day);
    }

    private static (double? TemperatureC, DateTime? ManufactureDate, DateTime? FirstUseDate)? ReadLenovoRecord()
    {
        using var driver = CreateFile(@"\\.\EnergyDrv", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (driver.IsInvalid) return null; // not a Lenovo, or Lenovo's driver isn't installed

        for (uint slot = 0; slot < 3; slot++)
        {
            var record = new byte[LenovoRecordLength];
            if (!DeviceIoControl(driver, IOCTL_ENERGY_BATTERY_INFORMATION, BitConverter.GetBytes(slot), 4, record, record.Length, out _, IntPtr.Zero))
                continue;
            var decoded = DecodeLenovoRecord(record);
            if (decoded != null) return decoded;
        }
        return null;
    }

    // ── Windows battery driver ─────────────────────────────────────────────

    private static readonly Guid BatteryInterfaceClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const int LevelInformation = 0, LevelTemperature = 2, LevelManufactureDate = 5, LevelSerialNumber = 8;

    private static (string? Serial, string? Chemistry, double? TemperatureC, DateTime? ManufactureDate) ReadWindowsBattery()
    {
        string? path = FirstBatteryPath();
        if (path == null) return default;

        using var battery = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0x80, IntPtr.Zero);
        if (battery.IsInvalid) return default;

        var tag = new byte[4];
        if (!DeviceIoControl(battery, IOCTL_BATTERY_QUERY_TAG, new byte[4], 4, tag, 4, out _, IntPtr.Zero)) return default;

        byte[]? Query(int level)
        {
            var input = new byte[12];
            tag.CopyTo(input, 0);
            BitConverter.GetBytes(level).CopyTo(input, 4);
            var output = new byte[256];
            return DeviceIoControl(battery, IOCTL_BATTERY_QUERY_INFORMATION, input, input.Length, output, output.Length, out int length, IntPtr.Zero)
                ? output.AsSpan(0, length).ToArray() : null;
        }

        string? serial = Query(LevelSerialNumber) is { } s ? Encoding.Unicode.GetString(s).TrimEnd('\0').Trim() : null;
        string? chemistry = Query(LevelInformation) is { Length: >= 8 } info ? ChemistryName(Encoding.ASCII.GetString(info, 4, 4)) : null;

        double? temperature = null;
        if (Query(LevelTemperature) is { Length: >= 4 } t)
        {
            double c = BitConverter.ToUInt32(t, 0) / 10.0 - 273.15;
            if (c is > 0 and < 90) temperature = Math.Round(c, 1);
        }

        DateTime? manufactured = null;
        if (Query(LevelManufactureDate) is { Length: >= 4 } d)
        {
            int day = d[0], month = d[1], year = BitConverter.ToUInt16(d, 2);
            if (year >= 2010 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
                manufactured = new DateTime(year, month, day);
        }

        return (string.IsNullOrWhiteSpace(serial) ? null : serial, chemistry, temperature, manufactured);
    }

    /// <summary>The battery's four-letter chemistry code in words, or null when it reports none.</summary>
    public static string? ChemistryName(string code) => new string(code.Where(char.IsLetter).ToArray()).ToUpperInvariant() switch
    {
        "" => null,
        "LION" or "LII" => "Lithium-Ion",
        "LIP" or "LIPO" => "Lithium-Polymer",
        "PBAC" => "Lead Acid",
        "NICD" => "Nickel Cadmium",
        "NIMH" => "Nickel Metal Hydride",
        "NIZN" => "Nickel Zinc",
        "RAM" => "Rechargeable Alkaline-Manganese",
        var other => other,
    };

    private static string? FirstBatteryPath()
    {
        var classGuid = BatteryInterfaceClass;
        IntPtr devices = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devices == new IntPtr(-1)) return null;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref classGuid, 0, ref data)) return null;

            SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr detail = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                return SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, size, out _, IntPtr.Zero)
                    ? Marshal.PtrToStringUni(detail + 4) : null;
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    // ── Native ─────────────────────────────────────────────────────────────

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr info, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int requiredSize, IntPtr info);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
}
