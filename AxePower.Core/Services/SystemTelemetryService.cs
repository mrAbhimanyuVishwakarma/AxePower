using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
    public string CpuDisplay { get; set; } = "0%  2.40 GHz";

    public double GpuPercent { get; set; }
    public string GpuName { get; set; } = "GPU";
    public string GpuDisplay { get; set; } = "0%";

    public double MemoryPercent { get; set; }
    public string MemoryDisplay { get; set; } = "0.0 GB (0%)";

    public double NetworkPercent { get; set; }
    public string NetworkName { get; set; } = "WiFi";
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
    private string _cachedCpuClock = "2.40 GHz";
    private readonly List<(string GpuName, int Index)> _discoveredGpus = new();

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
                    _cachedCpuClock = $"{mhzInt / 1000.0:0.00} GHz";
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

        snapshot.CpuPercent = cpuPercent;
        snapshot.CpuDisplay = $"{cpuPercent:0}%  {_cachedCpuClock}";

        // 2. Memory (RAM)
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
            double usedGb = totalGb - availGb;
            double memPercent = Math.Clamp((usedGb / totalGb) * 100.0, 0, 100);

            snapshot.MemoryPercent = memPercent;
            snapshot.MemoryDisplay = $"{usedGb:0.0} GB ({memPercent:0}%)";
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
                snapshot.NetworkName = "WiFi";
                snapshot.NetworkDisplay = $"↑{sendStr} ↓{recvStr}";
            }
            else
            {
                snapshot.NetworkName = "WiFi";
                snapshot.NetworkDisplay = "↑0 bps ↓0 bps";
                snapshot.NetworkPercent = 0;
            }
        }
        catch { }

        // 4. GPU (Dedicated or Primary GPU)
        var gpuUtilMap = new Dictionary<int, double>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D%'");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                if (ulong.TryParse(obj["UtilizationPercentage"]?.ToString(), out ulong util))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(name, @"phys_(\d+)");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int physIdx))
                        gpuUtilMap[physIdx] = gpuUtilMap.GetValueOrDefault(physIdx) + util;
                }
            }
        }
        catch { }

        snapshot.GpuName = "GPU";
        if (gpuUtilMap.Count > 0)
        {
            // Counter phys_N order is not guaranteed to match Win32_VideoController order,
            // so report the busiest physical adapter instead of assuming index 1 is discrete
            double gpuUsage = Math.Clamp(gpuUtilMap.Values.Max(), 0, 100);
            snapshot.GpuPercent = gpuUsage;
            snapshot.GpuDisplay = $"{gpuUsage:0}%";
        }
        else
        {
            // No real counter sample — report honestly rather than inventing a number
            snapshot.GpuPercent = 0;
            snapshot.GpuDisplay = "N/A";
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
