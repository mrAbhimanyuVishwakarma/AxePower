using System;

namespace AxePower.Core.Models;

public class LanDropFileItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FormattedSize => FormatBytes(SizeBytes);
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsReceived { get; set; }
    public string DeviceName { get; set; } = "Unknown Device";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public class LanDropTextMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SenderDevice { get; set; } = "Mobile";
    public bool IsFromPc { get; set; }
}

public class LanDropServerState
{
    public bool IsRunning { get; set; }
    public string SelectedIp { get; set; } = string.Empty;
    public int Port { get; set; } = 5050;
    public string ServerUrl => !string.IsNullOrEmpty(SelectedIp) ? $"http://{SelectedIp}:{Port}/" : string.Empty;
    public int ActiveTransfers { get; set; }
}
