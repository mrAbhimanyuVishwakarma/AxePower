using System;

namespace AxePower.Core.Models;

public class CommandResult
{
    public bool Started { get; set; }
    public bool Completed { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public bool TimedOut { get; set; }
    public bool Cancelled { get; set; }
    public Exception? Exception { get; set; }
    public bool Success => Completed && ExitCode == 0 && !TimedOut && !Cancelled;
}
