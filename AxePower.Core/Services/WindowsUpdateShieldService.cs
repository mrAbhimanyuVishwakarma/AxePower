using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class WindowsUpdateShieldService : IWindowsUpdateShieldService
{
    internal const string UpdatePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    internal const string AuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    private static readonly string[] UpdateServices = { "wuauserv", "bits", "cryptsvc", "msiserver" };

    private readonly LogService _log;
    private readonly IWindowsUpdateSystem _system;

    public WindowsUpdateShieldService(LogService log)
        : this(log, new WindowsUpdateSystem())
    {
    }

    internal WindowsUpdateShieldService(LogService log, IWindowsUpdateSystem system)
    {
        _log = log;
        _system = system;
    }

    public WindowsUpdateShieldStatus GetStatus()
    {
        var status = new WindowsUpdateShieldStatus();

        try
        {
            // Both values are required for the card's "365 days" claim to be true. A partial or
            // externally modified policy is deliberately shown as off rather than overstated.
            status.IsSecurityOnlyEnabled =
                _system.ReadDword(UpdatePolicyKey, "DeferFeatureUpdates") == 1 &&
                _system.ReadDword(UpdatePolicyKey, "DeferFeatureUpdatesPeriodInDays") == 365;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[UpdateShield] Could not read feature-update policy: {ex.Message}");
        }

        try
        {
            status.IsNoForcedRebootEnabled =
                _system.ReadDword(AuPolicyKey, "NoAutoRebootWithLoggedOnUsers") == 1;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[UpdateShield] Could not read reboot policy: {ex.Message}");
        }

        return status;
    }

    public Task<bool> SetSecurityOnlyUpdatesAsync(bool enable) => Task.Run(() =>
    {
        try
        {
            if (enable)
            {
                _system.WriteDword(UpdatePolicyKey, "DeferFeatureUpdates", 1);
                _system.WriteDword(UpdatePolicyKey, "DeferFeatureUpdatesPeriodInDays", 365);
            }
            else
            {
                _system.DeleteValue(UpdatePolicyKey, "DeferFeatureUpdates");
                _system.DeleteValue(UpdatePolicyKey, "DeferFeatureUpdatesPeriodInDays");

                // AxePower builds before 2.0.9 wrote this retired value. Remove only the exact
                // value AxePower used, leaving an administrator's different policy untouched.
                if (_system.ReadDword(UpdatePolicyKey, "BranchReadinessLevel") == 16)
                    _system.DeleteValue(UpdatePolicyKey, "BranchReadinessLevel");
            }

            bool verified = GetStatus().IsSecurityOnlyEnabled == enable;
            if (!verified)
            {
                _log.LogError("[UpdateShield] Security-only policy did not verify after writing.");
                return false;
            }

            _log.LogInfo(enable
                ? "[UpdateShield] Deferred feature updates for 365 days; quality/security updates remain enabled."
                : "[UpdateShield] Restored the default feature-update deferral policy.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("[UpdateShield] Failed to set security-only policy", ex);
            return false;
        }
    });

    public Task<bool> SetNoForcedRebootAsync(bool enable) => Task.Run(() =>
    {
        try
        {
            if (enable)
            {
                // Do not set AUOptions here: changing how updates download is unrelated to
                // preventing a restart while somebody is signed in.
                _system.WriteDword(AuPolicyKey, "NoAutoRebootWithLoggedOnUsers", 1);
            }
            else
            {
                _system.DeleteValue(AuPolicyKey, "NoAutoRebootWithLoggedOnUsers");

                // Clean up the download-policy value written by older AxePower releases.
                if (_system.ReadDword(AuPolicyKey, "AUOptions") == 3)
                    _system.DeleteValue(AuPolicyKey, "AUOptions");
            }

            bool verified = GetStatus().IsNoForcedRebootEnabled == enable;
            if (!verified)
            {
                _log.LogError("[UpdateShield] No-forced-reboot policy did not verify after writing.");
                return false;
            }

            _log.LogInfo(enable
                ? "[UpdateShield] Blocked automatic restart while a user is logged on."
                : "[UpdateShield] Restored the default logged-on-user restart policy.");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("[UpdateShield] Failed to set no-forced-reboot policy", ex);
            return false;
        }
    });

    public Task<bool> ResetWindowsUpdateCacheAsync(IProgress<string> outputProgress) => Task.Run(() =>
    {
        var restart = new List<string>();
        bool success = true;

        outputProgress.Report("=== Stopping Windows Update services ===");
        try
        {
            foreach (string service in UpdateServices)
            {
                var state = _system.GetServiceState(service);
                if (!state.Exists)
                {
                    outputProgress.Report($"[NOTICE] {service}: {state.Detail}");
                    continue;
                }

                if (!state.IsRunning)
                {
                    outputProgress.Report($"✓ {service}: already stopped; it will remain stopped.");
                    continue;
                }

                var stopped = _system.StopService(service, TimeSpan.FromSeconds(20));
                outputProgress.Report(stopped.Success
                    ? $"✓ {service}: stopped."
                    : $"[ERROR] {service}: {stopped.Detail}");
                if (stopped.Success) restart.Add(service);
                else success = false;
            }

            if (success)
            {
                outputProgress.Report("\n=== Clearing SoftwareDistribution download cache ===");
                var cleaned = _system.ClearUpdateDownloadCache();
                outputProgress.Report(cleaned.Success
                    ? $"✓ {cleaned.Detail}"
                    : $"[ERROR] {cleaned.Detail}");
                success = cleaned.Success;
            }
            else
            {
                outputProgress.Report("\n[ERROR] Cache was not deleted because one or more active update services could not be stopped.");
            }
        }
        catch (Exception ex)
        {
            success = false;
            outputProgress.Report($"[ERROR] Update cache reset failed: {ex.Message}");
            _log.LogError("[UpdateShield] Windows Update cache reset failed", ex);
        }
        finally
        {
            outputProgress.Report("\n=== Restoring services that AxePower stopped ===");
            foreach (string service in restart)
            {
                var started = _system.StartService(service, TimeSpan.FromSeconds(20));
                outputProgress.Report(started.Success
                    ? $"✓ {service}: running."
                    : $"[ERROR] {service}: {started.Detail}");
                if (!started.Success) success = false;
            }
        }

        outputProgress.Report(success
            ? "\n=== Windows Update cache reset completed successfully ==="
            : "\n=== Windows Update cache reset finished with errors ===");
        if (success) _log.LogInfo("[UpdateShield] Windows Update cache reset completed successfully.");
        return success;
    });
}
