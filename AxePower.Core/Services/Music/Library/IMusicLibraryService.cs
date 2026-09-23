using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The local music library. It indexes files and never deletes, moves or renames them:
/// removing a folder or hiding a track only affects the database.
/// </summary>
public interface IMusicLibraryService
{
    event EventHandler<LibraryScanProgress>? ScanProgress;
    event EventHandler? LibraryChanged;

    bool IsScanning { get; }

    Task InitializeAsync(CancellationToken ct);
    Task<LibraryScanResult> ScanAsync(bool full, CancellationToken ct);
    Task AddFolderAsync(string path, CancellationToken ct);

    /// <summary>Removes the folder's rows from the library. Files on disk are never touched.</summary>
    Task RemoveFolderAsync(string path, CancellationToken ct);

    Task<IReadOnlyList<Track>> GetTracksAsync(TrackQuery query, CancellationToken ct);
    Task<IReadOnlyList<AlbumSummary>> GetAlbumsAsync(AlbumQuery query, CancellationToken ct);
    Task<IReadOnlyList<ArtistSummary>> GetArtistsAsync(CancellationToken ct);
    Task<IReadOnlyList<GenreSummary>> GetGenresAsync(CancellationToken ct);
    Task<IReadOnlyList<Track>> SearchAsync(string text, int limit, CancellationToken ct);
    Task<Track?> GetTrackAsync(long id, CancellationToken ct);
    Task<Track?> GetTrackByPathAsync(string path, CancellationToken ct);

    /// <summary>Reads a file that is not in the library, without storing it.</summary>
    Task<Track> ReadLooseFileAsync(string path, CancellationToken ct);

    Task SetLikedAsync(long trackId, bool liked, CancellationToken ct);
    Task HideTrackAsync(long trackId, CancellationToken ct);
    Task RecordPlayAsync(long trackId, DateTime utc, CancellationToken ct);
}
