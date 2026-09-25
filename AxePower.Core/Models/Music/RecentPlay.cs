using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Music;

/// <summary>
/// One row of recently-played.json. Carries enough to play the song again: a local path, or the
/// provider reference a streamed track is resolved from.
/// </summary>
public sealed class RecentPlay
{
    /// <summary>Null for a local file; otherwise the provider id, e.g. "youtube".</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>The provider's own id for the track. Empty for a local file.</summary>
    [JsonPropertyName("sourceId")]
    public string SourceId { get; set; } = string.Empty;

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artists")]
    public List<string> Artists { get; set; } = new();

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    [JsonPropertyName("playedUtc")]
    public DateTime PlayedUtc { get; set; }

    [JsonIgnore]
    public bool IsLocal => Provider is null;

    /// <summary>What makes two rows the same song: the file, or the provider and its id.</summary>
    [JsonIgnore]
    public string Key => IsLocal
        ? "local:" + Path.ToUpperInvariant()
        : Provider + ":" + SourceId;

    public static RecentPlay From(Track track, DateTime playedUtc) => new()
    {
        Provider = track.Source?.Provider,
        SourceId = track.Source?.Id ?? string.Empty,
        SourceUrl = track.Source?.Url,
        Path = track.IsLocal ? track.Path : string.Empty,
        Title = track.Title,
        Artists = new List<string>(track.Artists),
        Album = track.Album,
        DurationMs = (long)track.Duration.TotalMilliseconds,
        ThumbnailUrl = track.ThumbnailUrl,
        PlayedUtc = playedUtc
    };

    /// <summary>Rebuilds a playable track. A streamed one is resolved again when it plays.</summary>
    public Track ToTrack()
    {
        var duration = TimeSpan.FromMilliseconds(DurationMs);
        if (!IsLocal)
            return Track.ForProvider(new ProviderRef(Provider!, SourceId, SourceUrl), Title, Artists, duration, Album, ThumbnailUrl);

        return new Track(
            Id: 0,
            Path: Path,
            Title: Title,
            Artists: Artists,
            Album: Album,
            AlbumArtist: null,
            TrackNumber: null,
            DiscNumber: null,
            Year: null,
            Genres: Array.Empty<string>(),
            Duration: duration,
            Format: new AudioFormatInfo(System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(), 0, 0, 2, 0, false),
            ReplayGain: null,
            CoverKey: null,
            Liked: false);
    }
}
