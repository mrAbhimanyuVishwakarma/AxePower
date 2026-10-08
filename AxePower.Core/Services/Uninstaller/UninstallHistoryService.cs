using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// Persists uninstall sessions to %LOCALAPPDATA%\AxePower\UninstallerHistory\sessions.json
/// (moved once from %APPDATA%). Writes are atomic, a session is saved again under the same ID
/// when its cleanup finishes, and history written before outcomes existed still loads.
/// </summary>
public sealed class UninstallHistoryService : IUninstallHistoryService
{
    private const int MaxSessions = 50;

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "UninstallerHistory");

    private static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AxePower", "UninstallerHistory");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly LogService? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _migrated;

    public UninstallHistoryService() : this(DefaultRoot) { }

    public UninstallHistoryService(LogService log) : this(DefaultRoot, log) { }

    public UninstallHistoryService(string root, LogService? log = null)
    {
        _root = root;
        _log = log;
    }

    private string HistoryFile => Path.Combine(_root, "sessions.json");

    public async Task<IReadOnlyList<UninstallSession>> GetSessionsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning("[Uninstaller] The uninstall history could not be read", ex);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSessionAsync(UninstallSession session)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sessions = await LoadAsync().ConfigureAwait(false);
            sessions.RemoveAll(s => s.SessionId == session.SessionId);
            sessions.Add(session);
            await WriteAsync(sessions).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the uninstall itself already happened. Nothing was written, so the
            // existing history is intact (LoadAsync throws rather than returning an empty list).
            _log?.LogWarning("[Uninstaller] This uninstall could not be added to the history", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sessions = await LoadAsync().ConfigureAwait(false);
            sessions.RemoveAll(s => s.SessionId == sessionId);
            await WriteAsync(sessions).ConfigureAwait(false);
        }
        // Not caught: a delete the user asked for must report failure (the page shows it).
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAllSessionsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await WriteAsync([]).ConfigureAwait(false);
        }
        // Not caught: a clear the user asked for must report failure.
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<UninstallSession>> LoadAsync()
    {
        MigrateOnce();
        if (!File.Exists(HistoryFile)) return [];
        // Read errors propagate: returning an empty list here would let the next save overwrite
        // a history that was only temporarily unreadable.
        var json = await File.ReadAllTextAsync(HistoryFile).ConfigureAwait(false);
        try
        {
            return Parse(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Damaged: keep it beside the new history instead of overwriting it.
            var kept = HistoryFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(HistoryFile, kept); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { kept = "(left in place)"; }
            _log?.LogWarning($"[Uninstaller] The uninstall history was damaged and was kept as {kept}; a new history is started", ex);
            return [];
        }
    }

    private async Task WriteAsync(List<UninstallSession> sessions)
    {
        var kept = sessions.OrderByDescending(s => s.StartTime).Take(MaxSessions).OrderBy(s => s.StartTime).ToList();
        Directory.CreateDirectory(_root);
        var temp = HistoryFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(kept, JsonOptions)).ConfigureAwait(false);
        File.Move(temp, HistoryFile, overwrite: true);
    }

    /// <summary>
    /// Reads sessions, giving results saved before <see cref="UninstallResult.Outcome"/> existed an
    /// outcome from their old Success / WasSkipped / WasDryRun flags.
    /// </summary>
    internal static List<UninstallSession> Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonArray;
        if (root is null) return [];

        foreach (var session in root.OfType<JsonObject>())
        {
            if (session["Results"] is not JsonArray results) continue;
            foreach (var result in results.OfType<JsonObject>())
            {
                if (result.ContainsKey("Outcome")) continue;
                var outcome =
                    Flag(result, "WasDryRun") ? UninstallOutcome.DryRun :
                    Flag(result, "WasSkipped") ? UninstallOutcome.Skipped :
                    Flag(result, "Success") ? UninstallOutcome.Removed :
                    UninstallOutcome.Failed;
                result["Outcome"] = outcome.ToString();
            }
        }

        return root.Deserialize<List<UninstallSession>>(JsonOptions) ?? [];

        static bool Flag(JsonObject o, string name) =>
            o.TryGetPropertyValue(name, out var v) && v is JsonValue value && value.TryGetValue<bool>(out var b) && b;
    }

    /// <summary>Moves history (sessions and backup folders) from %APPDATA% the first time it's needed.</summary>
    private void MigrateOnce()
    {
        if (_migrated) return;
        _migrated = true;
        if (!_root.Equals(DefaultRoot, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            if (!Directory.Exists(LegacyRoot)) return;
            Directory.CreateDirectory(_root);

            foreach (var file in Directory.EnumerateFiles(LegacyRoot))
            {
                var target = Path.Combine(_root, Path.GetFileName(file));
                if (!File.Exists(target)) File.Move(file, target);
            }
            foreach (var dir in Directory.EnumerateDirectories(LegacyRoot))
            {
                var target = Path.Combine(_root, Path.GetFileName(dir));
                if (!Directory.Exists(target)) Directory.Move(dir, target);
            }

            if (!Directory.EnumerateFileSystemEntries(LegacyRoot).Any())
                Directory.Delete(LegacyRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old history stays where it was; nothing is lost
            _log?.LogWarning("[Uninstaller] Old uninstall history could not be moved to the new folder", ex);
        }
    }
}
