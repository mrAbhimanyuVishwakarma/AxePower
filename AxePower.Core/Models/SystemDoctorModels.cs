using System;
using System.Collections.Generic;

namespace AxePower.Core.Models;

public enum DiagnosticTaskType
{
    SfcScan,
    DismRepair,
    WinGetReset,
    NetworkReset
}

public class CacheCategorySummary
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool IsSelected { get; set; } = true;
    public string FormattedSize => FormatBytes(TotalSizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024.0)
        {
            return $"{(mb / 1024.0):F2} GB";
        }
        return $"{mb:F1} MB";
    }
}

public class CacheCleanResult
{
    public long BytesCleaned { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesSkipped { get; set; }
    public string FormattedCleanedSize => CacheCategorySummary.FormatBytes(BytesCleaned);
}

public class DnsProviderItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PrimaryIpv4 { get; set; } = string.Empty;
    public string SecondaryIpv4 { get; set; } = string.Empty;
    public string PrimaryIpv6 { get; set; } = string.Empty;
    public string SecondaryIpv6 { get; set; } = string.Empty;
    public long LatencyMs { get; set; } = -1;
    public bool IsActive { get; set; }
    public bool IsFastest { get; set; }
    public bool IsDhcp { get; set; }

    public string FormattedLatency => LatencyMs >= 0 ? $"{LatencyMs} ms" : "--";
}
