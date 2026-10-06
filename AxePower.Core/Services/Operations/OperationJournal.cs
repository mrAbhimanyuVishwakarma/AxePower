using System.Globalization;
using System.Text;
using System.Text.Json;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services.Operations;

/// <summary>
/// The history of changes AxePower made to Windows, one JSON file per operation in
/// %LOCALAPPDATA%\AxePower\Operations. Each file holds the snapshots rollback needs, so the newest
/// <see cref="MaxRecords"/> are kept; older ones (and their rollback data) are removed. Writes are
/// atomic (temp file, then move), so a crash never leaves half a record.
/// </summary>
public sealed class OperationJournal
{
    public const int MaxRecords = 300;
    private const string Prefix = "op-";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly LogService? _log;

    public OperationJournal(string directory, LogService? log = null)
    {
        Directory = directory;
        _log = log;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Operations");

    public string Directory { get; }

    public void Save(OperationRecord record)
    {
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathFor(record);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            Prune();
        }
    }

    /// <summary>All recorded operations, newest first. Unreadable files are skipped and logged.</summary>
    public IReadOnlyList<OperationRecord> All()
    {
        lock (_gate)
        {
            if (!System.IO.Directory.Exists(Directory)) return Array.Empty<OperationRecord>();
            var records = new List<OperationRecord>();
            foreach (var file in System.IO.Directory.GetFiles(Directory, Prefix + "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<OperationRecord>(File.ReadAllText(file), Json);
                    if (record is null || !JsonNullRepair.Repair(record).IsComplete) continue;
                    records.Add(record);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    _log?.LogWarning($"[Operations] Skipped an unreadable history record ({ex.GetType().Name}).");
                }
            }
            return records.OrderByDescending(r => r.CreatedUtc).ToList();
        }
    }

    public OperationRecord? Find(string id) => All().FirstOrDefault(r => r.Id == id);

    /// <summary>The newest completed operation about <paramref name="subject"/> that has not been rolled back.</summary>
    public OperationRecord? LatestActive(string subject) => All().FirstOrDefault(r =>
        r.Subject == subject && !r.IsRollback && !r.WasRolledBack
        && r.Status is OperationStatus.Succeeded or OperationStatus.PartiallySucceeded);

    private string PathFor(OperationRecord record) =>
        Path.Combine(Directory, $"{Prefix}{record.CreatedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{record.Id}.json");

    private void Prune()
    {
        var files = System.IO.Directory.GetFiles(Directory, Prefix + "*.json")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Skip(MaxRecords);
        foreach (var file in files)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort: tried again on the next save. */ }
        }
    }
}
