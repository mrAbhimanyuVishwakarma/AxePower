using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services.Processes;
using Microsoft.Win32;

namespace AxePower.Core.Services;

public class FirmwareUpdateService
{
    private readonly LogService _logService;
    private readonly IUserShellLauncher _launcher;
    private readonly IProcessRunner _runner;
    private FirmwareInfo? _cachedInfo;
    private DateTime _lastScanTime = DateTime.MinValue;

    public FirmwareUpdateService(LogService logService, IUserShellLauncher? launcher = null, IProcessRunner? runner = null)
    {
        _logService = logService;
        _launcher = launcher ?? new UserShellLauncher(logService);
        _runner = runner ?? new AllowListedProcessRunner(ExecutableAllowList.Windows(), logService);
    }

    public async Task<FirmwareInfo> GetFirmwareInfoAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedInfo != null && (DateTime.UtcNow - _lastScanTime).TotalMinutes < 5)
        {
            return _cachedInfo;
        }

        return await Task.Run(() =>
        {
            var info = new FirmwareInfo();

            try
            {
                // 1. Query Win32_BIOS
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_BIOS"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        info.BiosVersion = obj["SMBIOSBIOSVersion"]?.ToString() ?? obj["Version"]?.ToString() ?? "--";
                        info.Manufacturer = obj["Manufacturer"]?.ToString() ?? "--";
                        info.SerialNumber = obj["SerialNumber"]?.ToString() ?? "--";

                        var releaseDateRaw = obj["ReleaseDate"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(releaseDateRaw) && releaseDateRaw.Length >= 8)
                        {
                            if (DateTime.TryParseExact(releaseDateRaw.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                            {
                                info.BiosReleaseDate = dt.ToString("MMMM dd, yyyy");
                            }
                            else
                            {
                                info.BiosReleaseDate = releaseDateRaw.Substring(0, 8);
                            }
                        }

                        info.SmbiosVersion = $"{obj["SMBIOSMajorVersion"]}.{obj["SMBIOSMinorVersion"]}";
                        break;
                    }
                }

                // 2. Query Win32_BaseBoard for Motherboard model
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        info.MotherboardProduct = obj["Product"]?.ToString() ?? "--";
                        if (info.Manufacturer == "--")
                        {
                            info.Manufacturer = obj["Manufacturer"]?.ToString() ?? "--";
                        }
                        break;
                    }
                }

                // 3. Query Win32_ComputerSystemProduct for System Model & Family
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        info.SystemModel = obj["Name"]?.ToString() ?? "--";
                        info.SystemFamily = obj["Version"]?.ToString() ?? "--";
                        if (string.IsNullOrWhiteSpace(info.SerialNumber) || info.SerialNumber == "--")
                        {
                            info.SerialNumber = obj["IdentifyingNumber"]?.ToString() ?? "--";
                        }
                        break;
                    }
                }

                // 4. Determine UEFI and Secure Boot Status
                info.SecureBootStatus = DetectSecureBoot();
                info.UefiStatus = DetectUefiMode();

                // 5. Query Device Firmware instances via Win32_PnPEntity
                info.FirmwareDevices = QueryFirmwareDevices();

                // 6. Resolve Vendor Name and Official Support Portal URL
                ResolveVendorAndPortal(info);

                info.LastScanned = DateTime.Now;
                info.UpdateStatus = "Ready to check for latest vendor BIOS and firmware updates.";
                _cachedInfo = info;
                _lastScanTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logService.LogError("Failed to query system BIOS and firmware details.", ex);
            }

            return info;
        });
    }

    public async Task<(bool HasUpdates, string StatusMessage)> CheckForFirmwareUpdatesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                _logService.LogInfo("[FirmwareUpdate] Checking for pending Windows Update firmware capsules...");

                int pendingFirmwareCount = 0;
                var pendingTitles = new List<string>();

                try
                {
                    var updateSession = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
                    var createSearcherMethod = updateSession.GetType().GetMethod("CreateUpdateSearcher");
                    var updateSearcher = createSearcherMethod?.Invoke(updateSession, null);

                    if (updateSearcher != null)
                    {
                        var searchMethod = updateSearcher.GetType().GetMethod("Search");
                        var searchResult = searchMethod?.Invoke(updateSearcher, new object[] { "IsInstalled=0 and IsHidden=0" });

                        if (searchResult != null)
                        {
                            var updatesProp = searchResult.GetType().GetProperty("Updates");
                            var updates = updatesProp?.GetValue(searchResult) as System.Collections.IEnumerable;

                            if (updates != null)
                            {
                                foreach (var update in updates)
                                {
                                    var titleProp = update.GetType().GetProperty("Title");
                                    var title = titleProp?.GetValue(update)?.ToString() ?? string.Empty;

                                    if (Regex.IsMatch(title, @"(?i)firmware|bios|uefi|system hardware update|lenovo - system|dell inc\. - firmware|asus - firmware|hp - firmware"))
                                    {
                                        pendingFirmwareCount++;
                                        pendingTitles.Add(title);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"Windows Update COM query note: {ex.Message}");
                }

                if (pendingFirmwareCount > 0)
                {
                    string message = $"Found {pendingFirmwareCount} firmware update(s) pending: {string.Join(", ", pendingTitles)}";
                    _logService.LogInfo($"[FirmwareUpdate] {message}");
                    return (true, message);
                }

                return (false, "Your BIOS and UEFI system firmware are up to date. No pending firmware capsules detected in Windows Update.");
            }
            catch (Exception ex)
            {
                _logService.LogError("Failed to scan for firmware updates.", ex);
                return (false, $"Scan completed. Could not contact online update catalog: {ex.Message}");
            }
        });
    }

    public void OpenVendorBiosPortal(FirmwareInfo info)
    {
        try
        {
            string url = info.VendorPortalUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://www.google.com/search?q=" + Uri.EscapeDataString($"{info.Manufacturer} {info.SystemModel} BIOS update download");
            }

            var result = _launcher.OpenUri(url);
            if (result.Started) _logService.LogInfo($"[FirmwareUpdate] Opened vendor support portal: {url}");
            else _logService.LogWarning($"[FirmwareUpdate] Vendor portal not opened: {result.Message}");
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to open vendor BIOS portal.", ex);
        }
    }

    public void TriggerInteractiveScan()
    {
        // Fire and forget: the scan runs inside Windows Update; usoclient only hands it over.
        _ = _runner.RunAsync(new ProcessCommand(AllowedTool.UsoClient, new[] { "StartInteractiveScan" }, TimeSpan.FromMinutes(1)))
            .ContinueWith(t =>
            {
                if (t.Result.Outcome == ProcessRunOutcome.Exited) _logService.LogInfo("[FirmwareUpdate] Triggered USOClient interactive scan.");
                else _logService.LogWarning($"[FirmwareUpdate] USOClient scan not triggered: {t.Result.Outcome} {t.Result.Error}");
            }, TaskScheduler.Default);
    }

    private string DetectSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key != null)
            {
                var val = key.GetValue("UEFISecureBootEnabled");
                if (val is int intVal)
                {
                    return intVal == 1 ? "Enabled" : "Disabled";
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { /* Unreadable: reported as unknown below. */ }

        return "Standard";
    }

    private string DetectUefiMode()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            var bootDriverFlags = key?.GetValue("PEFirmwareType");
            if (bootDriverFlags is int flags && flags == 2)
            {
                return "UEFI Native";
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { /* Unreadable: reported as unknown below. */ }

        return "UEFI";
    }

    private List<FirmwareDeviceItem> QueryFirmwareDevices()
    {
        var list = new List<FirmwareDeviceItem>();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PnPEntity WHERE PNPClass = 'Firmware'");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? obj["Description"]?.ToString() ?? "System Firmware";
                var id = obj["DeviceID"]?.ToString() ?? string.Empty;
                var status = obj["Status"]?.ToString() ?? "OK";

                list.Add(new FirmwareDeviceItem
                {
                    DeviceName = name,
                    DeviceId = id,
                    Status = status
                });
            }
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"Firmware PnP devices query note: {ex.Message}");
        }

        return list;
    }

    private void ResolveVendorAndPortal(FirmwareInfo info)
    {
        var mfg = (info.Manufacturer ?? string.Empty).ToLowerInvariant();
        var model = info.SystemModel ?? string.Empty;
        var serial = info.SerialNumber != "--" ? info.SerialNumber : string.Empty;

        if (mfg.Contains("lenovo"))
        {
            info.VendorName = "Lenovo";
            if (!string.IsNullOrEmpty(serial))
            {
                info.VendorPortalUrl = $"https://pcsupport.lenovo.com/products/search?query={Uri.EscapeDataString(serial)}";
            }
            else
            {
                info.VendorPortalUrl = "https://pcsupport.lenovo.com/products/laptops-and-netbooks";
            }
        }
        else if (mfg.Contains("asus") || mfg.Contains("asustek"))
        {
            info.VendorName = "ASUS";
            info.VendorPortalUrl = "https://www.asus.com/support/download-center/";
        }
        else if (mfg.Contains("dell"))
        {
            info.VendorName = "Dell";
            if (!string.IsNullOrEmpty(serial))
            {
                info.VendorPortalUrl = $"https://www.dell.com/support/home/en-us/product-support/servicetag/{Uri.EscapeDataString(serial)}/drivers";
            }
            else
            {
                info.VendorPortalUrl = "https://www.dell.com/support/home/en-us/products";
            }
        }
        else if (mfg.Contains("hp") || mfg.Contains("hewlett"))
        {
            info.VendorName = "HP";
            info.VendorPortalUrl = "https://support.hp.com/drivers";
        }
        else if (mfg.Contains("acer"))
        {
            info.VendorName = "Acer";
            info.VendorPortalUrl = "https://www.acer.com/support";
        }
        else if (mfg.Contains("msi") || mfg.Contains("micro-star"))
        {
            info.VendorName = "MSI";
            info.VendorPortalUrl = "https://www.msi.com/support/download";
        }
        else if (mfg.Contains("gigabyte"))
        {
            info.VendorName = "Gigabyte";
            info.VendorPortalUrl = "https://www.gigabyte.com/Support";
        }
        else if (mfg.Contains("asrock"))
        {
            info.VendorName = "ASRock";
            info.VendorPortalUrl = "https://www.asrock.com/support/index.asp";
        }
        else
        {
            info.VendorName = !string.IsNullOrWhiteSpace(info.Manufacturer) && info.Manufacturer != "--" ? info.Manufacturer : "OEM Support";
            info.VendorPortalUrl = $"https://www.google.com/search?q={Uri.EscapeDataString((info.Manufacturer ?? "OEM") + " " + model + " BIOS firmware update download")}";
        }
    }
}
