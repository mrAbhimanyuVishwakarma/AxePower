using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Searches YouTube and plays its audio, with no browser and no sign-in. It is both the
/// metadata source (what is there) and the streaming source (where the sound comes from), so
/// it also serves as the audio half for catalogues that cannot supply audio themselves.
/// </summary>
public sealed class YouTubeProvider : IStreamingProvider, IMetadataProvider, IDiscoveryProvider
{
    /// <summary>Only so many search hits are worth keeping as fallbacks for one track.</summary>
    private const int MaxCandidates = 5;

    private const string TopicSuffix = " - Topic";

    private readonly YoutubeClient _youtube = new();
    private readonly StreamCache _cache;
    private readonly SettingsService _settings;

    public YouTubeProvider(StreamCache cache, SettingsService settings)
    {
        _cache = cache;
        _settings = settings;
    }

    private Models.Music.ProviderSettings Options =>
        _settings.GetSettings().Music.Providers ?? new Models.Music.ProviderSettings();

    public ProviderInfo Info { get; } = new(
        Id: "youtube",
        Name: "YouTube",
        Description: "Search and stream from YouTube. No account and no browser window.",
        Author: "AxePower",
        Kinds: new[] { ProviderKind.Streaming, ProviderKind.Metadata, ProviderKind.Discovery },
        Capabilities: new[] { "Search", "Streaming", "Artwork", "Recommendations" },
        RequiresSetup: false);

    public bool SupportsLocalFiles => false;

    public Task<ProviderState> GetStateAsync(CancellationToken ct) =>
        Task.FromResult(ProviderState.Ready);

    // ── Metadata ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Track>> SearchTracksAsync(string query, int limit, CancellationToken ct)
    {
        var tracks = new List<Track>();
        if (string.IsNullOrWhiteSpace(query)) return tracks;

        await foreach (var video in _youtube.Search.GetVideosAsync(query, ct))
        {
            if (tracks.Count >= limit) break;

            var (title, artist) = CleanUp(video.Title, video.Author.ChannelTitle);
            tracks.Add(Track.ForProvider(
                source: new ProviderRef(Info.Id, video.Id.Value, video.Url),
                title: title,
                artists: new[] { artist },
                duration: video.Duration ?? TimeSpan.Zero,
                album: null,
                thumbnailUrl: BestThumbnail(video.Thumbnails)));
        }

        return tracks;
    }

