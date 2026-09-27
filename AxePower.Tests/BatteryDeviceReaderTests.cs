using System;
using System.Linq;
using AxePower.Core.Services;

namespace AxePower.Tests;

public class BatteryDeviceReaderTests
{
    // Lenovo's battery record as read from a LOQ 15IRX9 (Sunwoda L23D4PK4) through \\.\EnergyDrv.
    // Its capacities match Windows (6000/5279 x 10 mWh), so the layout is known good.
    private static readonly byte[] LoqRecord = Convert.FromHexString(
        "70179F1437103710FFFF47400000F50BDA581759503C4C6950000000000000004C32334434504B345375" +
        "6E776F6461003230323300000000000000000000000000000000000000000000000700D67602010500");

    [Fact]
    public void LenovoRecord_GivesBatteryTemperatureAndDates()
    {
        Assert.Equal(83, LoqRecord.Length);
        var decoded = BatteryDeviceReader.DecodeLenovoRecord(LoqRecord);

        Assert.NotNull(decoded);
        Assert.Equal(32.9, decoded!.Value.TemperatureC);
        Assert.Equal(new DateTime(2024, 6, 26), decoded.Value.ManufactureDate);
        Assert.Equal(new DateTime(2024, 8, 23), decoded.Value.FirstUseDate);
    }

    [Fact]
    public void EmptyBatterySlot_IsSkipped()
    {
        Assert.Null(BatteryDeviceReader.DecodeLenovoRecord(new byte[83]));
        Assert.Null(BatteryDeviceReader.DecodeLenovoRecord(Enumerable.Repeat((byte)0xFF, 83).ToArray()));
    }

    [Fact]
    public void ImplausibleValues_AreDroppedRatherThanShown()
    {
        var record = (byte[])LoqRecord.Clone();
        BitConverter.GetBytes((ushort)62720).CopyTo(record, 14);   // 5999 °C
        BitConverter.GetBytes((ushort)0x1234).CopyTo(record, 16);  // month 1, but 1989
        var decoded = BatteryDeviceReader.DecodeLenovoRecord(record);

        Assert.NotNull(decoded);
        Assert.Null(decoded!.Value.TemperatureC);
        Assert.Null(decoded.Value.ManufactureDate);
    }

    [Theory]
    [InlineData("LION", "Lithium-Ion")]
    [InlineData("LiP ", "Lithium-Polymer")]
    [InlineData("    ", null)]
    [InlineData("\0\0\0\0", null)]
    [InlineData("\u0001\u0002 \u0003", null)]
    public void ChemistryCode_IsNamed_OrLeftUnreported(string code, string? expected) =>
        Assert.Equal(expected, BatteryDeviceReader.ChemistryName(code));
}
