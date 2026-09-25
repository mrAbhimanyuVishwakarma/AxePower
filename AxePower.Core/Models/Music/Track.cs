using System;
using System.Collections.Generic;

namespace AxePower.Core.Models.Music;

/// <summary>The audio stream's real format, as reported by the decoder or the tags.</summary>
public sealed record AudioFormatInfo(
    string Codec,
    int SampleRate,
    int BitsPerSample,
    int Channels,
    int BitrateKbps,
    bool IsLossless);

/// <summary>ReplayGain values read from tags. Null means the tag is absent.</summary>
public sealed record ReplayGainInfo(
    double? TrackGainDb,
    double? TrackPeak,
    double? AlbumGainDb,
    double? AlbumPeak);

/// <summary>An immutable track description shared by the library, queue and every surface.</summary>
public sealed record Track(
    long Id,                                // 0 = opened directly, not in the library
    string Path,
    string Title,                           // falls back to the file name without extension
    IReadOnlyList<string> Artists,
    string? Album,
    string? AlbumArtist,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    IReadOnlyList<string> Genres,
    TimeSpan Duration,
    AudioFormatInfo Format,
    ReplayGainInfo? ReplayGain,
    string? CoverKey,
    bool Liked)
{
    /// <summary>
    /// Where the track came from. Null means a local file, and <see cref="Path"/> is its
    /// location on disk. Anything else is a provider track whose audio has to be resolved.
    /// </summary>
    public ProviderRef? Source { get; init; }

    /// <summary>True when the track plays straight from <see cref="Path"/>.</summary>
    public bool IsLocal => Source is null;

    /// <summary>Audio sources found for a provider track, best first. Null until searched.</summary>
    public IReadOnlyList<StreamCandidate>? Candidates { get; init; }

    /// <summary>The candidate currently chosen, whether automatically or by the listener.</summary>
    public string? SelectedCandidateId { get; init; }

    /// <summary>The playable location for the selected candidate. Re-resolved when it expires.</summary>
    public AudioStream? ResolvedStream { get; init; }

    /// <summary>Remote artwork, used before (or instead of) a local cover.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>
    /// A track that lives on a service rather than on disk. It carries no path and no format
    /// until a streaming provider resolves one, so the placeholder format is deliberately
    /// modest — the badge must never claim quality the engine has not seen.
    /// </summary>
    public static Track ForProvider(
        ProviderRef source,
        string title,
        IReadOnlyList<string> artists,
        TimeSpan duration,
        string? album = null,
        string? thumbnailUrl = null) =>
        new(
            Id: 0,
            Path: string.Empty,
            Title: title,
            Artists: artists,
            Album: album,
            AlbumArtist: null,
            TrackNumber: null,
            DiscNumber: null,
            Year: null,
            Genres: Array.Empty<string>(),
            Duration: duration,
            Format: new AudioFormatInfo("Stream", 0, 0, 2, 0, false),
            ReplayGain: null,
            CoverKey: null,
            Liked: false)
        {
            Source = source,
            ThumbnailUrl = thumbnailUrl
        };

    /// <summary>What to show when a track has no readable file name, e.g. in a log line.</summary>
    public string DisplayName => IsLocal && !string.IsNullOrEmpty(Path)
        ? System.IO.Path.GetFileName(Path)
        : $"{Title}{(Artists.Count > 0 ? " — " + Artists[0] : string.Empty)}";
}

/// <summary>
/// Raw tag values as read from a file, before artist/genre splitting and library lookups.
/// Artists and genres keep the file's own text; splitting is the library's job.
/// </summary>
public sealed record TrackTags(
    string? Title,
    string? Artists,
    string? AlbumArtist,
    string? Album,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    string? Genres,
    TimeSpan Duration,
    AudioFormatInfo Format,
    ReplayGainInfo? ReplayGain,
    bool HasEmbeddedCover,
    bool HasEmbeddedLyrics);

/// <summary>A file that exists but whose tags could not be read.</summary>
public sealed class TagReadException : Exception
{
    public TagReadException(string message, Exception? inner = null) : base(message, inner) { }
}
