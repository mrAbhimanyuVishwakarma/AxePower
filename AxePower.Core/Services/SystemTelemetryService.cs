using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using AxePower.Core.Helpers;
using AxePower.Core.Models;
using Microsoft.Win32;

namespace AxePower.Core.Services;

public class DiskDriveSnapshot
{
    public string DriveLetter { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedGb { get; set; }
    public double UsedPercent { get; set; }
    public double FreePercent => Math.Max(0.001, 100.0 - UsedPercent);
    public string FreeSpaceDisplay { get; set; } = string.Empty;
    public string UsageDisplay { get; set; } = string.Empty;
    public string StatusColorHex { get; set; } = "#10B981";
}

public class SystemTelemetrySnapshot
{
    public double CpuPercent { get; set; }
    public string CpuDisplay { get; set; } = "—";

    public double GpuPercent { get; set; }
    public string GpuName { get; set; } = "GPU";
    public string GpuDisplay { get; set; } = "0%";

    /// <summary>True while the discrete GPU is the one doing the work.</summary>
    public bool IsDedicatedGpuActive { get; set; }

    public double MemoryPercent { get; set; }
    public string MemoryDisplay { get; set; } = "0.0 GB (0%)";

    public double NetworkPercent { get; set; }
    public string NetworkName { get; set; } = "NET";
    public string NetworkDisplay { get; set; } = "↑0 bps ↓0 bps";

    public List<DiskDriveSnapshot> Disks { get; set; } = new();

    /// <summary>
    /// CPU package temperature in °C, or NaN when no sensor could be read. Exposed raw so
    /// callers that need the number (graphs, thresholds) do not have to parse the display string.
    /// </summary>
    public double CpuTempC { get; set; } = double.NaN;

    /// <summary>
    /// False when <see cref="CpuTempC"/> came from the ACPI chassis zone rather than the
    /// processor die, so the UI can say so instead of passing off an ambient reading as the CPU.
    /// </summary>
    public bool IsCpuTempFromDie { get; set; }

    /// <summary>Discrete GPU temperature in °C, or NaN when unavailable.</summary>
    public double GpuTempC { get; set; } = double.NaN;
}

public class SystemTelemetryService : IDisposable
{
    private readonly LogService? _logService;
    private readonly HardwareMonitorService? _hardwareMonitor;
    private readonly SettingsService? _settingsService;
    private readonly Timer _timer;
    private bool _isDisposed;

    // CPU Win32 API
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime, out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    // RAM Win32 API
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private long _prevIdleTime;
    private long _prevKernelTime;
    private long _prevUserTime;
    private bool _hasCpuSample;

    // Network Tracking per interface
    private readonly Dictionary<string, (long PrevSent, long PrevRecv, DateTime PrevTime)> _netStats = new();
    private List<NetworkInterface>? _cachedInterfaces;

    // Disk usage changes on a minutes scale — sampled every DiskSampleInterval ticks
    private const int DiskSampleInterval = 30;
    private List<DiskDriveSnapshot> _cachedDisks = new();
    private int _tickCount;

    // Discovered Devices Metadata
    private double _baseCpuMhz = 2400;

    /// <summary>Processor model as reported by the CPU itself, e.g. "13th Gen Intel(R) Core(TM) i7-13650HX".</summary>
    public string CpuName { get; private set; } = "Unknown processor";

    private readonly List<(string GpuName, int Index)> _discoveredGpus = new();
    private string? _integratedGpuName;
    private string? _dedicatedGpuName;

    // CPU temperature. The die sensor comes from HardwareMonitorService (MSR, needs admin); the
    // ACPI thermal zone is only a fallback and reads chassis temperature, not the processor.
    private bool _acpiTempUnavailable;
    private double _cachedCpuTempC = double.NaN;
    private bool _cpuTempIsFromDie;

