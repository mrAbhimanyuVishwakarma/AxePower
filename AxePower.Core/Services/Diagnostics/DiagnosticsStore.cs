using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Models;

namespace AxePower.Core.Services.Diagnostics;

/// <summary>How much diagnostics history is kept. The oldest files go first when either limit is reached.</summary>
public sealed record DiagnosticsRetention(TimeSpan MaxAge, long MaxTotalBytes, long MaxFileBytes)
{
    /// <summary>The owner's choice (2026-10-06): 14 days or 20 MB, in files of at most 2 MB.</summary>
    public static DiagnosticsRetention Default { get; } = new(TimeSpan.FromDays(14), 20L * 1024 * 1024, 2L * 1024 * 1024);
}

/// <summary>One file in the diagnostics folder.</summary>
public sealed record DiagnosticsFile(string Name, string FullPath, long Bytes, DateTime LastWriteUtc);

/// <summary>What the Settings page shows about stored diagnostics.</summary>
public sealed record DiagnosticsSummary(int Files, long Bytes, DateTime? OldestUtc);

/// <summary>The outcome of deleting diagnostics: what went and what could not be removed.</summary>
public sealed record DiagnosticsDeleteResult(int Deleted, IReadOnlyList<string> Failed)
{
    public bool Complete => Failed.Count == 0;
}

/// <summary>
/// The diagnostics folder: structured events as JSON Lines, one file per day, rolled at
/// <see cref="DiagnosticsRetention.MaxFileBytes"/>, pruned by age and total size. Appends are
/// serialised by one lock, and each write opens, appends and closes the file, so a reader
/// (export, the Settings page) or Delete all never finds it held open. Nothing here uploads anything.
/// </summary>
public sealed class DiagnosticsStore
{
    public const string FilePrefix = "axepower-";
    public const string FileExtension = ".jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private string? _currentPath;
    private DateTime _currentDay;

