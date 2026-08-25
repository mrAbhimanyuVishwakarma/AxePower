using System;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface IWindowsUpdateShieldService
{
    WindowsUpdateShieldStatus GetStatus();
    Task<bool> SetSecurityOnlyUpdatesAsync(bool enable);
    Task<bool> SetNoForcedRebootAsync(bool enable);
    Task<bool> ResetWindowsUpdateCacheAsync(IProgress<string> outputProgress);
}