    // Live CPU clock. Preferred source is the per-core sensor set; the WMI performance counter
    // below is the fallback and is only queried on the slow cadence — it used to run every tick.
    private double _cachedCpuGhz = double.NaN;

    // Network latency (sampled on a slow cadence; NaN while offline/unreachable)
    private double _cachedPingMs = double.NaN;

    // GPU clock/temperature via nvidia-smi (sampled on a slow cadence; self-disables when absent)
    private bool _nvidiaSmiUnavailable;

    // True once the sensor library has supplied the discrete GPU's readings, which makes the
    // nvidia-smi process spawn redundant. Set per poll; _freshGpuSample drives the hysteresis.
    private bool _gpuSensorsFromMonitor;
    private bool _freshGpuSample;
    private double _gpuClockGhz = double.NaN;
    private double _gpuTempC = double.NaN;
    private double _gpuSmiUtil = double.NaN;

    // Which GPU is "active" is latched with hysteresis: switching on a bare threshold made the
    // readout flip between the discrete and integrated GPU every tick as load fluctuated.
    private bool _dedicatedActiveState;
    private int _dedicatedIdleSamples;
    private const double DedicatedOnThresholdPercent = 10.0;
    private const double DedicatedOffThresholdPercent = 3.0;
    private const int DedicatedOffSamplesRequired = 3;   // ~15s below the off threshold

    public event Action<SystemTelemetrySnapshot>? TelemetryUpdated;

    public SystemTelemetryService(
        LogService? logService = null,
        HardwareMonitorService? hardwareMonitor = null,
        SettingsService? settingsService = null)
    {
        _logService = logService;
        _hardwareMonitor = hardwareMonitor;
        _settingsService = settingsService;
        InitializeHardwareTopology();

        // The adapter set only changes on connect/disconnect — invalidate the cache then
        try
        {
            NetworkChange.NetworkAddressChanged += (s, e) => _cachedInterfaces = null;
            NetworkChange.NetworkAvailabilityChanged += (s, e) => _cachedInterfaces = null;
        }
        catch { /* Without change notifications the adapter cache simply refreshes on its timer. */ }

        // 1-second rolling tick for Task Manager accuracy
        _timer = new Timer(OnTimerTick, null, 200, 1000);
    }

    /// <summary>Stops sampling (e.g. while the window is hidden in the tray).</summary>
    public void Pause()
    {
        if (_isDisposed) return;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Resumes 1-second sampling.</summary>
    public void Resume()
    {
        if (_isDisposed) return;
        _timer.Change(200, 1000);
    }

    private void InitializeHardwareTopology()
    {
        // 1. CPU Clock
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key != null)
            {
                var mhz = key.GetValue("~MHz");
                if (mhz is int mhzInt && mhzInt > 0)
                {
                    _baseCpuMhz = mhzInt;
                }

                if (key.GetValue("ProcessorNameString") is string cpuName && !string.IsNullOrWhiteSpace(cpuName))
                {
                    CpuName = cpuName.Trim();
                }
            }
        }
        catch (Exception caught) when (ExpectedFailures.Registry(caught)) { /* Unreadable: the CPU name and base clock stay unknown. */ }

