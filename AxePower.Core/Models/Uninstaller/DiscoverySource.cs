namespace AxePower.Core.Models.Uninstaller;

/// <summary>Where an installed application was discovered.</summary>
[Flags]
public enum DiscoverySource
{
    None             = 0,
    RegistryHKLM     = 1 << 0,
    RegistryHKCU     = 1 << 1,
    RegistryWOW6432  = 1 << 2,
    AppX             = 1 << 3,
    WinGet           = 1 << 4,
    Other            = 1 << 5,
}
