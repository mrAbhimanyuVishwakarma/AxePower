using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AxePower.Core.Services.Music;

/// <summary>
/// A bounded folder of audio downloaded from streaming sources. Nothing here is ever needed
/// again — it exists so a track that will not stream progressively can still be played, and so
/// replaying a track does not download it twice. When the folder grows past its budget the
/// least recently used files go first.
/// </summary>
public sealed class StreamCache
{
    private readonly MusicStoragePaths _paths;
    private readonly LogService _log;
    private readonly object _pruneLock = new();

    public StreamCache(MusicStoragePaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public string Directory => _paths.StreamCacheDirectory;

    /// <summary>A stable file name for a provider's item, with no user text in the path.</summary>
    public string PathFor(string providerId, string itemId, string extension)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string safeExt = extension.StartsWith('.') ? extension : "." + extension;
        return Path.Combine(Directory, Hash($"{providerId}|{itemId}") + safeExt);
    }

    /// <summary>A cached file that is present and big enough to be a real download.</summary>
    public bool TryGet(string path, out FileInfo file)
    {
        file = new FileInfo(path);
        if (!file.Exists || file.Length < 16 * 1024) return false;

        // Touch it so pruning treats a replayed track as recently used.
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: pruning may then treat it as older than it is. */ }
        return true;
    }

    /// <summary>Drops the oldest files until the folder fits inside <paramref name="maxMb"/>.</summary>
    public void Prune(int maxMb)
    {
        if (maxMb <= 0) return;

        lock (_pruneLock)
        {
            try
            {
                var dir = new DirectoryInfo(Directory);
                if (!dir.Exists) return;

                var files = dir.GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToList();
                long budget = (long)maxMb * 1024 * 1024;
                long total = files.Sum(f => f.Length);

                foreach (var file in files)
                {
                    if (total <= budget) break;
                    long size = file.Length;
                    try
                    {
                        file.Delete();
                        total -= size;
                    }
                    catch { /* in use by the player; it will be considered again next time */ }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Music] Stream cache could not be pruned: {ex.Message}");
            }
        }
    }

    /// <summary>Removes every cached file. Used by "Clear cache" and by the uninstaller.</summary>
    public long Clear()
    {
        lock (_pruneLock)
        {
            long freed = 0;
            try
            {
                var dir = new DirectoryInfo(Directory);
                if (!dir.Exists) return 0;

                foreach (var file in dir.GetFiles())
                {
                    long size = file.Length;
                    try { file.Delete(); freed += size; } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* In use (playing now): left for the next prune. */ }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Music] Stream cache could not be cleared: {ex.Message}");
            }
            return freed;
        }
    }

    public long SizeBytes()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            return dir.Exists ? dir.GetFiles().Sum(f => f.Length) : 0;
        }
        catch { return 0; }
    }

    private static string Hash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
