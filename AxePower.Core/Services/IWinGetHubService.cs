using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public interface IWinGetHubService
{
    IReadOnlyList<SoftwarePackageItem> GetAvailablePackages();
    Task RefreshInstallationStatusesAsync();
    Task<bool> InstallPackageAsync(string wingetId, IProgress<string> outputProgress);
    Task<bool> UninstallPackageAsync(string wingetId, IProgress<string> outputProgress);
    Task<bool> UpgradeAllPackagesAsync(IProgress<string> outputProgress);
}