        // 2. Discover GPUs via WMI
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            int gpuIndex = 0;
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _discoveredGpus.Add((name, gpuIndex));
                    gpuIndex++;
                }
            }
        }
        catch (Exception caught) when (ExpectedFailures.Wmi(caught)) { /* No GPU list from WMI: GPUs are discovered from the sensors instead. */ }

        if (_discoveredGpus.Count == 0)
        {
            _discoveredGpus.Add(("GPU", 0));
        }

        // Classify integrated vs dedicated by adapter name
        foreach (var (name, _) in _discoveredGpus)
        {
            if (Regex.IsMatch(name, "NVIDIA|GeForce|RTX|GTX", RegexOptions.IgnoreCase))
                _dedicatedGpuName ??= name;
            else if (Regex.IsMatch(name, "Intel|Iris|UHD|Radeon|Vega", RegexOptions.IgnoreCase))
                _integratedGpuName ??= name;
        }
    }


    private void OnTimerTick(object? state)
    {
        if (_isDisposed) return;

        try
        {
            _tickCount++;
            var snapshot = CollectAllMetrics();
            TelemetryUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _logService?.LogWarning($"Telemetry sampling error: {ex.Message}");
        }
    }

    private SystemTelemetrySnapshot CollectAllMetrics()
    {
        var snapshot = new SystemTelemetrySnapshot();

        // 1. CPU
        double cpuPercent = 0;
        if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            long idle = FileTimeToLong(idleFt);
            long kernel = FileTimeToLong(kernelFt);
            long user = FileTimeToLong(userFt);

            if (_hasCpuSample)
            {
                long idleDiff = idle - _prevIdleTime;
                long kernelDiff = kernel - _prevKernelTime;
                long userDiff = user - _prevUserTime;
                long totalDiff = kernelDiff + userDiff;

                if (totalDiff > 0)
                {
                    cpuPercent = Math.Clamp((1.0 - ((double)idleDiff / totalDiff)) * 100.0, 0, 100);
                }
            }

            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;
            _hasCpuSample = true;
        }

        SampleCpuSensors();

        double cpuGhz = double.IsNaN(_cachedCpuGhz) ? _baseCpuMhz / 1000.0 : _cachedCpuGhz;

        snapshot.CpuPercent = cpuPercent;
        snapshot.CpuTempC = _cachedCpuTempC;
        snapshot.IsCpuTempFromDie = _cpuTempIsFromDie;
        snapshot.CpuDisplay = $"{cpuGhz:0.00} GHz  {cpuPercent:0}%  {FormatTemp(_cachedCpuTempC)}";

        // 2. Memory (RAM)
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGb = totalGb - availGb;
            double memPercent = Math.Clamp((usedGb / totalGb) * 100.0, 0, 100);

            snapshot.MemoryPercent = memPercent;
            snapshot.MemoryDisplay = $"{usedGb:0.0} / {totalGb:0.0} GB ({memPercent:0}%)";
        }

        // 3. Network (Active Wi-Fi or Ethernet)
        try
        {
            var now = DateTime.UtcNow;
            var activeInterfaces = _cachedInterfaces ??= NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                           (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                .ToList();

            if (activeInterfaces.Count > 0)
            {
                double totalSendBps = 0;
                double totalRecvBps = 0;
                double linkBitsPerSec = 0;

                foreach (var ni in activeInterfaces)
                {
                    // Scale the meter against what the link can actually carry. Speed is -1 on
                    // adapters that do not report it, and those are simply left out of the total.
                    try { if (ni.Speed > 0) linkBitsPerSec += ni.Speed; } catch { /* Adapters that do not report a speed are left out of the total. */ }

                    var stats = ni.GetIPStatistics();
                    long currentSent = stats.BytesSent;
                    long currentRecv = stats.BytesReceived;

                    if (_netStats.TryGetValue(ni.Id, out var prev))
                    {
                        double seconds = (now - prev.PrevTime).TotalSeconds;
                        if (seconds > 0)
                        {
                            totalSendBps += Math.Max(0, currentSent - prev.PrevSent) / seconds;
                            totalRecvBps += Math.Max(0, currentRecv - prev.PrevRecv) / seconds;
                        }
                    }

                    _netStats[ni.Id] = (currentSent, currentRecv, now);
                }

                string sendStr = FormatBandwidth(totalSendBps);
                string recvStr = FormatBandwidth(totalRecvBps);
                double totalBitsPerSec = (totalSendBps + totalRecvBps) * 8;
                // Percentage of the negotiated link rate. The previous fixed 10 Mbps full-scale
                // pinned the meter at 100% on any modern Wi-Fi or gigabit link.
                double scaleBitsPerSec = linkBitsPerSec > 0 ? linkBitsPerSec : 100.0 * 1000 * 1000;
                snapshot.NetworkPercent = Math.Clamp((totalBitsPerSec / scaleBitsPerSec) * 100.0, 0, 100);
                // WiFi for a single wireless link, LAN for a single wired one, NET when several are active
                snapshot.NetworkName = activeInterfaces.Count > 1
                    ? "NET"
                    : activeInterfaces[0].NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "WiFi" : "LAN";
                SampleNetworkPing();
                snapshot.NetworkDisplay = double.IsNaN(_cachedPingMs)
                    ? $"↓{recvStr}  ↑{sendStr}"
                    : $"{_cachedPingMs:0}ms  ↓{recvStr}  ↑{sendStr}";
            }
            else
            {
                snapshot.NetworkName = "NET";
                snapshot.NetworkDisplay = "↓0 bps  ↑0 bps";
                snapshot.NetworkPercent = 0;
            }
        }
        catch { /* Best-effort sample: this second's value is skipped. */ }

        // 4. GPU — iGPU and dGPU are separate adapters with distinct LUIDs in the engine counters,
        // so aggregate 3D-engine load per LUID and follow whichever adapter is currently active
        var gpuUtilMap = new Dictionary<string, double>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D%'");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (ulong.TryParse(obj["UtilizationPercentage"]?.ToString(), out ulong util))
                {
                    var m = Regex.Match(name, @"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)");
                    string key = m.Success ? m.Groups[1].Value : "unknown";
                    gpuUtilMap[key] = gpuUtilMap.GetValueOrDefault(key) + util;
                }
            }
        }
        catch (Exception caught) when (ExpectedFailures.Wmi(caught)) { /* No GPU counters this second: utilisation shows as 0. */ }

        // The sensor library, when elevated, already reported this GPU in SampleCpuSensors;
        // nvidia-smi is only spawned when it did not.
        bool freshGpuSample = _freshGpuSample || (!_gpuSensorsFromMonitor && SampleNvidiaSmi());
        _freshGpuSample = false;

        double wmiActiveUtil = gpuUtilMap.Count > 0 ? Math.Clamp(gpuUtilMap.Values.Max(), 0, 100) : double.NaN;

        // Decide which GPU is active from the dedicated GPU's own utilization alone — mixing it
        // with the WMI counters compared a 5s-old number against a 1s-old one and oscillated. The
        // state is re-evaluated only when a fresh sample arrives and is held steady in between.
        if (double.IsNaN(_gpuSmiUtil) || (_nvidiaSmiUnavailable && !_gpuSensorsFromMonitor))
        {
            _dedicatedActiveState = false;
        }
        else if (_integratedGpuName == null)
        {
            _dedicatedActiveState = true;                 // single-GPU (NVIDIA) system
        }
        else if (freshGpuSample)
        {
            (_dedicatedActiveState, _dedicatedIdleSamples) =
                EvaluateDedicatedGpuState(_dedicatedActiveState, _dedicatedIdleSamples, _gpuSmiUtil);
        }

        bool dedicatedActive = _dedicatedActiveState;
        snapshot.IsDedicatedGpuActive = dedicatedActive;

        snapshot.GpuName = "GPU";

        // The discrete card has a real temperature whether or not it is the adapter currently
        // doing the work, so the raw value is always carried even when the display follows the
        // integrated adapter and shows no temperature of its own.
        snapshot.GpuTempC = _gpuTempC;

        if (dedicatedActive)
        {
            // Prefer the 1s WMI sample (the busiest adapter is the dGPU here); smi refreshes only every 5s
            double pct = Math.Clamp(!double.IsNaN(wmiActiveUtil) ? wmiActiveUtil : _gpuSmiUtil, 0, 100);
            snapshot.GpuPercent = pct;
            snapshot.GpuDisplay = FormatGpuDisplay(_gpuClockGhz, pct, _gpuTempC);
        }
        else if (!double.IsNaN(wmiActiveUtil))
        {
            // Load and clock follow whichever adapter is doing the work, but temperature always
            // describes the discrete card — it is the only one with a sensor, and an idle GPU
            // still has a real temperature worth showing. The integrated adapter reports no
            // clock at all, so that stays blank rather than reading as zero.
            snapshot.GpuPercent = wmiActiveUtil;
            snapshot.GpuDisplay = FormatGpuDisplay(double.NaN, wmiActiveUtil, _gpuTempC);
        }
        else
        {
            // No sample from any source — keep the layout stable, but do not claim a reading
            snapshot.GpuPercent = 0;
            snapshot.GpuDisplay = FormatGpuDisplay(double.NaN, 0, double.NaN);
        }

        // 5. Disks (Detect all fixed drives and compute status color: <70% green, 70-85% yellow, >85% red)
        // Free-space queries hit every volume; sample on a slow cadence and reuse the cached snapshot between samples
        if (_cachedDisks.Count > 0 && _tickCount % DiskSampleInterval != 1)
        {
            snapshot.Disks = _cachedDisks;
            return snapshot;
        }

        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderBy(d => d.Name)
                .ToList();

            foreach (var drive in drives)
            {
                try
                {
                    string letter = drive.Name.TrimEnd('\\');
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel.Trim();
                    string displayName = $"{label} ({letter})";
                    double totalGb = Math.Round(drive.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
                    double freeGb = Math.Round(drive.TotalFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                    double usedGb = Math.Round(totalGb - freeGb, 1);
                    double usedPercent = totalGb > 0 ? Math.Clamp((usedGb / totalGb) * 100.0, 0, 100) : 0;

                    string colorHex;
                    if (usedPercent < 70.0)
                        colorHex = "#10B981"; // Below 70%: Green
                    else if (usedPercent <= 85.0)
                        colorHex = "#EAB308"; // 70% - 85%: Yellow
                    else
                        colorHex = "#EF4444"; // Above 85%: Red

                    snapshot.Disks.Add(new DiskDriveSnapshot
                    {
                        DriveLetter = letter,
                        VolumeLabel = label,
                        DisplayName = displayName,
                        TotalGb = totalGb,
                        FreeGb = freeGb,
                        UsedGb = usedGb,
                        UsedPercent = Math.Round(usedPercent, 1),
                        FreeSpaceDisplay = $"{freeGb:0.0} GB free of {totalGb:0.0} GB",
                        UsageDisplay = $"{usedPercent:0}%",
                        StatusColorHex = colorHex
                    });
                }
                catch { /* Best-effort sample: this second's value is skipped. */ }
            }
        }
        catch { /* Best-effort sample: this second's value is skipped. */ }

        _cachedDisks = snapshot.Disks;
        return snapshot;
    }

    // Latency to a fast anycast resolver, refreshed every 5th tick — a per-second ping would be needless traffic.
    private void SampleNetworkPing()
    {
        if (_tickCount % 5 != 1) return;

        try
        {
            using var ping = new Ping();
            var reply = ping.Send("1.1.1.1", 1000);
            _cachedPingMs = reply.Status == IPStatus.Success ? reply.RoundtripTime : double.NaN;
        }
        catch
        {
            _cachedPingMs = double.NaN;
        }
    }

    /// <summary>
    /// Fills the cached CPU temperature and clock. Refreshed every 5th tick — neither moves
    /// meaningfully on a 1s scale, and both sources are comparatively expensive.
    ///
    /// Temperature is taken from the processor's own die sensors first. The ACPI thermal zone
    /// used as the fallback is a chassis sensor: on this class of machine it reports roughly
    /// 28 °C regardless of CPU load, so it is flagged as not-from-die and the UI must not
    /// present it as a processor temperature.
    /// </summary>
    private void SampleCpuSensors()
    {
        bool haveReading = !double.IsNaN(_cachedCpuTempC) || !double.IsNaN(_cachedCpuGhz);
        if (haveReading && _tickCount % 5 != 1) return;

        // Sample() is called unconditionally: it decides internally which backend to use, and the
        // Afterburner path stays viable even after the driver-backed one has disabled itself
        // (the user can start Afterburner at any point during the session).
        if (_hardwareMonitor != null)
        {
            var hw = _hardwareMonitor.Sample();

            double die = !double.IsNaN(hw.CpuPackageTempC) ? hw.CpuPackageTempC : hw.CpuMaxCoreTempC;
            if (!double.IsNaN(die))
            {
                _cachedCpuTempC = die;
                _cpuTempIsFromDie = true;
            }

            if (!double.IsNaN(hw.CpuClockGhz))
                _cachedCpuGhz = hw.CpuClockGhz;

            // The same poll already carries the discrete GPU's sensors — no reason to also
            // spawn nvidia-smi when this succeeded.
            if (!double.IsNaN(hw.GpuTempC)) _gpuTempC = hw.GpuTempC;
            if (!double.IsNaN(hw.GpuClockGhz)) _gpuClockGhz = hw.GpuClockGhz;
            if (!double.IsNaN(hw.GpuLoadPercent))
            {
                _gpuSmiUtil = hw.GpuLoadPercent;
                _gpuSensorsFromMonitor = true;
                _freshGpuSample = true;
            }
            if (!string.IsNullOrWhiteSpace(hw.GpuName)) _dedicatedGpuName ??= hw.GpuName;

            if (_cpuTempIsFromDie) { SampleCpuClockFallback(); return; }
        }

        SampleAcpiThermalZone();
        SampleCpuClockFallback();
    }

    // Base clock scaled by processor performance (Task Manager style). Only used when the
    // per-core sensors are unavailable — on hybrid P/E-core parts this ratio is an approximation.
    private void SampleCpuClockFallback()
    {
        if (!double.IsNaN(_cachedCpuGhz)) return;

        try
        {
            using var perfSearcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
            foreach (var obj in perfSearcher.Get())
            {
                if (ulong.TryParse(obj["PercentProcessorPerformance"]?.ToString(), out ulong perf) && perf > 0)
                    _cachedCpuGhz = _baseCpuMhz * perf / 100.0 / 1000.0;
                break;
            }
        }
        catch (Exception caught) when (ExpectedFailures.Wmi(caught)) { /* No performance counter: the base clock is shown. */ }
    }

    // Thermal-zone performance counters report Kelvin and, unlike the ACPI WMI class
    // (MSAcpi_ThermalZoneTemperature), are readable without administrator rights.
    private void SampleAcpiThermalZone()
    {
        if (_acpiTempUnavailable) return;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

            double max = double.NaN;
            foreach (var obj in searcher.Get())
            {
                if (double.TryParse(obj["Temperature"]?.ToString(), out double kelvin))
                {
                    double c = kelvin - 273.15;
                    if (c > 0 && c < 120 && (double.IsNaN(max) || c > max)) max = c;
                }
            }

            if (double.IsNaN(max))
            {
                _acpiTempUnavailable = true;
            }
            else
            {
                _cachedCpuTempC = max;
                _cpuTempIsFromDie = false;
            }
        }
        catch
        {
            _acpiTempUnavailable = true;
        }
    }

    /// <summary>
    /// Renders a temperature in the unit the user picked, or an em dash when there is no
    /// reading. A missing sensor must never render as "0°C" — that reads as a real measurement.
    /// </summary>
    private string FormatTemp(double celsius)
    {
        if (double.IsNaN(celsius)) return "—";

        var unit = _settingsService?.GetSettings().TemperatureUnit ?? TemperatureUnit.Celsius;
        return unit == TemperatureUnit.Fahrenheit
            ? $"{TemperatureHelper.ToFahrenheit(celsius):0}°F"
            : $"{celsius:0}°C";
    }

    // GPU clock and temperature are only exposed by the vendor tool; nvidia-smi covers NVIDIA machines.
    // Spawning a process is not free, so refresh every 5th tick and self-disable when the tool is absent.
    /// <returns>True when a new reading was taken on this tick.</returns>
    private bool SampleNvidiaSmi()
    {
        if (_nvidiaSmiUnavailable) return false;
        if (!double.IsNaN(_gpuClockGhz) && _tickCount % 5 != 1) return false;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,clocks.sm,utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) { _nvidiaSmiUnavailable = true; return false; }

            string line = p.StandardOutput.ReadLine() ?? string.Empty;
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch (Exception caught) when (ExpectedFailures.Process(caught)) { /* It exited as the timeout fired. */ }
            }

            var parts = line.Split(',');
            if (parts.Length >= 4
                && double.TryParse(parts[1].Trim(), out double mhz)
                && double.TryParse(parts[2].Trim(), out double util)
                && double.TryParse(parts[3].Trim(), out double temp))
            {
                _dedicatedGpuName ??= parts[0].Trim();
                _gpuClockGhz = mhz / 1000.0;
                _gpuSmiUtil = util;
                _gpuTempC = temp;
                return true;
            }

            if (double.IsNaN(_gpuClockGhz))
            {
                // Never produced a valid sample — stop trying
                _nvidiaSmiUnavailable = true;
            }
        }
        catch
        {
            _nvidiaSmiUnavailable = true;
        }

        return false;
    }

    /// <summary>
    /// Decides whether the discrete GPU counts as "active" from one nvidia-smi utilization
    /// sample, using hysteresis so a fluctuating load cannot flip the readout back and forth:
    /// it engages at <see cref="DedicatedOnThresholdPercent"/> and only disengages after
    /// <see cref="DedicatedOffSamplesRequired"/> consecutive samples below
    /// <see cref="DedicatedOffThresholdPercent"/>. Between those thresholds the state holds.
    /// </summary>
    public static (bool Active, int IdleSamples) EvaluateDedicatedGpuState(
        bool currentlyActive, int idleSamples, double smiUtilPercent)
    {
        if (smiUtilPercent >= DedicatedOnThresholdPercent)
            return (true, 0);

        if (smiUtilPercent < DedicatedOffThresholdPercent)
        {
            int idle = idleSamples + 1;
            return (idle >= DedicatedOffSamplesRequired ? false : currentlyActive, idle);
        }

        // Dead band between the two thresholds — keep whatever state we are in
        return (currentlyActive, 0);
    }

    /// <summary>
    /// Always renders clock / load / temperature so the card keeps one layout. A value with no
    /// source available renders as an em dash: integrated adapters expose neither a clock nor a
    /// temperature, and showing those as "0.00 GHz  0°C" claimed a measurement that was never taken.
    /// </summary>
    private string FormatGpuDisplay(double clockGhz, double percent, double tempC)
    {
        string clock = double.IsNaN(clockGhz) ? "—" : $"{clockGhz:0.00} GHz";
        return $"{clock}  {percent:0}%  {FormatTemp(tempC)}";
    }

    private static string FormatBandwidth(double bytesPerSec)
    {
        double bitsPerSec = bytesPerSec * 8;
        if (bitsPerSec >= 1000_000_000)
            return $"{bitsPerSec / 1000_000_000.0:0.0} Gbps";
        if (bitsPerSec >= 1000_000)
            return $"{bitsPerSec / 1000_000.0:0.0} Mbps";
        if (bitsPerSec >= 1000)
            return $"{bitsPerSec / 1000.0:0.0} Kbps";
        return $"{bitsPerSec:0} bps";
    }

    private static long FileTimeToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        return ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _timer.Dispose();
    }
}
