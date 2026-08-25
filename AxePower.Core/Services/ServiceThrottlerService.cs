using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class ServiceThrottlerService : IServiceThrottlerService
{
    private readonly LogService _log;
    private readonly List<ManagedServiceItem> _managedServices = new();
    private bool _areServicesThrottled;

    public bool AreServicesThrottled => _areServicesThrottled;

    public ServiceThrottlerService(LogService log)
    {
        _log = log;
        InitializeServicesList();
    }

    private void InitializeServicesList()
    {
        _managedServices.Clear();

        _managedServices.Add(new ManagedServiceItem
        {
            ServiceName = "SysMain",
            DisplayName = "SuperFetch / SysMain",
            Description = "Background RAM prefetching service that causes disk/RAM overhead during gaming.",
            Status = ManagedServiceStatus.Stopped
        });

        _managedServices.Add(new ManagedServiceItem
        {
            ServiceName = "WSearch",
            DisplayName = "Windows Search Indexer",
            Description = "Background file indexing service that causes periodic disk I/O and CPU spikes.",
            Status = ManagedServiceStatus.Stopped
        });

        _managedServices.Add(new ManagedServiceItem
        {
            ServiceName = "DiagTrack",
            DisplayName = "Connected User Telemetry",
            Description = "Diagnostic tracking service that records and uploads system telemetry.",
            Status = ManagedServiceStatus.Stopped
        });

        _managedServices.Add(new ManagedServiceItem
        {
            ServiceName = "WerSvc",
            DisplayName = "Windows Error Reporting",
            Description = "Error reporting service that triggers crash memory dumps in background.",
            Status = ManagedServiceStatus.Stopped
        });

        _managedServices.Add(new ManagedServiceItem
        {
            ServiceName = "DoSvc",
            DisplayName = "Delivery Optimization",
            Description = "P2P Windows update sharing service that utilizes background bandwidth.",
            Status = ManagedServiceStatus.Stopped
        });
    }

    public IReadOnlyList<ManagedServiceItem> GetManagedServices()
    {
        RefreshStatuses();
        return _managedServices.AsReadOnly();
    }

    private void RefreshStatuses()
    {
        foreach (var item in _managedServices)
        {
            try
            {
                using var sc = new ServiceController(item.ServiceName);
                item.Status = sc.Status switch
                {
                    ServiceControllerStatus.Running => ManagedServiceStatus.Running,
                    ServiceControllerStatus.Stopped => _areServicesThrottled && item.WasRunningBeforeThrottle ? ManagedServiceStatus.Throttled : ManagedServiceStatus.Stopped,
                    _ => ManagedServiceStatus.Stopped
                };
            }
            catch
            {
                item.Status = ManagedServiceStatus.NotFound;
            }
        }
    }

    public async Task<int> ThrottleHeavyServicesAsync()
    {
        return await Task.Run(() =>
        {
            int stoppedCount = 0;
            _log.LogInfo("[ServiceThrottler] Throttling heavy background services for Beast/Game Mode...");

            foreach (var item in _managedServices)
            {
                try
                {
                    using var sc = new ServiceController(item.ServiceName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        item.WasRunningBeforeThrottle = true;
                        StopServiceDirectly(item.ServiceName);
                        item.Status = ManagedServiceStatus.Throttled;
                        stoppedCount++;
                        _log.LogInfo($"[ServiceThrottler] Paused background service: {item.DisplayName} ({item.ServiceName})");
                    }
                    else
                    {
                        item.WasRunningBeforeThrottle = false;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"[ServiceThrottler] Could not throttle service {item.ServiceName}: {ex.Message}");
                }
            }

            _areServicesThrottled = true;
            _log.LogInfo($"[ServiceThrottler] Throttling complete. Paused {stoppedCount} non-essential background service(s).");
            return stoppedCount;
        });
    }

    public async Task<int> RestoreThrottledServicesAsync()
    {
        return await Task.Run(() =>
        {
            int restoredCount = 0;
            _log.LogInfo("[ServiceThrottler] Restoring previously throttled services for Eco/Balanced Mode...");

            foreach (var item in _managedServices)
            {
                if (item.WasRunningBeforeThrottle)
                {
                    try
                    {
                        StartServiceDirectly(item.ServiceName);
                        item.Status = ManagedServiceStatus.Running;
                        item.WasRunningBeforeThrottle = false;
                        restoredCount++;
                        _log.LogInfo($"[ServiceThrottler] Restored background service: {item.DisplayName} ({item.ServiceName})");
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"[ServiceThrottler] Could not restore service {item.ServiceName}: {ex.Message}");
                    }
                }
            }

            _areServicesThrottled = false;
            _log.LogInfo($"[ServiceThrottler] Restoration complete. Restored {restoredCount} background service(s).");
            return restoredCount;
        });
    }

    private static void StopServiceDirectly(string serviceName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = $"stop \"{serviceName}\" /y",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(3000);
        }
        catch { }
    }

    private static void StartServiceDirectly(string serviceName)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "net.exe",
                Arguments = $"start \"{serviceName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(3000);
        }
        catch { }
    }
}
