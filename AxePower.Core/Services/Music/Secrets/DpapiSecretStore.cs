using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Credentials encrypted with Windows DPAPI under the current user, in
/// <c>%LOCALAPPDATA%\AxePower\Music\secrets.dat</c>. Copying the file to another PC or another
/// account leaves it unreadable, which is the point: tokens never travel and never reach
/// settings.json, the logs or a support bundle.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AxePower.Music.Secrets.v1");

    private readonly string _file;
    private readonly LogService _log;
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    public DpapiSecretStore(MusicStoragePaths paths, LogService log)
    {
        _file = Path.Combine(paths.Root, "secrets.dat");
        _log = log;
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var values = Load();
            return values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            var values = Load();

            if (value is null)
            {
                if (!values.Remove(key)) return;
            }
            else
            {
                if (values.TryGetValue(key, out var existing) && existing == value) return;
                values[key] = value;
            }

            Save(values);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(_file))
            {
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
                _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>();
                return _cache;
            }
        }
        catch (Exception ex)
        {
            // A file written by another user or a corrupted one is not recoverable. Starting
            // empty means the user signs in again; it never blocks the app from running.
            _log.LogWarning($"[Music] Stored credentials could not be read ({ex.GetType().Name}); starting empty.");
        }

        _cache = new Dictionary<string, string>();
        return _cache;
    }

    private void Save(Dictionary<string, string> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(values);
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            string temp = _file + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            if (File.Exists(_file)) File.Replace(temp, _file, null);
            else File.Move(temp, _file);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Credentials could not be saved: {ex.Message}");
        }
    }
}
