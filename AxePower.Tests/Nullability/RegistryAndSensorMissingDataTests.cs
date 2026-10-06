using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Services;
using AxePower.Core.Services.Uninstaller;
using AxePower.Tests.Uninstaller;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Moq;
using Xunit;

namespace AxePower.Tests.Nullability;

/// <summary>
/// Registry values that are absent or stored with the wrong type, and hardware sensors that report
/// nothing. Each must read as "unknown" (empty, null or NaN), never throw, and never invent a number.
/// </summary>
public class RegistryAndSensorMissingDataTests : IDisposable
{
    private readonly TestRegistryKey _key = new();

    public void Dispose() => _key.Dispose();

    private RegistryKey NewEntry(string name, Action<RegistryKey> fill)
    {
        var key = Registry.CurrentUser.CreateSubKey($@"{_key.SubKey}\{name}", writable: true);
        fill(key);
        return key;
    }

    // -- Registry: uninstall entries -------------------------------------------

    [Fact]
    public void UninstallEntry_WithWrongTypedValues_ReadsThemAsAbsent()
    {
        using var key = NewEntry("WrongTypes", k =>
        {
            k.SetValue("DisplayName", 42, RegistryValueKind.DWord);
            k.SetValue("Publisher", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            k.SetValue("EstimatedSize", "not a number", RegistryValueKind.String);
            k.SetValue("SystemComponent", " 1 ", RegistryValueKind.String);
            k.SetValue("NoRemove", new byte[] { 1 }, RegistryValueKind.Binary);
            k.SetValue("UninstallString", new[] { @"C:\App\uninst.exe", "/S" }, RegistryValueKind.MultiString);
        });

        var values = UninstallEntryValues.FromRegistryKey(key);

        Assert.Equal(string.Empty, values.DisplayName);
        Assert.Equal(string.Empty, values.Publisher);
        Assert.Equal(0, values.EstimatedSize);
        Assert.Equal(1, values.SystemComponent);
        Assert.Null(values.NoRemove);
        Assert.Equal(@"C:\App\uninst.exe /S", values.UninstallString);
        // No usable name: not listed as an application.
        Assert.Null(UninstallEntryClassifier.Classify(values, "WrongTypes", $@"{_key.FullPath}\WrongTypes", new FakeFileSystemProbe(), new FakeMsiReader()));
    }

    [Fact]
    public void UninstallEntry_WithOnlyAName_IsListed_WithoutAnUninstallCommand()
    {
        using var key = NewEntry("NameOnly", k =>
        {
            k.SetValue("DisplayName", "Name Only App");
            k.SetValue("EstimatedSize", -5, RegistryValueKind.DWord);
        });

        var values = UninstallEntryValues.FromRegistryKey(key);
        var app = UninstallEntryClassifier.Classify(values, "NameOnly", $@"{_key.FullPath}\NameOnly", new FakeFileSystemProbe(), new FakeMsiReader());

        Assert.Equal(0, values.EstimatedSize); // a negative size is not a size
        Assert.NotNull(app);
        Assert.Equal("Name Only App", app.DisplayName);
        Assert.Equal(string.Empty, app.UninstallString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void UninstallCommand_ThatIsMissing_DoesNotParse(string? raw)
    {
        Assert.False(UninstallCommandParser.TryParse(raw, new FakeFileSystemProbe(), out var command));
        Assert.Null(command);
    }

    // -- Hardware sensors -------------------------------------------------------

    private static ISensor Sensor(SensorType type, string name, float? value)
    {
        var sensor = new Mock<ISensor>();
        sensor.SetupGet(s => s.SensorType).Returns(type);
        sensor.SetupGet(s => s.Name).Returns(name);
        sensor.SetupGet(s => s.Value).Returns(value);
        return sensor.Object;
    }

    private static IHardware Hardware(string name, params ISensor[] sensors)
    {
        var hardware = new Mock<IHardware>();
        hardware.SetupGet(h => h.Name).Returns(name);
        hardware.SetupGet(h => h.Sensors).Returns(sensors);
        return hardware.Object;
    }

    [Fact]
    public void Cpu_WithoutReadings_StaysUnknown()
    {
        var snapshot = new HardwareSnapshot();

        HardwareMonitorService.ReadCpu(Hardware("CPU",
            Sensor(SensorType.Temperature, "CPU Package", null),
            Sensor(SensorType.Temperature, "Core #1", float.NaN),
            Sensor(SensorType.Clock, "CPU Core #1", null),
            Sensor(SensorType.Power, "CPU Package", float.NaN)), snapshot);

        Assert.True(double.IsNaN(snapshot.CpuPackageTempC));
        Assert.True(double.IsNaN(snapshot.CpuMaxCoreTempC));
        Assert.True(double.IsNaN(snapshot.CpuClockGhz));
        Assert.True(double.IsNaN(snapshot.CpuPowerW));
    }

    [Fact]
    public void Cpu_WithoutAPackageSensor_KeepsTheHottestCore_AndAveragesOnlyRealClocks()
    {
        var snapshot = new HardwareSnapshot();

        HardwareMonitorService.ReadCpu(Hardware("CPU",
            Sensor(SensorType.Temperature, "Core #1", 61),
            Sensor(SensorType.Temperature, "Core #2", null),
            Sensor(SensorType.Temperature, "Core #3", 74),
            Sensor(SensorType.Clock, "CPU Core #1", 4000),
            Sensor(SensorType.Clock, "CPU Core #2", null),
            Sensor(SensorType.Clock, "CPU Core #3", 0),
            Sensor(SensorType.Clock, "CPU Core #4", 3000)), snapshot);

        Assert.True(double.IsNaN(snapshot.CpuPackageTempC));
        Assert.Equal(74, snapshot.CpuMaxCoreTempC);
        Assert.Equal(3.5, snapshot.CpuClockGhz, 3);
    }

    [Fact]
    public void Gpu_WithoutReadings_StaysUnknown_ButKeepsItsName()
    {
        var snapshot = new HardwareSnapshot();

        HardwareMonitorService.ReadGpu(Hardware("NVIDIA GeForce RTX 4060 Laptop GPU",
            Sensor(SensorType.Temperature, "GPU Core", null),
            Sensor(SensorType.Clock, "GPU Core", float.NaN),
            Sensor(SensorType.Load, "GPU Core", null)), snapshot);

        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", snapshot.GpuName);
        Assert.True(double.IsNaN(snapshot.GpuTempC));
        Assert.True(double.IsNaN(snapshot.GpuClockGhz));
        Assert.True(double.IsNaN(snapshot.GpuLoadPercent));
    }

    [Fact]
    public void Afterburner_NotRunning_ReportsNothing()
    {
        var snapshot = HardwareMonitorService.FromAfterburner(new Dictionary<string, float>());

        Assert.Equal("None", snapshot.Source);
        Assert.True(double.IsNaN(snapshot.CpuPackageTempC));
        Assert.True(double.IsNaN(snapshot.GpuTempC));
    }

    [Fact]
    public void Afterburner_PlaceholderZeros_AreNotReadings()
    {
        var snapshot = HardwareMonitorService.FromAfterburner(new Dictionary<string, float>
        {
            ["CPU temperature"] = 0,   // placeholder for an absent sensor
            ["CPU1 temperature"] = 66,
            ["CPU clock"] = 0,
            ["GPU temperature"] = 0,
        });

        Assert.Equal("MSI Afterburner", snapshot.Source);
        Assert.Equal(66, snapshot.CpuPackageTempC);
        Assert.True(double.IsNaN(snapshot.CpuClockGhz));
        Assert.True(double.IsNaN(snapshot.GpuTempC));
        Assert.True(double.IsNaN(snapshot.CpuPowerW));
    }
}
