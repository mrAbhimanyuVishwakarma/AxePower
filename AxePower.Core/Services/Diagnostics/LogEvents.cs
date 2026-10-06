namespace AxePower.Core.Services.Diagnostics;

/// <summary>The three levels a log event can have, as they are written and shown.</summary>
public static class LogLevels
{
    public const string Info = "INFO";
    public const string Warning = "WARN";
    public const string Error = "ERROR";
}

/// <summary>A fixed event number and name, so the same event can be found across versions and machines.</summary>
public readonly record struct LogEventId(int Id, string Name);

/// <summary>
/// Events with fixed IDs. Older call sites log without one (ID 0) and are identified by their call
/// site instead. Ranges: 1xxx app lifetime, 2xxx diagnostics itself, 3xxx security-relevant refusals.
/// Never reuse or renumber an ID.
/// </summary>
public static class LogEvents
{
    public static readonly LogEventId AppStarted = new(1000, "App.Started");
    public static readonly LogEventId UnhandledUiException = new(1001, "App.UnhandledUiException");
    public static readonly LogEventId UnhandledException = new(1002, "App.UnhandledException");
    public static readonly LogEventId StartupFailed = new(1003, "App.StartupFailed");
    public static readonly LogEventId UnobservedTaskException = new(1004, "App.UnobservedTaskException");

    public static readonly LogEventId DiagnosticsExported = new(2001, "Diagnostics.Exported");
    public static readonly LogEventId LegacyLogsRemoved = new(2002, "Diagnostics.LegacyLogsRemoved");

    public static readonly LogEventId LanDropAccessRefused = new(3000, "LanDrop.AccessRefused");
    public static readonly LogEventId UpdateVerificationFailed = new(3001, "Update.VerificationFailed");
}
