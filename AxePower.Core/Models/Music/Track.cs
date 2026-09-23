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
    bool Liked);

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