    /// <summary>YouTube is used without an account here, so there are no playlists to list.</summary>
    public Task<IReadOnlyList<ProviderPlaylist>> GetPlaylistsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderPlaylist>>(Array.Empty<ProviderPlaylist>());

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, int limit, CancellationToken ct)
    {
        var tracks = new List<Track>();
        await foreach (var video in _youtube.Playlists.GetVideosAsync(playlistId, ct))
        {
            if (tracks.Count >= limit) break;

            var (title, artist) = CleanUp(video.Title, video.Author.ChannelTitle);
            tracks.Add(Track.ForProvider(
                source: new ProviderRef(Info.Id, video.Id.Value, video.Url),
                title: title,
                artists: new[] { artist },
                duration: video.Duration ?? TimeSpan.Zero,
                thumbnailUrl: BestThumbnail(video.Thumbnails)));
        }
        return tracks;
    }

    // ── Discovery ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Track>> GetRecommendationsAsync(
        IReadOnlyList<Track> context, DiscoveryOptions options, CancellationToken ct)
    {
        if (context.Count == 0) return Array.Empty<Track>();

        // The artists that have actually been playing, most recent first, so a session that has
        // drifted is followed rather than anchored to whatever started it.
        var artists = context
            .Reverse()
            .SelectMany(t => t.Artists)
            .Where(a => !string.IsNullOrWhiteSpace(a) && !a.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (artists.Count == 0) return Array.Empty<Track>();

        // Anything already in the context is not a recommendation, and a search for one artist
        // returns their own catalogue over and over, so both are filtered out as results arrive.
        var seen = context
            .Select(t => Key(t.Title, t.Artists.FirstOrDefault()))
            .ToHashSet(StringComparer.Ordinal);
        var seenIds = context
            .Where(t => t.Source?.Provider == Info.Id)
            .Select(t => t.Source!.Id)
            .ToHashSet(StringComparer.Ordinal);

        var results = new List<Track>();
        foreach (string query in Queries(artists, options.Variety))
        {
            if (results.Count >= options.Limit) break;
            ct.ThrowIfCancellationRequested();

            foreach (var found in await SearchTracksAsync(query, options.Limit, ct))
            {
                if (results.Count >= options.Limit) break;
                if (found.Duration > TimeSpan.FromMinutes(12) || found.Duration < TimeSpan.FromSeconds(45)) continue;
                if (found.Source is null || !seenIds.Add(found.Source.Id)) continue;
                if (!seen.Add(Key(found.Title, found.Artists.FirstOrDefault()))) continue;

                results.Add(found);
            }
        }

        return results;
    }

    /// <summary>
    /// Searches to try, in order. Low variety asks for more of the same artists; high variety
    /// asks for the music around them, which is what the slider is for.
    /// </summary>
    private static IEnumerable<string> Queries(IReadOnlyList<string> artists, double variety)
    {
        double clamped = Math.Clamp(variety, 0, 1);

        if (clamped < 0.66)
        {
            foreach (string artist in artists) yield return artist + " songs";
        }

        if (clamped >= 0.33)
        {
            foreach (string artist in artists) yield return "artists similar to " + artist;
            foreach (string artist in artists) yield return artist + " radio mix";
        }
    }

    private static string Key(string title, string? artist) =>
        CandidateRanker.Normalize(title) + "|" + CandidateRanker.Normalize(artist);

    // ── Streaming ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<StreamCandidate>> FindCandidatesAsync(Track track, CancellationToken ct)
    {
        // A track that already names a YouTube video is its own first candidate: searching for
        // it again would only risk matching a different upload.
        if (track.Source?.Provider == Info.Id)
        {
            return new[]
            {
                new StreamCandidate(
                    Id: track.Source.Id,
                    Title: track.Title,
                    Source: track.Source,
                    Artist: track.Artists.FirstOrDefault(),
                    Duration: track.Duration,
                    ThumbnailUrl: track.ThumbnailUrl)
            };
        }

        string artist = track.Artists.FirstOrDefault() ?? string.Empty;
        string query = string.IsNullOrWhiteSpace(artist) ? track.Title : artist + " - " + track.Title;

        var candidates = new List<StreamCandidate>();
        await foreach (var video in _youtube.Search.GetVideosAsync(query, ct))
        {
            if (candidates.Count >= MaxCandidates) break;

            candidates.Add(new StreamCandidate(
                Id: video.Id.Value,
                Title: video.Title,
                Source: new ProviderRef(Info.Id, video.Id.Value, video.Url),
                Artist: video.Author.ChannelTitle,
                Duration: video.Duration,
                ThumbnailUrl: BestThumbnail(video.Thumbnails)));
        }

        return CandidateRanker.Rank(track, candidates);
    }

    public async Task<AudioStream?> ResolveAsync(StreamCandidate candidate, bool preferLocalCopy, CancellationToken ct)
    {
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(candidate.Id, ct);

        // Media Foundation decodes AAC in MP4 but not Opus in WebM, so an MP4 stream is worth
        // more here than a higher bitrate we could not open.
        var info = manifest.GetAudioOnlyStreams()
            .Where(s => s.Container == Container.Mp4)
            .OrderByDescending(s => s.Bitrate.BitsPerSecond)
            .FirstOrDefault();

        if (info is null) return null;

        var source = new ProviderRef(Info.Id, candidate.Id, "https://www.youtube.com/watch?v=" + candidate.Id);
        int bitrateKbps = (int)(info.Bitrate.BitsPerSecond / 1000);

        if (!preferLocalCopy && !Options.CacheStreamsToDisk)
        {
            return new AudioStream(
                Url: info.Url,
                Protocol: StreamProtocol.Https,
                Source: source,
                Codec: info.AudioCodec,
                Container: info.Container.Name,
                BitrateKbps: bitrateKbps);
        }

        string path = await DownloadAsync(info, candidate.Id, ct);
        return new AudioStream(
            Url: path,
            Protocol: StreamProtocol.File,
            Source: source,
            Codec: info.AudioCodec,
            Container: info.Container.Name,
            BitrateKbps: bitrateKbps);
    }

    /// <summary>Downloads a stream into the cache, reusing a complete earlier download.</summary>
    private async Task<string> DownloadAsync(IStreamInfo info, string videoId, CancellationToken ct)
    {
        string path = _cache.PathFor(Info.Id, videoId, ".m4a");
        if (_cache.TryGet(path, out _)) return path;

        // Download beside the target and move it into place, so an interrupted download can
        // never be mistaken for a complete one.
        string partial = path + ".part";
        try
        {
            await _youtube.Videos.Streams.DownloadAsync(info, partial, cancellationToken: ct);
            if (File.Exists(path)) File.Delete(path);
            File.Move(partial, path);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: a partial stream download is overwritten next time. */ }
            throw;
        }

        _cache.Prune(Options.StreamCacheMaxMb);
        return path;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? BestThumbnail(IReadOnlyList<Thumbnail> thumbnails) =>
        thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url;

    /// <summary>
    /// Turns "Artist - Title" on an auto-generated Topic channel into a real artist and title.
    /// Anything else is left exactly as uploaded rather than guessed at.
    /// </summary>
    internal static (string Title, string Artist) CleanUp(string rawTitle, string channel)
    {
        string title = (rawTitle ?? string.Empty).Trim();
        string artist = (channel ?? string.Empty).Trim();

        bool topic = artist.EndsWith(TopicSuffix, StringComparison.OrdinalIgnoreCase);
        if (topic) artist = artist[..^TopicSuffix.Length].Trim();

        int separator = title.IndexOf(" - ", StringComparison.Ordinal);
        if (topic && separator > 0)
        {
            artist = title[..separator].Trim();
            title = title[(separator + 3)..].Trim();
        }

        return (title, string.IsNullOrWhiteSpace(artist) ? "Unknown artist" : artist);
    }
}
