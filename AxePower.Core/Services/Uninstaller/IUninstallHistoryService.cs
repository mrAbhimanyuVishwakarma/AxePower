using AxePower.Core.Models.Uninstaller;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Persists and retrieves uninstall session history.</summary>
public interface IUninstallHistoryService
{
    Task<IReadOnlyList<UninstallSession>> GetSessionsAsync();
    Task SaveSessionAsync(UninstallSession session);
    Task DeleteSessionAsync(string sessionId);
    Task ClearAllSessionsAsync();
}
