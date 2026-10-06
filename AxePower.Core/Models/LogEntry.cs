using System;

namespace AxePower.Core.Models;

/// <summary>
/// One structured diagnostics event. Every text field has already been through
/// <see cref="Services.LogRedactor"/> by the time an entry exists, so it is safe to show, store and export.
/// </summary>
public class LogEntry
{
    /// <summary>Local time, for display. The file stores it as UTC.</summary>
    public DateTime Timestamp { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>INFO, WARN or ERROR (see <see cref="Services.Diagnostics.LogLevels"/>).</summary>
    public string Level { get; set; } = "INFO";

    /// <summary>The subsystem, e.g. LanDrop or Music. Taken from a leading "[Tag]" in older call sites.</summary>
    public string Category { get; set; } = "App";

    /// <summary>A fixed number for events that have one (see <see cref="Services.Diagnostics.LogEvents"/>); 0 otherwise.</summary>
    public int EventId { get; set; }

    /// <summary>The event's name, or the call site (Class.Method) when it has no fixed ID.</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>Ties together the events of one operation (one LAN request, one uninstall, one update check).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Exception type, message and (for errors) stack, redacted. Null when there was none.</summary>
    public string? Exception { get; set; }

    public override string ToString()
    {
        var correlation = CorrelationId is null ? string.Empty : $" ({CorrelationId})";
        var exception = Exception is null ? string.Empty : Environment.NewLine + Exception;
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] [{Category}] {Message}{correlation}{exception}";
    }
}
