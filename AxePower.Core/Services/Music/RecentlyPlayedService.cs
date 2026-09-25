using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The "Last played" lists on the Music page, one for local files and one for streamed songs.
/// A song counts once it actually starts playing, so a session restored paused adds nothing.
/// Kept in recently-played.json beside playback-state.json, written the same atomic way; a
/// missing or corrupt file just means an empty history.
/// </summary>
public sealed class RecentlyPlayedService : IPlayerIntegration
{
    /// <summary>How many songs each list keeps.</summary>
    public const int Capacity = 20;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly MusicStoragePaths _paths;
    private readonly IClock _clock;
    private readonly LogService? _log;
    private readonly object _lock = new();
    private List<RecentPlay>? _plays;
    private Guid? _lastRecordedKey;

    public RecentlyPlayedService(MusicStoragePaths paths, IClock clock, LogService? log = null)
    {
        _paths = paths;
        _clock = clock;
        _log = log;
    }

    public string Name => "Recently played";

    /// <summary>Raised on the player's thread after a list changes.</summary>
    public event EventHandler? Changed;

    public Task InitializeAsync(IntPtr mainWindowHandle, CancellationToken ct) => Task.CompletedTask;

    public void OnStateChanged(PlayerState state)
    {
        var current = state.Current;
        if (current is null || !state.Playing || current.Key == _lastRecordedKey) return;

        _lastRecordedKey = current.Key;
        Record(current.Track);
    }

    /// <summary>Newest first. Local files, or everything streamed.</summary>
    public IReadOnlyList<RecentPlay> Get(bool streamed)
    {
        lock (_lock)
            return Plays().Where(p => p.IsLocal != streamed).ToList();
    }

    public void Record(Track track)
    {
        if (track.IsLocal && string.IsNullOrEmpty(track.Path)) return;

        var play = RecentPlay.From(track, _clock.UtcNow);
        lock (_lock)
        {
            var plays = Plays();
            plays.RemoveAll(p => p.Key == play.Key);
            plays.Insert(0, play);

            // Each list is capped on its own, so a run of streamed songs never pushes out local ones
            var kept = plays.Where(p => p.IsLocal).Take(Capacity)
                .Concat(plays.Where(p => !p.IsLocal).Take(Capacity))
                .OrderByDescending(p => p.PlayedUtc)
                .ToList();
            _plays = kept;
            Save(kept);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<RecentPlay> Plays() => _plays ??= Load();

    private List<RecentPlay> Load()
    {
        try
        {
            if (!File.Exists(_paths.RecentlyPlayedFile)) return new();
            return JsonSerializer.Deserialize<List<RecentPlay>>(File.ReadAllText(_paths.RecentlyPlayedFile), Options)?
                .Where(p => !string.IsNullOrWhiteSpace(p.Title))
                .OrderByDescending(p => p.PlayedUtc)
                .ToList() ?? new();
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Music] Recently played list was unreadable and was ignored: {ex.Message}");
            return new();
        }
    }

    private void Save(List<RecentPlay> plays)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            string target = _paths.RecentlyPlayedFile;
            string temp = target + ".temp";

            File.WriteAllText(temp, JsonSerializer.Serialize(plays, Options));

            if (File.Exists(target)) File.Replace(temp, target, null, ignoreMetadataErrors: true);
            else File.Move(temp, target);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Music] Could not save the recently played list: {ex.Message}");
        }
    }

    public void Dispose()
    {
    }
}
