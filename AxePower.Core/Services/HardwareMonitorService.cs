using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace AxePower.Core.Services;

/// <summary>One sampled set of sensor readings. Anything unavailable is <see cref="double.NaN"/>.</summary>
public class HardwareSnapshot
{
    /// <summary>CPU package (die) temperature in °C — the number Task Manager does not show but HWiNFO does.</summary>
    public double CpuPackageTempC { get; set; } = double.NaN;

    /// <summary>Highest core temperature in °C, used when the package sensor is absent.</summary>
    public double CpuMaxCoreTempC { get; set; } = double.NaN;

    /// <summary>Effective clock in GHz, averaged over the active cores.</summary>
    public double CpuClockGhz { get; set; } = double.NaN;

    /// <summary>Package power draw in watts.</summary>
    public double CpuPowerW { get; set; } = double.NaN;

    /// <summary>Discrete GPU core temperature in °C.</summary>
    public double GpuTempC { get; set; } = double.NaN;

    /// <summary>Discrete GPU core clock in GHz.</summary>
    public double GpuClockGhz { get; set; } = double.NaN;

    /// <summary>Discrete GPU core load percentage.</summary>
    public double GpuLoadPercent { get; set; } = double.NaN;

    /// <summary>The name the sensor library reports for the discrete GPU.</summary>
    public string? GpuName { get; set; }

    /// <summary>Which backend produced this snapshot, for logging and the settings screen.</summary>
    public string Source { get; set; } = "None";
}

/// <summary>
/// Reads the sensors the OS does not expose, trying three backends in order of reliability:
///
/// 1. <see cref="LenovoSensorReader"/> — the firmware's own ACPI-WMI interface. Needs no kernel
///    driver, so it keeps working on machines with Memory Integrity enabled. Preferred wherever
///    it exists, which is every Legion/LOQ/IdeaPad Gaming laptop.
/// 2. <see cref="AfterburnerSensorReader"/> — if MSI Afterburner is running it republishes its
///    readings in shared memory. Free to read and works unelevated.
/// 3. LibreHardwareMonitor — loads its own kernel driver to read the CPU's thermal MSRs.
///
/// The ordering is deliberate. The MSR route is what most tools use, but on current Windows 11
/// the driver it needs (WinRing0) is on Microsoft's vulnerable driver blocklist: with Memory
/// Integrity or the blocklist enabled — the default on new machines — it silently fails to load
/// and every reading comes back empty. The firmware route is unaffected, so it goes first.
///
/// All three need administrator rights, which is why <see cref="Models.AppSettings.RunAsAdministrator"/>
/// defaults to on.
///
/// The ACPI thermal zone AxePower used before this service (<c>\_TZ.TZ00</c>) is a chassis/skin
/// sensor — measured here, it sat at 27.9 °C through 25 seconds of full load, which is why the
/// dashboard read wrong. It survives only as a last-resort fallback, explicitly labelled.
/// </summary>
public class HardwareMonitorService : IDisposable
{
    private readonly LogService? _logService;
    private readonly object _lock = new();

    private Computer? _computer;
    private LenovoSensorReader? _lenovo;
    private bool _initialised;
    private bool _isDisposed;
    private string? _loggedSource;

    /// <summary>
    /// Set once the library has been asked for a reading and could not produce one — typically
    /// because the process is not elevated, or the driver is blocked. Sampling then stops.
    /// </summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>Why <see cref="IsUnavailable"/> was set, for the UI to show instead of a number.</summary>
    public string? UnavailableReason { get; private set; }

    public HardwareMonitorService(LogService? logService = null)
    {
        _logService = logService;
    }

