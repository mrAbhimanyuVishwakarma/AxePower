using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>One remembered YouTube Music search and the songs it found.</summary>
public sealed class RecentSearch
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("searchedUtc")]
    public DateTime SearchedUtc { get; set; }

    /// <summary>The results in the order the source returned them. Rebuilt with <see cref="RecentPlay.ToTrack"/>.</summary>
    [JsonPropertyName("results")]
    public List<RecentPlay> Results { get; set; } = new();
}

/// <summary>
/// The last searches made in YouTube Music, each with its song list, so the Music page can show
/// them again after a restart or a trip to Local Music without searching again. Kept in
/// recent-searches.json beside playback-state.json; a missing or corrupt file is no history.
/// </summary>
public sealed class RecentSearchesService
{
    /// <summary>How many searches are remembered.</summary>
    public const int Capacity = 7;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly MusicStoragePaths _paths;
    private readonly IClock _clock;
    private readonly LogService? _log;
    private readonly object _lock = new();
    private List<RecentSearch>? _searches;

    public RecentSearchesService(MusicStoragePaths paths, IClock clock, LogService? log = null)
    {
        _paths = paths;
        _clock = clock;
        _log = log;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentSearch> Get()
    {
        lock (_lock)
            return Searches().ToList();
    }

    /// <summary>The saved search for a query, matched the way <see cref="Add"/> de-duplicates.</summary>
    public RecentSearch? Find(string query)
    {
        string key = Normalize(query);
        lock (_lock)
            return Searches().FirstOrDefault(s => Normalize(s.Query) == key);
    }

    /// <summary>Remembers a search and its results. Searching the same text again moves it to the top.</summary>
    public void Add(string query, IReadOnlyList<Track> results)
    {
        query = query.Trim();
        if (query.Length == 0) return;

        var now = _clock.UtcNow;
        var search = new RecentSearch
        {
            Query = query,
            SearchedUtc = now,
            Results = results.Select(t => RecentPlay.From(t, now)).ToList()
        };

        lock (_lock)
        {
            var searches = Searches();
            searches.RemoveAll(s => Normalize(s.Query) == Normalize(query));
            searches.Insert(0, search);
            if (searches.Count > Capacity) searches.RemoveRange(Capacity, searches.Count - Capacity);
            Save(searches);
        }
    }

    public void Remove(string query)
    {
        string key = Normalize(query);
        lock (_lock)
        {
            var searches = Searches();
            if (searches.RemoveAll(s => Normalize(s.Query) == key) > 0) Save(searches);
        }
    }

    private static string Normalize(string query) => query.Trim().ToUpperInvariant();

    private List<RecentSearch> Searches() => _searches ??= Load();

    private List<RecentSearch> Load()
    {
        try
        {
            if (!File.Exists(_paths.RecentSearchesFile)) return new();
            return JsonSerializer.Deserialize<List<RecentSearch>>(File.ReadAllText(_paths.RecentSearchesFile), Options)?
                .Where(s => !string.IsNullOrWhiteSpace(s.Query))
                .Select(s => { s.Results ??= new(); return s; })
                .OrderByDescending(s => s.SearchedUtc)
                .Take(Capacity)
                .ToList() ?? new();
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Music] Recent searches were unreadable and were ignored: {ex.Message}");
            return new();
        }
    }

    private void Save(List<RecentSearch> searches)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            string target = _paths.RecentSearchesFile;
            string temp = target + ".temp";

            File.WriteAllText(temp, JsonSerializer.Serialize(searches, Options));

            if (File.Exists(target)) File.Replace(temp, target, null, ignoreMetadataErrors: true);
            else File.Move(temp, target);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Music] Could not save recent searches: {ex.Message}");
        }
    }
}
