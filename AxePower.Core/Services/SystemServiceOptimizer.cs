using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;

namespace AxePower.Core.Services;

public class SystemServiceOptimizer
{
    // Known safe-to-optimize services
    public static readonly Dictionary<string, string> OptimizableServices = new()
    {
        { "Spooler", "Print Spooler" },
        { "SysMain", "SysMain (Superfetch)" },
        { "WSearch", "Windows Search" },
        { "edgeupdate", "Microsoft Edge Update Service" },
        { "WaaSMedicSvc", "Windows Update Medic Service" },
        { "XblAuthManager", "Xbox Live Auth Manager" },
        { "XblGameSave", "Xbox Live Game Save" },
        { "XboxNetApiSvc", "Xbox Live Networking Service" },
        { "VMAuthdService", "VMware Authorization Service" },
        { "VMnetDHCP", "VMware DHCP Service" },
        { "VMware NAT Service", "VMware NAT Service" },
        { "postgresql-x64-15", "PostgreSQL Server" },
        { "MongoDB", "MongoDB Server" }
    };

    public List<SystemServiceObject> GetOptimizableServices()
    {
        var list = new List<SystemServiceObject>();
        var allServices = ServiceController.GetServices();

        foreach (var svc in allServices)
        {
            if (OptimizableServices.ContainsKey(svc.ServiceName))
            {
                list.Add(new SystemServiceObject
                {
                    ServiceName = svc.ServiceName,
                    DisplayName = OptimizableServices[svc.ServiceName],
                    IsRunning = svc.Status == ServiceControllerStatus.Running
                });
            }
        }
        return list;
    }

    public void OptimizeServices(IEnumerable<string> serviceNames)
    {
        foreach (var name in serviceNames)
        {
            try
            {
                var svc = new ServiceController(name);
                if (svc.Status == ServiceControllerStatus.Running || svc.Status == ServiceControllerStatus.Paused)
                {
                    svc.Stop();
                    svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }

                // Set to manual
                var startInfo = new ProcessStartInfo("sc.exe", $"config \"{name}\" start= demand")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Verb = "runas"
                };
                Process.Start(startInfo)?.WaitForExit();
            }
            catch
            {
                // Ignore if we lack permissions or service doesn't exist
            }
        }
    }
}

public class SystemServiceObject
{
    public string ServiceName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    public bool IsSelected { get; set; }

    public bool IsPaused { get; set; }

    public string StatusText => IsPaused ? "Paused" : (IsRunning ? "Running" : "Stopped");
}
