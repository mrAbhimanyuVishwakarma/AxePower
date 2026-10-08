using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using AxePower.Core.Models;
using AxePower.Core.Services.Diagnostics;

namespace AxePower.Core.Services;

/// <summary>
/// The one way AxePower records what happened. Every event is structured (level, category, event
/// ID, correlation ID) and every text field is redacted here, so no caller can leak a token, a room
/// link, an email address or the user's name. With a <see cref="DiagnosticsStore"/> the events are
/// also kept on disk for a limited time; without one (tests, tools) nothing touches the disk.
/// Thread-safe: services log from timers, sockets and the UI thread at once.
/// </summary>
public class LogService
{
    private static readonly AsyncLocal<string?> Correlation = new();
    private static readonly Regex LeadingTag = new(@"^\[(?<tag>[A-Za-z][A-Za-z0-9 ]{0,40})\]\s*", RegexOptions.CultureInvariant);

    private readonly DiagnosticsStore? _store;

    public ObservableCollection<LogEntry> Logs { get; } = new ObservableCollection<LogEntry>();

    /// <summary>In-memory only: nothing is written to disk. For tests and tools.</summary>
    public LogService() : this(null) { }

    /// <summary>Keeps events in <paramref name="store"/> as well as in memory.</summary>
    public LogService(DiagnosticsStore? store)
    {
        _store = store;
    }

    /// <summary>Where events are kept on disk, or null when they are not.</summary>
    public DiagnosticsStore? Store => _store;

    public event EventHandler<LogEntry>? OnLogAdded;

    // -- The calls the code base uses -------------------------------------------

    public void LogInfo(string message, [CallerFilePath] string file = "", [CallerMemberName] string member = "") =>
        Write(LogLevels.Info, message, null, includeStack: false, CallSite(file, member));

    public void LogWarning(string message, [CallerFilePath] string file = "", [CallerMemberName] string member = "") =>
        Write(LogLevels.Warning, message, null, includeStack: false, CallSite(file, member));

    /// <summary>A handled failure worth knowing about: the message plus the exception's type and message, no stack.</summary>
    public void LogWarning(string message, Exception ex, [CallerFilePath] string file = "", [CallerMemberName] string member = "") =>
        Write(LogLevels.Warning, message, ex, includeStack: false, CallSite(file, member));

    public void LogError(string message, Exception? ex = null, [CallerFilePath] string file = "", [CallerMemberName] string member = "") =>
        Write(LogLevels.Error, message, ex, includeStack: true, CallSite(file, member));

    /// <summary>An event with a fixed ID and category.</summary>
    public void Log(string level, string category, LogEventId eventId, string message, Exception? ex = null)
    {
        Emit(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            EventId = eventId.Id,
            EventName = eventId.Name,
            CorrelationId = Correlation.Value,
            Message = LogRedactor.Redact(message),
            Exception = DescribeException(ex, includeStack: level == LogLevels.Error),
        });
    }

    // -- Correlation ------------------------------------------------------------

    /// <summary>The correlation ID of the operation running on this async flow, if any.</summary>
    public static string? CurrentCorrelationId => Correlation.Value;

    /// <summary>
    /// Marks the events logged until the scope is disposed (on this async flow) as one operation.
    /// Nested scopes get their own ID and restore the outer one when they end.
    /// </summary>
    public static IDisposable BeginCorrelation()
    {
        var previous = Correlation.Value;
        Correlation.Value = Guid.NewGuid().ToString("N")[..8];
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Correlation.Value = previous;
        }
    }

    // -- The in-app log list ----------------------------------------------------

    /// <summary>Adds an entry to <see cref="Logs"/>. Call on the UI thread (LogsViewModel does).</summary>
    public void AddLogToCollection(LogEntry entry)
    {
        Logs.Insert(0, entry);
        if (Logs.Count > 1000)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    // -- Internals --------------------------------------------------------------

    private void Write(string level, string message, Exception? ex, bool includeStack, string callSite)
    {
        var category = "App";
        var match = LeadingTag.Match(message);
        if (match.Success)
        {
            category = match.Groups["tag"].Value.Replace(" ", string.Empty);
            message = message[match.Length..];
        }

        Emit(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            EventId = 0,
            EventName = callSite,
            CorrelationId = Correlation.Value,
            Message = LogRedactor.Redact(message),
            Exception = DescribeException(ex, includeStack),
        });
    }

    private void Emit(LogEntry entry)
    {
        OnLogAdded?.Invoke(this, entry);
        _store?.Append(entry);
    }

    private static string? DescribeException(Exception? ex, bool includeStack)
    {
        if (ex is null) return null;
        return includeStack ? LogRedactor.Redact(ex.ToString()) : LogRedactor.Describe(ex);
    }

    /// <summary>"LanDropService.StartServerAsync": the file name only, never the build machine's path.</summary>
    private static string CallSite(string file, string member)
    {
        var type = string.IsNullOrEmpty(file) ? "?" : Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/')[^1]);
        return string.IsNullOrEmpty(member) ? type : $"{type}.{member}";
    }
}
