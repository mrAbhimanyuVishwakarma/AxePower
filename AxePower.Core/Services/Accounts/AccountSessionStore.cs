using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxePower.Core.Services.Accounts;

/// <summary>What survives a restart: the session tokens plus the last profile seen, so the sidebar shows your name offline.</summary>
public sealed class StoredAccountSession
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = string.Empty;
    [JsonPropertyName("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
    [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("profile")] public AccountProfile? Profile { get; set; }
}

/// <summary>
/// The signed-in session, encrypted with Windows DPAPI under the current user in
/// <c>%LOCALAPPDATA%\AxePower\Account\session.dat</c>. Never settings.json and never the log: the
/// refresh token is a password in all but name, and copying this file to another PC or Windows
/// account leaves it unreadable.
/// </summary>
public sealed class AccountSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AxePower.Account.Session.v1");

    private readonly LogService _log;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private readonly object _gate = new();

    public AccountSessionStore(LogService log)
        : this(log, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Account"))
    {
    }

    /// <summary>Tests pass a temp directory, and may swap DPAPI for a reversible stand-in.</summary>
    public AccountSessionStore(
        LogService log, string directory, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _log = log;
        FilePath = Path.Combine(directory, "session.dat");
        _protect = protect ?? (bytes => ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        _unprotect = unprotect ?? (bytes => ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    public string FilePath { get; }

    /// <summary>Reads the stored session, or null. Read-only: never creates the folder or the file.</summary>
    public StoredAccountSession? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var session = JsonSerializer.Deserialize<StoredAccountSession>(_unprotect(File.ReadAllBytes(FilePath)));
                return session is { RefreshToken.Length: > 0, UserId.Length: > 0 } ? session : null;
            }
            catch (Exception ex)
            {
                // Another user's file or a corrupted one cannot be recovered; signing in again fixes it.
                _log.LogWarning($"[Account] Stored session could not be read ({ex.GetType().Name}); treating as signed out.");
                return null;
            }
        }
    }

    public void Save(StoredAccountSession session)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var encrypted = _protect(JsonSerializer.SerializeToUtf8Bytes(session));

                var temp = FilePath + ".tmp";
                File.WriteAllBytes(temp, encrypted);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                // Staying signed in for this run still works; the user signs in again next start.
                _log.LogWarning($"[Account] Session could not be saved: {ex.Message}");
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Account] Stored session could not be deleted: {ex.Message}");
            }
        }
    }
}
