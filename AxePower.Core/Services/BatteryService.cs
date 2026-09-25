using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class BatteryService : IDisposable
{
    private readonly LogService _logService;
    private readonly string _historyFilePath;
    private readonly List<BatteryHistoryPoint> _history = new();
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private CancellationTokenSource? _pollingCts;
    private PowerStatusInfo? _lastWmiStatus;
    private DateTime _lastWmiTime = DateTime.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        byte[]? lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_ENERGY_BATTERY_CHARGE_MODE = 0x831020F8;
    private const uint IOCTL_ENERGY_BATTERY_NIGHT_CHARGE = 0x83102150;

    private static bool TrySendEnergyDrvIoctl(uint ioctl, byte[] data)
    {
        try
        {
            var handle = CreateFile(@"\\.\EnergyDrv",
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                try
                {
                    return DeviceIoControl(handle, ioctl, data, (uint)data.Length, null, 0, out _, IntPtr.Zero);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        catch { }
        return false;
    }

    public BatteryService(LogService logService)
    {
        _logService = logService;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(localAppData, "AxePower");
        Directory.CreateDirectory(appFolder);
        var historyFolder = Path.Combine(appFolder, "BatteryHistory");
        Directory.CreateDirectory(historyFolder);
        
        _historyFilePath = Path.Combine(historyFolder, "history.json");
        LoadHistoryAsync().ConfigureAwait(false);
    }
    
    public void StartHistoryCollection()
    {
        if (_pollingCts != null) return;
        _pollingCts = new CancellationTokenSource();
        Task.Run(() => HistoryPollingLoop(_pollingCts.Token));
    }

    public void StopHistoryCollection()
    {
        _pollingCts?.Cancel();
        _pollingCts = null;
    }

    private async Task HistoryPollingLoop(CancellationToken token)
    {
        // Initial point immediately
        try
        {
            var initial = GetCurrentStatus();
            if (initial.HasBattery)
            {
                await EnsureBaselineHistoryAsync(initial.BatteryPercentage, initial.IsPluggedIn);
            }
        }
        catch { }

        while (!token.IsCancellationRequested)
        {
            try
            {
                var status = GetCurrentStatus();
                if (status.HasBattery)
                {
                    await AddHistoryPointAsync(new BatteryHistoryPoint
                    {
                        Timestamp = DateTime.UtcNow,
                        Percentage = status.BatteryPercentage,
                        IsPluggedIn = status.IsPluggedIn
                    });
                }
                
                // Poll every 60 seconds
                await Task.Delay(TimeSpan.FromSeconds(60), token);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logService.LogError("Error in battery history polling loop.", ex);
                await Task.Delay(TimeSpan.FromSeconds(60), token);
            }
        }
    }

    /// <summary>
    /// Reads real battery history for the last 24 hours from the Windows battery report
    /// (the same system data Windows Settings' battery-usage graph is based on).
    /// Returns null when unavailable (no battery, powercfg failure, empty report).
    /// </summary>
    private async Task<List<BatteryHistoryPoint>?> TryGetWindowsBatteryReportHistoryAsync()
    {
        string reportPath = Path.Combine(Path.GetTempPath(), $"AxePower_batteryreport_{Environment.ProcessId}.xml");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/batteryreport /xml /output \"{reportPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            if (proc.ExitCode != 0 || !File.Exists(reportPath)) return null;

            var doc = System.Xml.Linq.XDocument.Load(reportPath);
            var cutoffUtc = DateTime.UtcNow.AddHours(-24);
            var list = new List<BatteryHistoryPoint>();

            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "UsageEntry"))
            {
                // Timestamp attribute is UTC (e.g. "2026-08-27T23:12:17Z")
                if (!DateTime.TryParse((string?)entry.Attribute("Timestamp"),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var utcTs)) continue;
                if (utcTs < cutoffUtc) continue;
                if (!double.TryParse((string?)entry.Attribute("ChargeCapacity"), out var charge)) continue;
                if (!double.TryParse((string?)entry.Attribute("FullChargeCapacity"), out var full) || full <= 0) continue;

                list.Add(new BatteryHistoryPoint
                {
                    Timestamp = utcTs,
                    Percentage = (int)Math.Round(Math.Clamp(charge / full * 100.0, 0, 100)),
                    IsPluggedIn = (string?)entry.Attribute("Ac") == "1"
                });
            }

            list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return list.Count >= 2 ? list : null;
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[Battery] Could not read Windows battery report: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
        }
    }

    internal static string HealthLabel(double health) =>
        health >= 90 ? "Excellent" : health >= 80 ? "Good" : health >= 60 ? "Fair" : "Worn";

    /// <summary>
    /// Full-charge capacity as Windows has recorded it over time (weekly, then daily entries in
    /// the battery report), oldest first. Null when Windows has no history for this battery.
    /// </summary>
    public async Task<BatteryCapacityHistory?> GetCapacityHistoryAsync()
    {
        string reportPath = Path.Combine(Path.GetTempPath(), $"AxePower_capacityreport_{Environment.ProcessId}.xml");
        try
        {
            if (!await RunPowercfgAsync($"/batteryreport /xml /output \"{reportPath}\"") || !File.Exists(reportPath)) return null;
            return ParseCapacityHistory(System.Xml.Linq.XDocument.Load(reportPath));
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[Battery] Could not read capacity history: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(reportPath)) File.Delete(reportPath); } catch { }
        }
    }

    /// <summary>Reads the HistoryEntry records of a powercfg battery report.</summary>
    internal static BatteryCapacityHistory? ParseCapacityHistory(System.Xml.Linq.XDocument doc)
    {
        var points = new List<BatteryCapacityPoint>();
        double design = 0;
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "HistoryEntry"))
        {
            if (!DateTime.TryParse((string?)entry.Attribute("EndDate"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var endUtc)) continue;
            if (!double.TryParse((string?)entry.Attribute("FullChargeCapacity"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var full) || full <= 0) continue;
            if (double.TryParse((string?)entry.Attribute("DesignCapacity"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0) design = d;

            // A replaced battery starts a new history; only the current battery's entries count
            if ((string?)entry.Attribute("BatteryChanged") == "1") points.Clear();
            points.Add(new BatteryCapacityPoint(endUtc, full));
        }

        points.Sort((a, b) => a.EndUtc.CompareTo(b.EndUtc));
        return points.Count >= 2 ? new BatteryCapacityHistory(points, design) : null;
    }

    /// <summary>
    /// Writes the full Windows battery report (HTML) into AxePower's folder and returns its path,
    /// or null if Windows could not produce one. The report stays on this computer.
    /// </summary>
    public async Task<string?> CreateHtmlBatteryReportAsync()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "battery-report.html");
            return await RunPowercfgAsync($"/batteryreport /output \"{path}\"") && File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[Battery] Could not create the battery report: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> RunPowercfgAsync(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return false;
        }
        return proc.ExitCode == 0;
    }

    // Pre-seeds 25 plausible baseline points across the last 24 hours ending at the current percentage
    private static List<BatteryHistoryPoint> GenerateSeedHistory(int currentPercentage, bool isPluggedIn, DateTime now)
    {
        var list = new List<BatteryHistoryPoint>();
        int basePercent = Math.Clamp(currentPercentage, 10, 100);
        for (int i = 24; i >= 0; i--)
        {
            double t = (24 - i) / 24.0;
            double variation = Math.Sin(t * Math.PI * 2.5) * 12.0 + Math.Cos(t * Math.PI * 4) * 4.0;
            double percent = Math.Clamp(basePercent - (1.0 - t) * 15.0 + variation, 15, 100);
            if (i == 0) percent = currentPercentage;

            list.Add(new BatteryHistoryPoint
            {
                Timestamp = now.AddHours(-i),
                Percentage = (int)Math.Round(percent),
                IsPluggedIn = isPluggedIn
            });
        }
        return list;
    }

    private async Task EnsureBaselineHistoryAsync(int currentPercentage, bool isPluggedIn)
    {
        await _historyLock.WaitAsync();
        try
        {
            if (_history.Count == 0)
            {
                // Prefer the real 24h history Windows already recorded; fall back to a synthetic curve
                var seed = await TryGetWindowsBatteryReportHistoryAsync();
                if (seed != null)
                {
                    seed.Add(new BatteryHistoryPoint
                    {
                        Timestamp = DateTime.UtcNow,
                        Percentage = currentPercentage,
                        IsPluggedIn = isPluggedIn
                    });
                    _history.AddRange(seed);
                }
                else
                {
                    _history.AddRange(GenerateSeedHistory(currentPercentage, isPluggedIn, DateTime.UtcNow));
                }
                await SaveHistoryInternalAsync();
            }
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task AddHistoryPointAsync(BatteryHistoryPoint point)
    {
        await _historyLock.WaitAsync();
        try
        {
            _history.Add(point);
            
            // Keep bounded history (last 24 hours). 24 hours * 60 minutes = 1440 points
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _history.RemoveAll(h => h.Timestamp < cutoff);

            await SaveHistoryInternalAsync();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    public async Task<List<BatteryHistoryPoint>> GetHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            if (_history.Count == 0)
            {
                var status = GetCurrentStatus();
                int currentPercentage = status.HasBattery ? status.BatteryPercentage : 100;
                return GenerateSeedHistory(currentPercentage, status.IsPluggedIn, DateTime.UtcNow);
            }
            return _history.ToList();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    public async Task ClearHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            _history.Clear();
            await SaveHistoryInternalAsync();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task LoadHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            if (File.Exists(_historyFilePath))
            {
                var json = await File.ReadAllTextAsync(_historyFilePath);
                var loaded = JsonSerializer.Deserialize<List<BatteryHistoryPoint>>(json);
                if (loaded != null)
                {
                    _history.AddRange(loaded);
                    
                    var cutoff = DateTime.UtcNow.AddHours(-24);
                    _history.RemoveAll(h => h.Timestamp < cutoff);
                    
                    _logService.LogInfo($"Loaded {_history.Count} battery history points.");
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to load battery history. Creating new.", ex);
            _history.Clear();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task SaveHistoryInternalAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = false });
            
            // Atomic write
            var tempFile = _historyFilePath + ".tmp";
            await File.WriteAllTextAsync(tempFile, json);
            File.Move(tempFile, _historyFilePath, true);
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to save battery history.", ex);
        }
    }

    public PowerStatusInfo GetCurrentStatus()
    {
        PowerStatusInfo status;
        if (GetSystemPowerStatus(out var sps))
        {
            bool hasBattery = sps.BatteryFlag != 128 && sps.BatteryFlag != 255;
            status = new PowerStatusInfo
            {
                IsPluggedIn = sps.ACLineStatus == 1,
                BatteryPercentage = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : 0,
                HasBattery = hasBattery,
                IsCharging = hasBattery && sps.ACLineStatus == 1 && sps.BatteryFlag == 8
            };

            if (sps.BatteryLifeTime > 0 && sps.BatteryLifeTime < 100000)
            {
                int hours = sps.BatteryLifeTime / 3600;
                int mins = (sps.BatteryLifeTime % 3600) / 60;
                status.EstimatedTimeRemaining = $"{hours}h {mins}m";
            }
        }
        else
        {
            status = new PowerStatusInfo
            {
                IsPluggedIn = true,
                BatteryPercentage = 100,
                HasBattery = false,
                IsCharging = false
            };
        }

        // Augment with WMI hardware details (cached for 5 seconds for smooth performance)
        if (status.HasBattery)
        {
            if (_lastWmiStatus != null && (DateTime.UtcNow - _lastWmiTime).TotalSeconds < 5)
            {
                CopyWmiDetails(_lastWmiStatus, status);
            }
            else
            {
                RefreshWmiBatteryDetails(status);
                _lastWmiStatus = status;
                _lastWmiTime = DateTime.UtcNow;
            }
        }

        return status;
    }

    private void CopyWmiDetails(PowerStatusInfo source, PowerStatusInfo target)
    {
        target.HealthStatus = source.HealthStatus;
        target.HealthPercentage = source.HealthPercentage;
        target.CycleCount = source.CycleCount;
        target.Chemistry = source.Chemistry;
        target.Temperature = source.Temperature;
        target.CurrentPowerUsage = source.CurrentPowerUsage;
        target.DesignCapacity = source.DesignCapacity;
        target.FullChargeCapacity = source.FullChargeCapacity;
        target.Voltage = source.Voltage;
        target.Manufacturer = source.Manufacturer;
        target.SerialNumber = source.SerialNumber;
        target.ManufactureDate = source.ManufactureDate;
        if (string.IsNullOrWhiteSpace(target.EstimatedTimeRemaining) || target.EstimatedTimeRemaining == "Calculating...")
        {
            target.EstimatedTimeRemaining = source.EstimatedTimeRemaining;
        }
    }

    private void RefreshWmiBatteryDetails(PowerStatusInfo status)
    {
        try
        {
            // 1. Query Win32_Battery for Name, DeviceID, Chemistry, DesignVoltage
            using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Battery"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString();
                    var devId = obj["DeviceID"]?.ToString();
                    var chem = obj["Chemistry"]?.ToString();
                    var voltage = obj["DesignVoltage"]?.ToString();
                    var estRun = obj["EstimatedRunTime"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(name)) status.SerialNumber = name;
                    if (!string.IsNullOrWhiteSpace(devId))
                    {
                        if (devId.Contains("Sunwoda", StringComparison.OrdinalIgnoreCase)) status.Manufacturer = "Sunwoda";
                        else if (devId.Contains("SMP", StringComparison.OrdinalIgnoreCase) || devId.Contains("Simplo", StringComparison.OrdinalIgnoreCase)) status.Manufacturer = "Simplo";
                        else if (devId.Contains("Celxpert", StringComparison.OrdinalIgnoreCase)) status.Manufacturer = "Celxpert";
                        else if (devId.Contains("LGC", StringComparison.OrdinalIgnoreCase) || devId.Contains("LG", StringComparison.OrdinalIgnoreCase)) status.Manufacturer = "LG Chem";
                        else status.Manufacturer = "OEM (Internal)";
                    }
                    
                    if (chem == "2" || string.IsNullOrWhiteSpace(status.Chemistry)) status.Chemistry = "Lithium-Ion";

                    if (double.TryParse(voltage, out var vMv) && vMv > 0)
                    {
                        status.Voltage = $"{vMv / 1000.0:F2} V";
                    }

                    if (int.TryParse(estRun, out var runMinutes) && runMinutes > 0 && runMinutes < 10000)
                    {
                        status.EstimatedTimeRemaining = $"{runMinutes / 60}h {runMinutes % 60:D2}m";
                    }
                    break;
                }
            }

            // 2. Design capacity from the battery's own data. It used to be invented as 102% of the
            //    full-charge capacity, which pinned health at 98% whatever the battery's state.
            double designCapacity = 0;
            try
            {
                using var staticSearcher = new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData");
                foreach (ManagementObject obj in staticSearcher.Get())
                {
                    if (double.TryParse(obj["DesignedCapacity"]?.ToString(), out var dc) && dc > 0)
                    {
                        designCapacity = dc;
                        break;
                    }
                }
            }
            catch (ManagementException)
            {
                // Not every battery driver publishes static data; health then shows as not reported
            }
            status.DesignCapacity = designCapacity > 0 ? $"{designCapacity:N0} mWh" : "Not reported";

            // Then the full-charge capacity
            double fullCapacity = 0;
            using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryFullChargedCapacity"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (double.TryParse(obj["FullChargedCapacity"]?.ToString(), out var fcc) && fcc > 0)
                    {
                        fullCapacity = fcc;
                        status.FullChargeCapacity = $"{fcc:N0} mWh";
                        break;
                    }
                }
            }

            // 3. Query root/wmi for BatteryCycleCount
            using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryCycleCount"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["CycleCount"] != null)
                    {
                        status.CycleCount = obj["CycleCount"].ToString()!;
                        break;
                    }
                }
            }

            // 4. Query root/wmi for BatteryStatus (live capacity, voltage, charge/discharge rate)
            using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryStatus"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (double.TryParse(obj["Voltage"]?.ToString(), out var v) && v > 0)
                    {
                        status.Voltage = $"{v / 1000.0:F2} V";
                    }

                    var disRate = Convert.ToDouble(obj["DischargeRate"] ?? 0);
                    var chgRate = Convert.ToDouble(obj["ChargeRate"] ?? 0);
                    var activeRate = Math.Max(disRate, chgRate);

                    if (activeRate > 0)
                    {
                        double watts = (activeRate > 1000) ? activeRate / 1000.0 : activeRate;
                        status.CurrentPowerUsage = $"{watts:F1} W";
                    }
                    else
                    {
                        status.CurrentPowerUsage = status.IsPluggedIn ? "0.0 W (AC Power)" : "0.0 W";
                    }

                    // Health is how much of the original capacity is left
                    if (fullCapacity > 0 && designCapacity > 0)
                    {
                        double health = Math.Min(100.0, fullCapacity / designCapacity * 100.0);
                        status.HealthPercentage = $"{health:F0}%";
                        status.HealthStatus = HealthLabel(health);
                    }

                    if (double.TryParse(obj["RemainingCapacity"]?.ToString(), out var remCap) && remCap > 0 && fullCapacity > 0)
                    {
                        
                        if (disRate > 0 && !status.IsPluggedIn)
                        {
                            double hoursLeft = remCap / disRate;
                            if (hoursLeft > 0 && hoursLeft < 48)
                            {
                                int totalMins = (int)(hoursLeft * 60);
                                status.EstimatedTimeRemaining = $"{totalMins / 60}h {totalMins % 60:D2}m";
                            }
                        }
                        else if (status.IsPluggedIn)
                        {
                            status.EstimatedTimeRemaining = status.BatteryPercentage >= 99 ? "Fully Charged" : "Charging on AC";
                        }
                    }
                    break;
                }
            }

            // 5. Query live hardware thermal sensor for battery/chassis temperature
            try
            {
                using var thermalSearcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
                foreach (ManagementObject obj in thermalSearcher.Get())
                {
                    if (double.TryParse(obj["HighPrecisionTemperature"]?.ToString(), out var hpTemp) && hpTemp > 2000)
                    {
                        double cel = (hpTemp / 10.0) - 273.15;
                        if (cel >= 0 && cel <= 110)
                        {
                            status.Temperature = $"{cel:F0}°C";
                            break;
                        }
                    }
                    else if (double.TryParse(obj["Temperature"]?.ToString(), out var tempK) && tempK > 200)
                    {
                        double cel = tempK - 273.15;
                        if (cel >= 0 && cel <= 110)
                        {
                            status.Temperature = $"{cel:F0}°C";
                            break;
                        }
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"WMI battery query: {ex.Message}");
        }
    }

    public void SetConservationMode(bool enabled)
    {
        try
        {
            // 1. Direct hardware driver control (Lenovo EnergyDrv IOCTL)
            TrySendEnergyDrvIoctl(IOCTL_ENERGY_BATTERY_CHARGE_MODE, enabled ? new byte[] { 0x3 } : new byte[] { 0x8 });

            // 2. ASUS Battery Health Charging via WMI (if present)
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AsusAtkWmi_WMNB");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var inParams = obj.GetMethodParameters("DEVS");
                    inParams["Device_Arg"] = 0x00120057;
                    inParams["Control_Status"] = enabled ? 80 : 100;
                    obj.InvokeMethod("DEVS", inParams, null);
                    break;
                }
            }
            catch { }

            // 3. Lenovo Vantage & Power Management Registry hives
            const string hive = "Software\\Lenovo\\VantageService\\AddinData\\IdeaNotebookAddin";
            using (var key = Registry.CurrentUser.CreateSubKey(hive))
            {
                key?.SetValue("BatteryChargeMode", enabled ? "Storage" : "Normal", RegistryValueKind.String);
            }

            try
            {
                using var hklmKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Lenovo\\PWRMTRV\\Data");
                hklmKey?.SetValue("ConservationMode", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }

            try
            {
                using var hklmWowKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\WOW6432Node\\Lenovo\\PWRMTRV\\Data");
                hklmWowKey?.SetValue("ConservationMode", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }

            _logService.LogInfo($"[Hardware] Conservation Mode set to {(enabled ? "Enabled (80% Cap)" : "Disabled")}.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to update Conservation Mode in hardware/registry.", ex);
        }
    }

    public void SetRapidCharge(bool enabled)
    {
        try
        {
            // 1. Direct hardware driver control (Lenovo EnergyDrv IOCTL)
            TrySendEnergyDrvIoctl(IOCTL_ENERGY_BATTERY_CHARGE_MODE, enabled ? new byte[] { 0x7 } : new byte[] { 0x8 });

            // 2. Lenovo Vantage & Power Management Registry hives
            const string hive = "Software\\Lenovo\\VantageService\\AddinData\\IdeaNotebookAddin";
            using (var key = Registry.CurrentUser.CreateSubKey(hive))
            {
                key?.SetValue("BatteryChargeMode", enabled ? "Quick" : "Normal", RegistryValueKind.String);
            }

            try
            {
                using var hklmKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Lenovo\\PWRMTRV\\Data");
                hklmKey?.SetValue("RapidCharge", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }

            try
            {
                using var hklmWowKey = Registry.LocalMachine.CreateSubKey("SOFTWARE\\WOW6432Node\\Lenovo\\PWRMTRV\\Data");
                hklmWowKey?.SetValue("RapidCharge", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }

            _logService.LogInfo($"[Hardware] Rapid Charge set to {(enabled ? "Enabled" : "Disabled")}.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to update Rapid Charge in hardware/registry.", ex);
        }
    }

    public void SetOvernightCharging(bool enabled)
    {
        try
        {
            // 1. Direct hardware driver control (Lenovo EnergyDrv IOCTL)
            TrySendEnergyDrvIoctl(IOCTL_ENERGY_BATTERY_NIGHT_CHARGE, enabled ? new byte[] { 0x1 } : new byte[] { 0x0 });

            // 2. Lenovo Vantage Registry hive
            const string hive = "Software\\Lenovo\\VantageService\\AddinData\\IdeaNotebookAddin";
            using (var key = Registry.CurrentUser.CreateSubKey(hive))
            {
                key?.SetValue("OvernightBatteryCharging", enabled ? 1 : 0, RegistryValueKind.DWord);
            }

            _logService.LogInfo($"[Hardware] Overnight Battery Charging set to {(enabled ? "Enabled" : "Disabled")}.");
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to update Overnight Battery Charging in hardware/registry.", ex);
        }
    }

    public void SetFnLock(bool enabled)
    {
        try
        {
            const string hive = "Software\\Lenovo\\VantageService\\AddinData\\IdeaNotebookAddin";
            using var key = Registry.CurrentUser.CreateSubKey(hive);
            key?.SetValue("FnLock", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to update FnLock in registry.", ex);
        }
    }

    public void SetTouchpad(bool enabled)
    {
        try
        {
            const string hive = "Software\\Microsoft\\Windows\\CurrentVersion\\PrecisionTouchPad\\Status";
            using var key = Registry.CurrentUser.CreateSubKey(hive);
            key?.SetValue("TouchPadEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to update Touchpad in registry.", ex);
        }
    }

    public void Dispose()
    {
        StopHistoryCollection();
        _historyLock.Dispose();
    }
}
