using System;

namespace AxePower.Core.Models.Music;

public sealed record AlbumSummary(
    long Id,
    string Title,
    string AlbumArtist,
    int? Year,
    int TrackCount,
    TimeSpan Duration,
    string? CoverKey);

public sealed record ArtistSummary(
    long Id,
    string Name,
    int AlbumCount,
    int TrackCount);

public sealed record GenreSummary(
    long Id,
    string Name,
    int TrackCount);

/// <summary>Filter and sort for a track list. Null filters are ignored.</summary>
public sealed record TrackQuery(
    TrackSortField Sort = TrackSortField.Title,
    bool Descending = false,
    long? AlbumId = null,
    long? ArtistId = null,
    long? GenreId = null,
    string? Folder = null,
    bool LikedOnly = false,
    int Offset = 0,
    int Limit = int.MaxValue);

public sealed record AlbumQuery(
    AlbumSortField Sort = AlbumSortField.Title,
    bool Descending = false,
    long? ArtistId = null,
    long? GenreId = null);

/// <summary>Live scan counters, reported at most 4 times a second.</summary>
public sealed record LibraryScanProgress(
    int Found,
    int Processed,
    int Added,
    int Updated,
    int Removed,
    string? CurrentFolder);

public sealed record LibraryScanResult(
    int Added,
    int Updated,
    int Removed,
    int Unchanged,
    int Failed,
    TimeSpan Elapsed,
    bool Cancelled);
