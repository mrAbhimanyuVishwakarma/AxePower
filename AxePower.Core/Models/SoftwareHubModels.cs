using System;

namespace AxePower.Core.Models;

public enum PackageCategory
{
    Runtimes,
    Gaming,
    Monitoring,
    Utilities
}

public class SoftwarePackageItem
{
    public string Id { get; set; } = string.Empty;
    public string WinGetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PackageCategory Category { get; set; }
    public string Glyph { get; set; } = "\uE71D";
    public bool IsInstalled { get; set; }
    public bool IsBusy { get; set; }
    public bool IsWebLinkOnly { get; set; }
    public string WebUrl { get; set; } = string.Empty;
    public string LaunchProtocolOrExe { get; set; } = string.Empty;

    public string CategoryDisplayName => Category switch
    {
        PackageCategory.Runtimes => "Runtimes & DirectX",
        PackageCategory.Gaming => "Launchers & Gaming",
        PackageCategory.Monitoring => "Hardware & Tuning",
        PackageCategory.Utilities => "Tools & Productivity",
        _ => "General"
    };
}

public class WindowsUpdateShieldStatus
{
    public bool IsSecurityOnlyEnabled { get; set; }
    public bool IsNoForcedRebootEnabled { get; set; }
}
