using System;

namespace AxePower.Core.Models;

public enum TweakCategory
{
    Privacy,
    Gaming,
    Windows11
}

public enum TweakImpact
{
    Low,
    Medium,
    High
}

public class SystemTweakItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TweakCategory Category { get; set; }
    public TweakImpact Impact { get; set; } = TweakImpact.Medium;
    public bool IsApplied { get; set; }
    public bool IsRecommended { get; set; }
    public string RegistryKey { get; set; } = string.Empty;
    public string ValueName { get; set; } = string.Empty;
    public string CategoryDisplayName => Category switch
    {
        TweakCategory.Privacy => "Privacy & Telemetry",
        TweakCategory.Gaming => "Gaming & Latency",
        TweakCategory.Windows11 => "Windows 11 QoL",
        _ => "General"
    };
}
