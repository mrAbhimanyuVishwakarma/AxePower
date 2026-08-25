using System;

namespace AxePower.Core.Models;

public enum ManagedServiceStatus
{
    Running,
    Stopped,
    Throttled,
    NotFound
}

public class ManagedServiceItem
{
    public string ServiceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool WasRunningBeforeThrottle { get; set; }
    public ManagedServiceStatus Status { get; set; }
}

public class MemoryStatusInfo
{
    public double TotalPhysicalGb { get; set; }
    public double AvailablePhysicalGb { get; set; }
    public double UsedPhysicalGb { get; set; }
    public double MemoryLoadPercent { get; set; }
    public string FormattedSummary => $"{UsedPhysicalGb:F1} GB / {TotalPhysicalGb:F1} GB ({MemoryLoadPercent:F0}%)";
}

public class DynamicAutomationConfig
{
    public bool AutoPauseServicesInBeast { get; set; } = true;
    public bool AutoFlushRamOnGameLaunch { get; set; } = true;
    public bool AutoRestoreOnEco { get; set; } = true;
}
