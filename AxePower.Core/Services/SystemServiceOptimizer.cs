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
                    CautionReason = CloseCaution.ForService(svc.ServiceName),
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
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // sc.exe could not be started (no rights or not found): this service is left as it was.
            }
        }
    }
}

/// <summary>One row in Game Mode's service list. Notifies, so select-all updates every checkbox.</summary>
public class SystemServiceObject : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isRunning, _isSelected, _isPaused;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string ServiceName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Why stopping this service is risky, or null when it is safe (<see cref="CloseCaution"/>).</summary>
    public string? CautionReason { get; set; }

    public bool IsCaution => CautionReason != null;

    public string GroupName => IsCaution ? CloseCaution.CautionGroup : "Safe to stop";

    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value, nameof(IsRunning)); }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value, nameof(IsSelected)); }

    public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value, nameof(IsPaused)); }

    public string StatusText => IsPaused ? "Paused" : (IsRunning ? "Running" : "Stopped");

    private void Set(ref bool field, bool value, string name)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusText)));
    }
}
