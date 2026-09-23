using System;
using System.IO;
using System.Text.Json;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Where AxePower Music keeps its files. Tests pass a temporary folder instead.</summary>
public sealed class MusicStoragePaths
{
    public MusicStoragePaths() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxePower", "Music"))
    {
    }

    public MusicStoragePaths(string root)
    {
        Root = root;
    }

    public string Root { get; }
    public string PlaybackStateFile => Path.Combine(Root, "playback-state.json");
    public string CoversDirectory => Path.Combine(Root, "covers");
}

/// <summary>
/// Reads and writes playback-state.json with the same atomic pattern as settings.json:
/// write a temp file, then <see cref="File.Replace(string, string, string?)"/>.
/// A missing or corrupt file loads as "nothing saved".
/// </summary>
public sealed class PlaybackStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly MusicStoragePaths _paths;
    private readonly LogService? _log;
    private readonly object _writeLock = new();

    public PlaybackStateStore(MusicStoragePaths paths, LogService? log = null)
    {
        _paths = paths;
        _log = log;
    }

    public PersistedPlaybackState? Load()
    {
        try
        {
            if (!File.Exists(_paths.PlaybackStateFile)) return null;

            var state = JsonSerializer.Deserialize<PersistedPlaybackState>(File.ReadAllText(_paths.PlaybackStateFile), Options);
            if (state == null || state.Version != PersistedPlaybackState.CurrentVersion) return null;

            state.Queue ??= new();
            return state;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Music] Saved playback state was unreadable and was ignored: {ex.Message}");
            return null;
        }
    }

    public void Save(PersistedPlaybackState state)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(_paths.Root);
                string target = _paths.PlaybackStateFile;
                string temp = target + ".temp";
                string backup = target + ".backup";

                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));

                if (File.Exists(target)) File.Replace(temp, target, backup, ignoreMetadataErrors: true);
                else File.Move(temp, target);
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"[Music] Could not save playback state: {ex.Message}");
            }
        }
    }
}
