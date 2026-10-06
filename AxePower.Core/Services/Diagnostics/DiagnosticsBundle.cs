using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AxePower.Core.Models;
using Microsoft.Win32;

namespace AxePower.Core.Services.Diagnostics;

/// <summary>What the user ticked for a diagnostic bundle. Logs and the system summary are always in it.</summary>
public sealed record DiagnosticsBundleOptions(bool IncludeSettings = false, bool IncludeUninstallLogs = false);

/// <summary>One file of a bundle, with the exact text that will be written.</summary>
public sealed record DiagnosticsBundleItem(string Name, string Description, string Content)
{
    public long Bytes => Encoding.UTF8.GetByteCount(Content);
}

/// <summary>
/// Builds a diagnostic bundle in two steps the user sees: <see cref="Preview"/> returns every file
/// with its full text, and <see cref="Write"/> writes exactly those items to a .zip the user chose.
/// Everything is redacted again on the way out. Nothing is sent anywhere: there is no network code here.
/// </summary>
public sealed class DiagnosticsBundleBuilder
{
    /// <summary>Settings that name people, programs, folders or devices; the snapshot keeps only how many there were.</summary>
    internal static readonly IReadOnlySet<string> PersonalSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        "appRules", "appsToClose", "routines", "gpuPreferenceBackups",
        "libraryFolders", "playlistFolder", "lastFmUsername", "outputDeviceId",
        "excludedRegistryPaths", "excludedFilePaths",
        "savedFriends", "systemAudioDeviceId", "microphoneDeviceId", "viewerHostOverride",
    };

    public const int MaxUninstallLogs = 10;
    public const int MaxUninstallLogChars = 256 * 1024;

    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DiagnosticsStore _store;
    private readonly Func<AppSettings>? _settings;
    private readonly string? _uninstallLogDirectory;
    private readonly Func<string> _systemSummary;
    private readonly Func<DateTime> _utcNow;

    public DiagnosticsBundleBuilder(
        DiagnosticsStore store,
        Func<AppSettings>? settings = null,
        string? uninstallLogDirectory = null,
        Func<string>? systemSummary = null,
        Func<DateTime>? utcNow = null)
    {
        _store = store;
        _settings = settings;
        _uninstallLogDirectory = uninstallLogDirectory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _systemSummary = systemSummary ?? (() => SystemSummary(_store.Retention));
    }

    /// <summary>%LOCALAPPDATA%\AxePower\UninstallLogs, where Uninstaller Pro keeps Windows Installer logs.</summary>
    public static string DefaultUninstallLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "UninstallLogs");

    /// <summary>Every file the bundle will contain, with its full text.</summary>
    public IReadOnlyList<DiagnosticsBundleItem> Preview(DiagnosticsBundleOptions options)
    {
        var items = new List<DiagnosticsBundleItem>
        {
            new("system-summary.txt", "App, Windows and hardware versions. No computer or user name.", LogRedactor.Redact(_systemSummary())),
        };

        foreach (var file in _store.Files().Reverse())
        {
            string text;
            try { text = DiagnosticsStore.ReadText(file.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            items.Add(new DiagnosticsBundleItem($"logs/{file.Name}", "AxePower's log for that day, already redacted.", RedactLines(text)));
        }

        if (options.IncludeSettings && _settings is not null)
            items.Add(new DiagnosticsBundleItem("settings.json", "Your settings without people, programs, folders or devices.", SettingsSnapshot(_settings())));

        if (options.IncludeUninstallLogs && _uninstallLogDirectory is not null)
            items.AddRange(UninstallLogs(_uninstallLogDirectory));

        var manifest = new StringBuilder()
            .AppendLine("AxePower diagnostic bundle")
            .AppendLine(CultureInfo.InvariantCulture, $"Created {_utcNow():yyyy-MM-dd HH:mm} UTC on the user's request. Nothing was uploaded.")
            .AppendLine()
            .AppendLine("Files:");
        foreach (var item in items)
            manifest.AppendLine(CultureInfo.InvariantCulture, $"  {item.Name} ({item.Bytes:N0} bytes): {item.Description}");
        items.Insert(0, new DiagnosticsBundleItem("README.txt", "What this bundle contains.", manifest.ToString()));
        return items;
    }

    /// <summary>Writes exactly <paramref name="items"/> (the preview the user saw) to <paramref name="zipPath"/>.</summary>
    public static void Write(IReadOnlyList<DiagnosticsBundleItem> items, string zipPath)
    {
        var temp = zipPath + ".partial";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var item in items)
                {
                    var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write(item.Content);
                }
            }
            File.Move(temp, zipPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort: a leftover .partial is harmless and overwritten next time. */ }
        }
    }

    // -- Parts ----------------------------------------------------------------

    /// <summary>Settings as JSON with personal lists replaced by a count, then redacted.</summary>
    internal static string SettingsSnapshot(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsJson);
        RemovePersonal(node);
        return LogRedactor.Redact(node?.ToJsonString(SettingsJson) ?? "{}");
    }

    private static void RemovePersonal(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    if (PersonalSettings.Contains(key))
                    {
                        obj[key] = value switch
                        {
                            null => null,
                            JsonArray array => $"[removed: {array.Count} item(s)]",
                            JsonObject map => $"[removed: {map.Count} item(s)]",
                            _ => "[removed]",
                        };
                    }
                    else
                    {
                        RemovePersonal(value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array) RemovePersonal(item);
                break;
        }
    }

    private static IEnumerable<DiagnosticsBundleItem> UninstallLogs(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        FileInfo[] logs;
        try
        {
            logs = new DirectoryInfo(directory).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxUninstallLogs)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var log in logs)
        {
            string text;
            try
            {
                // Windows Installer writes UTF-16 with a byte order mark; the reader detects it.
                using var reader = new StreamReader(log.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (text.Length > MaxUninstallLogChars)
                text = "[earlier lines omitted]" + Environment.NewLine + text[^MaxUninstallLogChars..];
            // The file name carries the program's name and is not needed to read the log.
            yield return new DiagnosticsBundleItem(
                $"uninstall-logs/uninstall-{log.LastWriteTimeUtc:yyyyMMdd-HHmmss}.log",
                "A Windows Installer log from Uninstaller Pro, redacted.",
                LogRedactor.Redact(text));
        }
    }

    private static string RedactLines(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = LogRedactor.Redact(lines[i]);
        return string.Join('\n', lines);
    }

    /// <summary>Versions and hardware models only: nothing that names the user or the computer.</summary>
    internal static string SystemSummary(DiagnosticsRetention retention)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(DiagnosticsBundleBuilder).Assembly.GetName().Version?.ToString() ?? "unknown";
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"AxePower: {version.Split('+')[0]}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine(CultureInfo.InvariantCulture, $".NET: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Processor: {ReadMachineString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")} ({Environment.ProcessorCount} logical)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Graphics: {ReadMachineString(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", "DriverDesc")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024 * 1024.0):0.#} GB");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Running elevated: {Environment.IsPrivilegedProcess}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Logs kept: {retention.MaxAge.TotalDays:0} days or {retention.MaxTotalBytes / (1024 * 1024)} MB");
        return builder.ToString();
    }

    private static string ReadMachineString(string subKey, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return (key?.GetValue(name) as string)?.Trim() is { Length: > 0 } value ? value : "unknown";
        }
        catch (Exception ex) when (ExpectedFailures.Registry(ex))
        {
            return "unknown";
        }
    }
}