    public DiagnosticsStore(string directory, DiagnosticsRetention? retention = null, Func<DateTime>? utcNow = null)
    {
        Directory = directory;
        Retention = retention ?? DiagnosticsRetention.Default;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>%LOCALAPPDATA%\AxePower\Diagnostics: this machine only, never roamed.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Diagnostics");

    /// <summary>Where AxePower.log lived before Prompt 23 (%APPDATA%\AxePower).</summary>
    public static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxePower");

    public static readonly IReadOnlyList<string> LegacyLogNames = new[] { "AxePower.log", "AxePower.log.old" };

    public string Directory { get; }

    public DiagnosticsRetention Retention { get; }

    /// <summary>Writes one event. Never throws: logging runs inside other catch blocks.</summary>
    public void Append(LogEntry entry)
    {
        var line = Serialize(entry, _utcNow());
        lock (_gate)
        {
            try
            {
                var path = CurrentFile(Encoding.UTF8.GetByteCount(line) + 2);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                // Best-effort: a full disk or a locked folder must not take logging (or the app) down.
                // The event still reached the in-app log.
                _currentPath = null;
            }
        }
    }

    /// <summary>The diagnostics files, newest first.</summary>
    public IReadOnlyList<DiagnosticsFile> Files()
    {
        lock (_gate) return ListFiles();
    }

    public DiagnosticsSummary Summarize()
    {
        var files = Files();
        return new DiagnosticsSummary(files.Count, files.Sum(f => f.Bytes), files.Count == 0 ? null : files.Min(f => f.LastWriteUtc));
    }

    /// <summary>The stored events, oldest first. Lines that cannot be read are skipped.</summary>
    public IReadOnlyList<LogEntry> ReadEntries(int max = int.MaxValue)
    {
        var entries = new List<LogEntry>();
        foreach (var file in Files().Reverse())
        {
            foreach (var line in ReadLines(file.FullPath))
            {
                if (Deserialize(line) is { } entry) entries.Add(entry);
            }
        }
        return entries.Count > max ? entries.GetRange(entries.Count - max, max) : entries;
    }

    /// <summary>A file's text exactly as stored, for the preview. Readable while it is being written.</summary>
    public static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Removes files past the age limit, then the oldest until the total fits. Returns how many went.</summary>
    public int EnforceRetention()
    {
        lock (_gate)
        {
            int removed = 0;
            var now = _utcNow();
            var files = ListFiles().ToList();
            foreach (var file in files.Where(f => now - f.LastWriteUtc > Retention.MaxAge).ToList())
            {
                if (TryDelete(file.FullPath)) removed++;
                files.Remove(file);
            }

            long total = files.Sum(f => f.Bytes);
            foreach (var file in Enumerable.Reverse(files).ToList())
            {
                if (total <= Retention.MaxTotalBytes) break;
                if (file.FullPath == _currentPath) continue;
                if (TryDelete(file.FullPath))
                {
                    removed++;
                    total -= file.Bytes;
                }
            }
            return removed;
        }
    }

    /// <summary>Deletes every diagnostics file. Only files this store writes are touched.</summary>
    public DiagnosticsDeleteResult DeleteAll()
    {
        lock (_gate)
        {
            var failed = new List<string>();
            int deleted = 0;
            foreach (var file in ListFiles())
            {
                if (TryDelete(file.FullPath)) deleted++;
                else failed.Add(file.Name);
            }
            _currentPath = null;
            return new DiagnosticsDeleteResult(deleted, failed);
        }
    }

    /// <summary>Deletes AxePower.log and AxePower.log.old from before Prompt 23. Returns how many went.</summary>
    public static int DeleteLegacyLogs(string legacyDirectory)
    {
        int removed = 0;
        foreach (var name in LegacyLogNames)
        {
            var path = Path.Combine(legacyDirectory, name);
            if (File.Exists(path) && TryDelete(path)) removed++;
        }
        return removed;
    }

    // -- Format ---------------------------------------------------------------

    private sealed class Line
    {
        [JsonPropertyName("time")] public DateTime Time { get; set; }
        [JsonPropertyName("level")] public string Level { get; set; } = LogLevels.Info;
        [JsonPropertyName("category")] public string Category { get; set; } = "App";
        [JsonPropertyName("eventId")] public int EventId { get; set; }
        [JsonPropertyName("event")] public string EventName { get; set; } = string.Empty;
        [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("exception")] public string? Exception { get; set; }
    }

    internal static string Serialize(LogEntry entry, DateTime fallbackUtc) => JsonSerializer.Serialize(new Line
    {
        Time = entry.Timestamp == default ? fallbackUtc : entry.Timestamp.ToUniversalTime(),
        Level = entry.Level,
        Category = entry.Category,
        EventId = entry.EventId,
        EventName = entry.EventName,
        CorrelationId = entry.CorrelationId,
        Message = entry.Message,
        Exception = entry.Exception,
    }, Json);

    internal static LogEntry? Deserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<Line>(line, Json);
            if (parsed is null) return null;
            return new LogEntry
            {
                Timestamp = DateTime.SpecifyKind(parsed.Time, DateTimeKind.Utc).ToLocalTime(),
                Level = parsed.Level ?? LogLevels.Info,
                Category = parsed.Category ?? "App",
                EventId = parsed.EventId,
                EventName = parsed.EventName ?? string.Empty,
                CorrelationId = parsed.CorrelationId,
                Message = parsed.Message ?? string.Empty,
                Exception = parsed.Exception,
            };
        }
        catch (JsonException)
        {
            // A torn last line (power cut mid-write) or a hand edit: skip it.
            return null;
        }
    }

    // -- Files ----------------------------------------------------------------

    private string CurrentFile(int incomingBytes)
    {
        var day = _utcNow().Date;
        if (_currentPath is not null && day == _currentDay && File.Exists(_currentPath)
            && new FileInfo(_currentPath).Length + incomingBytes <= Retention.MaxFileBytes)
        {
            return _currentPath;
        }

        System.IO.Directory.CreateDirectory(Directory);
        var stem = FilePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        // Continue from today's last part: parts freed by retention must not be reused, or new
        // events would land in a file that sorts as the oldest.
        var today = stem[FilePrefix.Length..];
        int lastPart = ListFiles().Select(f => SequenceKey(f.Name)).Where(k => k.Day == today).Select(k => k.Part).DefaultIfEmpty(1).Max();
        for (int part = Math.Max(1, lastPart); ; part++)
        {
            var path = Path.Combine(Directory, part == 1 ? stem + FileExtension : $"{stem}-{part}{FileExtension}");
            if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= Retention.MaxFileBytes)
            {
                bool rolled = _currentPath is not null && _currentPath != path;
                _currentPath = path;
                _currentDay = day;
                if (rolled) EnforceRetention();
                return path;
            }
        }
    }

    private List<DiagnosticsFile> ListFiles()
    {
        if (!System.IO.Directory.Exists(Directory)) return new List<DiagnosticsFile>();
        try
        {
            return new DirectoryInfo(Directory)
                .EnumerateFiles(FilePrefix + "*" + FileExtension)
                .Select(f => new DiagnosticsFile(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc))
                .OrderByDescending(f => SequenceKey(f.Name))
                .ThenByDescending(f => f.LastWriteUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<DiagnosticsFile>();
        }
    }

    /// <summary>
    /// The order files were written in, from the name (axepower-YYYYMMDD[-N].jsonl). Write times tie
    /// when a busy day rolls several files within the clock's resolution; names do not.
    /// </summary>
    private static (string Day, int Part) SequenceKey(string name)
    {
        var stem = name[FilePrefix.Length..^FileExtension.Length];
        var dash = stem.IndexOf('-');
        if (dash < 0) return (stem, 1);
        return int.TryParse(stem[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var part)
            ? (stem[..dash], part)
            : (stem, 0);
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        string text;
        try { text = ReadText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in text.Split('\n')) yield return line.TrimEnd('\r');
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
