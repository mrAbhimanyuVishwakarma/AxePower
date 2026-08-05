using System;

namespace AxePower.Core.Models;

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = "INFO";

    public override string ToString()
    {
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] {Message}";
    }
}
