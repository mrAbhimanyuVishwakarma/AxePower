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
public sealed class YouTubeProvider : IStreamingProvider, IMetadataProvider
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
        Kinds: new[] { ProviderKind.Streaming, ProviderKind.Metadata },
        Capabilities: new[] { "Search", "Streaming", "Artwork" },
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

        // A hit whose length is close to the known duration is almost always the right take,
        // so it goes first. With no known duration the search order stands.
        if (track.Duration > TimeSpan.Zero)
        {
            candidates = candidates
                .OrderBy(c => c.Duration is null
                    ? TimeSpan.MaxValue
                    : (c.Duration.Value - track.Duration).Duration())
                .ToList();
        }

        return candidates;
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
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
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
