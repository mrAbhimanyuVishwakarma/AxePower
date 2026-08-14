using System.Management;
using AxePower.Core.Services;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Creates Windows System Restore Points before batch uninstall operations.
/// Gracefully handles unavailability (Windows Home, System Protection disabled, etc.).
/// </summary>
public sealed class SystemRestoreService
{
    private readonly LogService _log;

    public SystemRestoreService(LogService log)
    {
        _log = log;
    }

    /// <summary>
    /// Attempts to create a System Restore Point.
    /// Returns true if created, false if unavailable or failed.
    /// Never throws — failure is non-fatal.
    /// </summary>
    public async Task<bool> TryCreateRestorePointAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                // SystemRestore is only available on Windows desktop editions
                var scope = new ManagementScope(@"\\localhost\root\default");
                var path  = new ManagementPath("SystemRestore");
                using var cls = new ManagementClass(scope, path, null);

                var inParams = cls.GetMethodParameters("CreateRestorePoint");
                inParams["Description"]    = description;
                inParams["RestorePointType"] = 0; // APPLICATION_INSTALL
                inParams["EventType"]      = 100; // BEGIN_SYSTEM_CHANGE

                var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
                var retVal = result?["ReturnValue"];
                bool success = retVal is uint uval && uval == 0;

                if (success)
                    _log.LogInfo($"[Uninstaller] System Restore Point created: {description}");
                else
                    _log.LogWarning($"[Uninstaller] System Restore Point creation returned: {retVal}");

                return success;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Uninstaller] System Restore Point unavailable: {ex.Message}");
                return false;
            }
        });
    }
}
