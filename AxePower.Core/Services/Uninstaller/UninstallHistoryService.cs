using System.Text.Json;
using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Persists and retrieves uninstall session history as JSON.</summary>
public sealed class UninstallHistoryService : IUninstallHistoryService
{
    private static readonly string HistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AxePower", "UninstallerHistory", "sessions.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<UninstallSession>> GetSessionsAsync()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return [];
            var json = await File.ReadAllTextAsync(HistoryFile);
            return JsonSerializer.Deserialize<List<UninstallSession>>(json, JsonOptions)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveSessionAsync(UninstallSession session)
    {
        try
        {
            var sessions = new List<UninstallSession>(await GetSessionsAsync()) { session };
            // Keep last 50 sessions
            if (sessions.Count > 50)
                sessions = sessions.OrderByDescending(s => s.StartTime).Take(50).ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            var json = JsonSerializer.Serialize(sessions, JsonOptions);
            await File.WriteAllTextAsync(HistoryFile, json);
        }
        catch { }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        try
        {
            var sessions = new List<UninstallSession>(await GetSessionsAsync());
            sessions.RemoveAll(s => s.SessionId == sessionId);
            var json = JsonSerializer.Serialize(sessions, JsonOptions);
            await File.WriteAllTextAsync(HistoryFile, json);
        }
        catch { }
    }

    public async Task ClearAllSessionsAsync()
    {
        try
        {
            if (File.Exists(HistoryFile))
                await File.WriteAllTextAsync(HistoryFile, "[]");
        }
        catch { }
    }
}