    private bool EnsureInitialised()
    {
        if (IsUnavailable || _isDisposed) return false;
        if (_initialised) return true;

        if (!ElevationService.CheckElevated())
        {
            // The MSR driver cannot be loaded from a standard-user token. Fail fast rather than
            // letting the library install half a session's worth of state for nothing.
            IsUnavailable = true;
            UnavailableReason = "Administrator rights are required to read CPU temperature.";
            _logService?.LogInfo("Hardware sensors unavailable: AxePower is not running elevated.");
            return false;
        }

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = false,
                IsMotherboardEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsBatteryEnabled = false
            };
            _computer.Open();
            _initialised = true;
            _logService?.LogInfo("Hardware sensor monitoring started (elevated).");
            return true;
        }
        catch (Exception ex)
        {
            IsUnavailable = true;
            UnavailableReason = "The hardware sensor driver could not be loaded.";
            _logService?.LogWarning($"Hardware sensors unavailable: {ex.Message}");
            _computer = null;
            return false;
        }
    }

    /// <summary>
    /// Takes one reading of every enabled sensor. Safe to call from a timer thread; the
    /// underlying library is not thread-safe so calls are serialised.
    /// </summary>
    public HardwareSnapshot Sample()
    {
        // 1. Lenovo firmware. Driver-free, so it survives Memory Integrity; on this hardware it
        //    is the only source that reports a CPU temperature at all.
        var snapshot = ReadFromLenovoFirmware();
        if (!double.IsNaN(snapshot.CpuPackageTempC))
        {
            LogSourceOnce(snapshot.Source);
            // The firmware reports temperature but no clock or load, so fill those in from the
            // GPU-side library reading where one is available.
            MergeGpuDetail(snapshot);
            return snapshot;
        }

        // 2. Afterburner, when it is running: a shared-memory read, no elevation needed.
        snapshot = ReadFromAfterburner();
        if (!double.IsNaN(snapshot.CpuPackageTempC))
        {
            LogSourceOnce(snapshot.Source);
            return snapshot;
        }

        lock (_lock)
        {
            if (!EnsureInitialised() || _computer == null) return snapshot;

            try
            {
                snapshot.Source = "LibreHardwareMonitor";
                foreach (var hardware in _computer.Hardware)
                {
                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            ReadCpu(hardware, snapshot);
                            break;
                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                            ReadGpu(hardware, snapshot);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"Hardware sensor read failed: {ex.Message}");
            }
        }

        // The library loaded but the CPU exposes nothing readable — stop paying for the polling.
        if (_initialised && double.IsNaN(snapshot.CpuPackageTempC) && double.IsNaN(snapshot.CpuMaxCoreTempC))
        {
            IsUnavailable = true;
            UnavailableReason = "This processor does not expose a readable temperature sensor.";
            _logService?.LogWarning("Hardware sensors reported no CPU temperature; disabling sensor polling.");
        }

        return snapshot;
    }

    private void LogSourceOnce(string source)
    {
        if (_loggedSource == source) return;
        _loggedSource = source;
        _logService?.LogInfo($"Reading hardware sensors from {source}.");
    }

    private HardwareSnapshot ReadFromLenovoFirmware()
    {
        var snapshot = new HardwareSnapshot();

        _lenovo ??= new LenovoSensorReader(_logService);

        double cpu = _lenovo.GetCpuTempC();
        if (double.IsNaN(cpu)) return snapshot;

        snapshot.Source = "Lenovo firmware (ACPI-WMI)";
        snapshot.CpuPackageTempC = cpu;
        snapshot.GpuTempC = _lenovo.GetGpuTempC();
        return snapshot;
    }

    /// <summary>
    /// The firmware interface reports temperatures only. Clock and load still come from the
    /// sensor library when it is usable, so the dashboard keeps a full GPU readout.
    /// </summary>
    private void MergeGpuDetail(HardwareSnapshot snapshot)
    {
        if (IsUnavailable) return;

        lock (_lock)
        {
            if (!EnsureInitialised() || _computer == null) return;

            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd))
                        continue;

                    var gpu = new HardwareSnapshot();
                    ReadGpu(hardware, gpu);

                    if (!double.IsNaN(gpu.GpuClockGhz)) snapshot.GpuClockGhz = gpu.GpuClockGhz;
                    if (!double.IsNaN(gpu.GpuLoadPercent)) snapshot.GpuLoadPercent = gpu.GpuLoadPercent;
                    if (double.IsNaN(snapshot.GpuTempC)) snapshot.GpuTempC = gpu.GpuTempC;
                    snapshot.GpuName ??= gpu.GpuName;
                }
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"GPU detail read failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Maps Afterburner's published source names onto a snapshot. Its CPU temperature row is the
    /// package reading, so it lands in <see cref="HardwareSnapshot.CpuPackageTempC"/> directly.
    /// </summary>
    private static HardwareSnapshot ReadFromAfterburner() => FromAfterburner(AfterburnerSensorReader.TryReadSensors());

    /// <summary>Absent rows, and the placeholder zeros Afterburner publishes for them, stay NaN (unknown).</summary>
    internal static HardwareSnapshot FromAfterburner(Dictionary<string, float> sensors)
    {
        var snapshot = new HardwareSnapshot();
        if (sensors.Count == 0) return snapshot;

        snapshot.Source = "MSI Afterburner";

        // Multi-die parts publish "CPU1 temperature"; the plain row is the package on the rest.
        snapshot.CpuPackageTempC = AfterburnerSensorReader.Find(
            sensors, 1, 125, "CPU temperature", "CPU1 temperature", "CPU package temperature");

        double cpuClockMhz = AfterburnerSensorReader.Find(sensors, 1, 10000, "CPU clock", "CPU1 clock");
        if (!double.IsNaN(cpuClockMhz)) snapshot.CpuClockGhz = cpuClockMhz / 1000.0;

        snapshot.CpuPowerW = AfterburnerSensorReader.Find(sensors, 0.1, 500, "CPU power");

        snapshot.GpuTempC = AfterburnerSensorReader.Find(sensors, 1, 125, "GPU temperature");
        snapshot.GpuLoadPercent = AfterburnerSensorReader.Find(sensors, 0, 100, "GPU usage");

        double gpuClockMhz = AfterburnerSensorReader.Find(sensors, 1, 10000, "Core clock");
        if (!double.IsNaN(gpuClockMhz)) snapshot.GpuClockGhz = gpuClockMhz / 1000.0;

        return snapshot;
    }

    /// <summary>Sensors without a reading (null or NaN) are skipped, so a missing value stays NaN.</summary>
    internal static void ReadCpu(IHardware hardware, HardwareSnapshot snapshot)
    {
        hardware.Update();

        double maxCore = double.NaN;
        double clockSum = 0;
        int clockCount = 0;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not float raw || float.IsNaN(raw)) continue;
            double value = raw;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    // "CPU Package" on Intel, "Core (Tctl/Tdie)" on AMD — either is the die reading
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                    {
                        snapshot.CpuPackageTempC = value;
                    }
                    else if (sensor.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) &&
                             (double.IsNaN(maxCore) || value > maxCore))
                    {
                        maxCore = value;
                    }
                    break;

                case SensorType.Clock:
                    // Per-core clocks only; the bus clock is a different sensor entirely
                    if (sensor.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) && value > 0)
                    {
                        clockSum += value;
                        clockCount++;
                    }
                    break;

                case SensorType.Power:
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        snapshot.CpuPowerW = value;
                    break;
            }
        }

        snapshot.CpuMaxCoreTempC = maxCore;
        if (clockCount > 0)
            snapshot.CpuClockGhz = clockSum / clockCount / 1000.0;
    }

    internal static void ReadGpu(IHardware hardware, HardwareSnapshot snapshot)
    {
        hardware.Update();
        snapshot.GpuName = hardware.Name;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not float raw || float.IsNaN(raw)) continue;
            double value = raw;

            switch (sensor.SensorType)
            {
                case SensorType.Temperature when sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                    snapshot.GpuTempC = value;
                    break;
                case SensorType.Clock when sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                    snapshot.GpuClockGhz = value / 1000.0;
                    break;
                case SensorType.Load when sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase):
                    snapshot.GpuLoadPercent = value;
                    break;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _computer?.Close(); } catch { /* Best-effort teardown of the sensor driver at shutdown. */ }
            _computer = null;

            try { _lenovo?.Dispose(); } catch { /* Best-effort teardown of the Lenovo sensor reader at shutdown. */ }
            _lenovo = null;
        }
    }
}
