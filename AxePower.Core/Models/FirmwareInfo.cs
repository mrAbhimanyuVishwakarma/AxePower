using System;
using System.Collections.Generic;

namespace AxePower.Core.Models;

public class FirmwareInfo
{
    public string BiosVersion { get; set; } = "--";
    public string BiosReleaseDate { get; set; } = "--";
    public string Manufacturer { get; set; } = "--";
    public string SystemModel { get; set; } = "--";
    public string SystemFamily { get; set; } = "--";
    public string SerialNumber { get; set; } = "--";
    public string MotherboardProduct { get; set; } = "--";
    public string SmbiosVersion { get; set; } = "--";
    public string SecureBootStatus { get; set; } = "Unknown";
    public string UefiStatus { get; set; } = "UEFI Mode";
    public string VendorPortalUrl { get; set; } = "https://www.google.com";
    public string VendorName { get; set; } = "OEM";
    public List<FirmwareDeviceItem> FirmwareDevices { get; set; } = new();
    public string UpdateStatus { get; set; } = "Ready to scan for BIOS & firmware updates";
    public bool HasPendingUpdates { get; set; }
    public DateTime? LastScanned { get; set; }
}

public class FirmwareDeviceItem
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Status { get; set; } = "OK";
    public string DriverVersion { get; set; } = string.Empty;
    public string DriverDate { get; set; } = string.Empty;
}
