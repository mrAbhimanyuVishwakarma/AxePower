using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using AxePower.Core.Services.Operations;

namespace AxePower.Core.Services;

/// <summary>
/// Game Mode's list of services that can be paused while gaming. Pausing goes through the
/// operations framework (preview, history, rollback); it no longer launches sc.exe.
/// </summary>
public class SystemServiceOptimizer
{
    private readonly Lazy<OperationRunner> _runner;

    /// <param name="runner">Where changes are executed and recorded. Null: the app's journal, created on first use.</param>
    public SystemServiceOptimizer(LogService? log = null, OperationRunner? runner = null)
    {
        _runner = new Lazy<OperationRunner>(() => runner ?? new OperationRunner(
            new OperationJournal(OperationJournal.DefaultDirectory, log), log ?? new LogService(), new IChangeHandler[] { new ServiceChangeHandler() }));
    }

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

    public const string SourceFeature = "Game Mode › Services";
    public const string Subject = "services:game-mode";

    /// <summary>
    /// What pausing <paramref name="serviceNames"/> would change: each is stopped and set to Manual
    /// start. The plan records each service's current startup type and state, so rolling it back
    /// restores them (and starts the ones that were running). Changes nothing.
    /// </summary>
    public OperationRecord PlanPause(IEnumerable<string> serviceNames)
    {
        var names = serviceNames.Where(OptimizableServices.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Runner.Plan(
            names.Count == 1 ? $"Stop {OptimizableServices[names[0]]} and set it to Manual" : $"Stop {names.Count} services and set them to Manual",
            SourceFeature, Subject,
            names.Select(n => ServiceChangeHandler.Set(n, ServiceStart.Manual, running: false)));
    }

    /// <summary>Runs an approved plan. Each service is independent: one failing does not undo the others.</summary>
    public Task<OperationRecord> ExecuteAsync(OperationRecord plan) => Runner.ExecuteAsync(plan, FailurePolicy.ContinueWithOthers);

    private OperationRunner Runner => _runner.Value;
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
