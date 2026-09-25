namespace AxePower.Core.Services.Music;

/// <summary>
/// Encrypted storage for credentials (Last.fm session and LAN portal token).
/// Values never go into settings.json and are never logged. Setting null removes the key.
/// </summary>
public interface ISecretStore
{
    void Set(string key, string? value);
    string? Get(string key);
}
