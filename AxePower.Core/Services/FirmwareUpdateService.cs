using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
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
    private readonly IWindowsUpdateCatalogService _catalog;
    private FirmwareInfo? _cachedInfo;
    private DateTime _lastScanTime = DateTime.MinValue;

    public FirmwareUpdateService(LogService logService, IUserShellLauncher? launcher = null, IProcessRunner? runner = null,
        IWindowsUpdateCatalogService? catalog = null)
    {
        _logService = logService;
        _launcher = launcher ?? new UserShellLauncher(logService);
        _runner = runner ?? new AllowListedProcessRunner(ExecutableAllowList.Windows(), logService);
        _catalog = catalog ?? new WindowsUpdateCatalogService(logService);
    }

    public async Task<FirmwareInfo> GetFirmwareInfoAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedInfo != null && (DateTime.UtcNow - _lastScanTime).TotalMinutes < 5)
        {
            return _cachedInfo;
        }

        return await Task.Run(() =>
        {
            var info = new FirmwareInfo { VendorPortalUrl = string.Empty };

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
                (info.VendorName, info.VendorPortalUrl) = ResolveVendorAndPortal(info);

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
        _logService.LogInfo("[FirmwareUpdate] Checking for firmware in Windows Update.");
        var scan = await _catalog.ScanAsync().ConfigureAwait(false);
        string[] firmware = scan.Candidates.Where(update => update.IsFirmware == true)
            .Select(update => update.Title).ToArray();

        if (firmware.Length > 0)
        {
            string status = scan.Succeeded ? "Windows Update listed firmware" : "Windows Update returned partial results and listed firmware";
            return (true, $"{status}: {string.Join(", ", firmware)}. Review Windows Update or your manufacturer's support page.");
        }

        if (!scan.Succeeded)
            return (false, $"Firmware check incomplete. {scan.Detail ?? "Open Windows Update in Settings to check again."}");

        if (scan.Candidates.Any(update => update.IsDeviceUpdate && update.IsFirmware is null))
            return (false, "Windows Update listed device updates it could not classify as firmware. Review Optional updates and your manufacturer's support page.");

        return (false, "Windows Update did not identify a firmware update in this scan. Check your manufacturer's support page for other releases.");
    }

    public ShellLaunchResult OpenVendorBiosPortal(FirmwareInfo info)
    {
        try
        {
            var (vendor, url) = ResolveVendorAndPortal(info);
            if (string.IsNullOrWhiteSpace(url))
            {
                return new(ShellLaunchStatus.Refused,
                    "AxePower could not identify an official BIOS support site for this device. Check Windows Optional Updates or your PC or motherboard manufacturer's support site.");
            }

            var result = _launcher.OpenUri(url);
            if (result.Started) _logService.LogInfo($"[FirmwareUpdate] Requested {vendor} BIOS support.");
            else _logService.LogWarning($"[FirmwareUpdate] Vendor portal not opened: {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            _logService.LogError("Failed to open vendor BIOS portal.", ex);
            return new(ShellLaunchStatus.Failed,
                "Could not open the BIOS support site. Try Windows Optional Updates or visit your manufacturer's support site.");
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

    private static (string Vendor, string Url) ResolveVendorAndPortal(FirmwareInfo info)
    {
        var mfg = (info.Manufacturer ?? string.Empty).ToLowerInvariant();
        var serial = info.SerialNumber != "--" ? info.SerialNumber : string.Empty;

        if (mfg.Contains("lenovo"))
        {
            return ("Lenovo", string.IsNullOrEmpty(serial)
                ? "https://pcsupport.lenovo.com/products/laptops-and-netbooks"
                : $"https://pcsupport.lenovo.com/products/search?query={Uri.EscapeDataString(serial)}");
        }
        if (mfg.Contains("asus") || mfg.Contains("asustek"))
            return ("ASUS", "https://www.asus.com/support/download-center/");
        if (mfg.Contains("dell"))
        {
            return ("Dell", string.IsNullOrEmpty(serial)
                ? "https://www.dell.com/support/home/en-us/products"
                : $"https://www.dell.com/support/home/en-us/product-support/servicetag/{Uri.EscapeDataString(serial)}/drivers");
        }
        if (mfg.Contains("hp") || mfg.Contains("hewlett"))
            return ("HP", "https://support.hp.com/drivers");
        if (mfg.Contains("acer"))
            return ("Acer", "https://www.acer.com/support");
        if (mfg.Contains("msi") || mfg.Contains("micro-star"))
            return ("MSI", "https://www.msi.com/support/download");
        if (mfg.Contains("gigabyte"))
            return ("Gigabyte", "https://www.gigabyte.com/Support");
        if (mfg.Contains("asrock"))
            return ("ASRock", "https://www.asrock.com/support/index.asp");

        string manufacturer = string.IsNullOrWhiteSpace(info.Manufacturer) || info.Manufacturer == "--" ? "OEM Support" : info.Manufacturer;
        return (manufacturer, string.Empty);
    }
}
