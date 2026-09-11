using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
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
}

public class SystemTelemetryService : IDisposable
{
    private readonly LogService? _logService;
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

    // CPU temperature (ACPI thermal zone; often unavailable without admin — self-disables)
    private bool _cpuTempUnavailable;
    private double _cachedCpuTempC = double.NaN;

    // Network latency (sampled on a slow cadence; NaN while offline/unreachable)
    private double _cachedPingMs = double.NaN;

    // GPU clock/temperature via nvidia-smi (sampled on a slow cadence; self-disables when absent)
    private bool _nvidiaSmiUnavailable;
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

    public SystemTelemetryService(LogService? logService = null)
    {
        _logService = logService;
        InitializeHardwareTopology();

        // The adapter set only changes on connect/disconnect — invalidate the cache then
        try
        {
            NetworkChange.NetworkAddressChanged += (s, e) => _cachedInterfaces = null;
            NetworkChange.NetworkAvailabilityChanged += (s, e) => _cachedInterfaces = null;
        }
        catch { }

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
        catch { }

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
        catch { }

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

        // Live clock: base clock scaled by processor performance (Task Manager style)
        double cpuGhz = _baseCpuMhz / 1000.0;
        try
        {
            using var perfSearcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
            foreach (var obj in perfSearcher.Get())
            {
                if (ulong.TryParse(obj["PercentProcessorPerformance"]?.ToString(), out ulong perf) && perf > 0)
                    cpuGhz = _baseCpuMhz * perf / 100.0 / 1000.0;
                break;
            }
        }
        catch { }

        SampleCpuTemperature();

        snapshot.CpuPercent = cpuPercent;
        snapshot.CpuDisplay = double.IsNaN(_cachedCpuTempC)
            ? $"{cpuGhz:0.00} GHz  {cpuPercent:0}%"
            : $"{cpuGhz:0.00} GHz  {cpuPercent:0}%  {_cachedCpuTempC:0}°C";

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

                foreach (var ni in activeInterfaces)
                {
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
                double totalBps = totalSendBps + totalRecvBps;
                // 10 Mbps scale for Task Manager percentage
                snapshot.NetworkPercent = Math.Clamp((totalBps / (10.0 * 1024.0 * 1024.0)) * 100.0, 0, 100);
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
        catch { }

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
        catch { }

        bool freshSmiSample = SampleNvidiaSmi();

        double wmiActiveUtil = gpuUtilMap.Count > 0 ? Math.Clamp(gpuUtilMap.Values.Max(), 0, 100) : double.NaN;

        // Decide which GPU is active from nvidia-smi alone — mixing it with the WMI counters
        // compared a 5s-old number against a 1s-old one and oscillated. The state is re-evaluated
        // only when a fresh smi sample arrives and is held steady in between.
        if (_nvidiaSmiUnavailable || double.IsNaN(_gpuSmiUtil))
        {
            _dedicatedActiveState = false;
        }
        else if (_integratedGpuName == null)
        {
            _dedicatedActiveState = true;                 // single-GPU (NVIDIA) system
        }
        else if (freshSmiSample)
        {
            (_dedicatedActiveState, _dedicatedIdleSamples) =
                EvaluateDedicatedGpuState(_dedicatedActiveState, _dedicatedIdleSamples, _gpuSmiUtil);
        }

        bool dedicatedActive = _dedicatedActiveState;
        snapshot.IsDedicatedGpuActive = dedicatedActive;

        snapshot.GpuName = "GPU";

        if (dedicatedActive)
        {
            // Prefer the 1s WMI sample (the busiest adapter is the dGPU here); smi refreshes only every 5s
            double pct = Math.Clamp(!double.IsNaN(wmiActiveUtil) ? wmiActiveUtil : _gpuSmiUtil, 0, 100);
            snapshot.GpuPercent = pct;
            snapshot.GpuDisplay = FormatGpuDisplay(_gpuClockGhz, pct, _gpuTempC);
        }
        else if (!double.IsNaN(wmiActiveUtil))
        {
            // Integrated adapters expose no clock or temperature source, so those read zero.
            // The nvidia-smi values are not reused here — they describe the idle discrete GPU.
            snapshot.GpuPercent = wmiActiveUtil;
            snapshot.GpuDisplay = FormatGpuDisplay(double.NaN, wmiActiveUtil, double.NaN);
        }
        else
        {
            // No sample from any source — keep the layout stable with zeros
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
                catch { }
            }
        }
        catch { }

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

    // Thermal-zone performance counters report Kelvin and, unlike the ACPI WMI class
    // (MSAcpi_ThermalZoneTemperature), are readable without administrator rights.
    // Refreshed every 5th tick — temperature doesn't move on a 1s scale.
    private void SampleCpuTemperature()
    {
        if (_cpuTempUnavailable) return;
        if (!double.IsNaN(_cachedCpuTempC) && _tickCount % 5 != 1) return;

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

            if (double.IsNaN(max)) _cpuTempUnavailable = true;
            else _cachedCpuTempC = max;
        }
        catch
        {
            _cpuTempUnavailable = true;
        }
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
                try { p.Kill(true); } catch { }
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
    /// Always renders clock / load / temperature so the card keeps one layout.
    /// Values with no source available read as zero rather than being dropped.
    /// </summary>
    private static string FormatGpuDisplay(double clockGhz, double percent, double tempC)
    {
        double clock = double.IsNaN(clockGhz) ? 0 : clockGhz;
        double temp = double.IsNaN(tempC) ? 0 : tempC;
        return $"{clock:0.00} GHz  {percent:0}%  {temp:0}°C";
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
