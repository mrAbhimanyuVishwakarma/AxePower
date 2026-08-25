using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class WindowsUpdateShieldService : IWindowsUpdateShieldService
{
    private readonly LogService _log;

    private const string UpdatePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string AuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    public WindowsUpdateShieldService(LogService log)
    {
        _log = log;
    }

    public WindowsUpdateShieldStatus GetStatus()
    {
        var status = new WindowsUpdateShieldStatus();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UpdatePolicyKey);
            if (key != null)
            {
                var deferVal = key.GetValue("DeferFeatureUpdates");
                status.IsSecurityOnlyEnabled = (deferVal is int intVal && intVal == 1);
            }
        }
        catch { }

        try
        {
            using var auKey = Registry.LocalMachine.OpenSubKey(AuPolicyKey);
            if (auKey != null)
            {
                var noRebootVal = auKey.GetValue("NoAutoRebootWithLoggedOnUsers");
                status.IsNoForcedRebootEnabled = (noRebootVal is int intVal && intVal == 1);
            }
        }
        catch { }

        return status;
    }

    public async Task<bool> SetSecurityOnlyUpdatesAsync(bool enable)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (enable)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(UpdatePolicyKey);
                    key?.SetValue("DeferFeatureUpdates", 1, RegistryValueKind.DWord);
                    key?.SetValue("DeferFeatureUpdatesPeriodInDays", 365, RegistryValueKind.DWord);
                    key?.SetValue("BranchReadinessLevel", 16, RegistryValueKind.DWord);
                    _log.LogInfo("[UpdateShield] Enabled Security-Only Updates (Deferred Feature Updates for 365 days).");
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(UpdatePolicyKey, true);
                    key?.DeleteValue("DeferFeatureUpdates", false);
                    key?.DeleteValue("DeferFeatureUpdatesPeriodInDays", false);
                    key?.DeleteValue("BranchReadinessLevel", false);
                    _log.LogInfo("[UpdateShield] Reverted to default Windows Update delivery policy.");
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("[UpdateShield] Failed to set Security-Only policy", ex);
                return false;
            }
        });
    }

    public async Task<bool> SetNoForcedRebootAsync(bool enable)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (enable)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(AuPolicyKey);
                    key?.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
                    key?.SetValue("AUOptions", 3, RegistryValueKind.DWord); // Notify download and auto install
                    _log.LogInfo("[UpdateShield] Enabled No Forced Reboots for logged-on users.");
                }
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(AuPolicyKey, true);
                    key?.DeleteValue("NoAutoRebootWithLoggedOnUsers", false);
                    _log.LogInfo("[UpdateShield] Reverted forced reboot policy to default.");
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("[UpdateShield] Failed to set No-Forced-Reboot policy", ex);
                return false;
            }
        });
    }

    public async Task<bool> ResetWindowsUpdateCacheAsync(IProgress<string> outputProgress)
    {
        return await Task.Run(() =>
        {
            outputProgress.Report("=== Stopping Windows Update & BITS Services ===");
            ExecuteCommand("net.exe", "stop wuauserv", outputProgress);
            ExecuteCommand("net.exe", "stop bits", outputProgress);
            ExecuteCommand("net.exe", "stop cryptSvc", outputProgress);
            ExecuteCommand("net.exe", "stop msiserver", outputProgress);

            outputProgress.Report("\n=== Cleaning Corrupted SoftwareDistribution Download Cache ===");
            try
            {
                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var downloadPath = Path.Combine(winDir, "SoftwareDistribution", "Download");
                if (Directory.Exists(downloadPath))
                {
                    foreach (var file in Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { }
                    }
                    outputProgress.Report($"✓ Purged cached installation files from: {downloadPath}");
                }
            }
            catch (Exception ex)
            {
                outputProgress.Report($"[NOTICE] Cache clean exception: {ex.Message}");
            }

            outputProgress.Report("\n=== Restarting Windows Update & Cryptographic Services ===");
            ExecuteCommand("net.exe", "start cryptSvc", outputProgress);
            ExecuteCommand("net.exe", "start bits", outputProgress);
            ExecuteCommand("net.exe", "start wuauserv", outputProgress);
            ExecuteCommand("net.exe", "start msiserver", outputProgress);

            outputProgress.Report("\n=== Windows Update Reset & Repair Completed ===");
            _log.LogInfo("[UpdateShield] Windows Update cache reset successfully executed.");
            return true;
        });
    }

    private static void ExecuteCommand(string fileName, string args, IProgress<string> outputProgress)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            outputProgress.Report($"> {fileName} {args} (Exit: {proc?.ExitCode ?? -1})");
        }
        catch (Exception ex)
        {
            outputProgress.Report($"[ERROR] Failed to run {fileName} {args}: {ex.Message}");
        }
    }
}
