using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using Microsoft.Data.Sqlite;

namespace AxePower.Core.Services.Music;

/// <summary>What happened to a track during one play, recorded when it ends.</summary>
public sealed record PlayRecord(
    long TrackId,
    DateTime StartedUtc,
    DateTime EndedUtc,
    TimeSpan Listened,
    TimeSpan Duration,
    bool Completed,
    bool Skipped,
    int SeekCount,
    int PauseCount);

/// <summary>
/// Every read and write of the music library. It owns the SQL so nothing above it has to, and
/// it never builds a statement out of user text — searches and filters are all parameterised.
///
/// Writes are serialised through one semaphore; reads open their own connection, which WAL
/// allows to run while a scan is writing.
/// </summary>
public sealed class MusicLibraryRepository
{
    private readonly LibraryDatabase _database;
    private readonly LogService _log;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public MusicLibraryRepository(LibraryDatabase database, LogService log)
    {
        _database = database;
        _log = log;
    }

    // ── Folders ─────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetFoldersAsync(CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_folders ORDER BY path";

        var folders = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) folders.Add(reader.GetString(0));
        return (IReadOnlyList<string>)folders;
    }, ct);

    public Task AddFolderAsync(string path, CancellationToken ct) => WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO library_folders(path) VALUES ($path)";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }, ct);

    /// <summary>
    /// Forgets a folder and everything indexed under it. Only database rows go: the files on
    /// disk are never touched, which is what makes removing a folder safe to do by accident.
    /// </summary>
    public Task<int> RemoveFolderAsync(string path, CancellationToken ct) => WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();

        using var deleteTracks = connection.CreateCommand();
        deleteTracks.Transaction = transaction;
        deleteTracks.CommandText = """
            DELETE FROM tracks
            WHERE path IS NOT NULL AND (folder = $path OR folder LIKE $prefix ESCAPE '\')
            """;
        deleteTracks.Parameters.AddWithValue("$path", path);
        deleteTracks.Parameters.AddWithValue("$prefix", SubfolderPattern(path));
        int removed = deleteTracks.ExecuteNonQuery();

        using var deleteFolder = connection.CreateCommand();
        deleteFolder.Transaction = transaction;
        deleteFolder.CommandText = "DELETE FROM library_folders WHERE path = $path";
        deleteFolder.Parameters.AddWithValue("$path", path);
        deleteFolder.ExecuteNonQuery();

        RebuildFtsFor(connection, transaction, null);
        transaction.Commit();
        return removed;
    }, ct);

    // ── Tracks ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts or updates a track, keyed by its path for a file and by its source for anything
    /// streamed. Returns the row id either way.
    /// </summary>
    public Task<long> UpsertTrackAsync(Track track, DateTime nowUtc, CancellationToken ct) =>
        WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            long id = Upsert(connection, transaction, track, nowUtc);
            transaction.Commit();
            return id;
        }, ct);

    /// <summary>
    /// Writes a batch in one transaction. The scanner uses this so a cancelled scan leaves the
    /// database whole: a batch is either fully applied or not at all.
    /// </summary>
    public Task<int> UpsertBatchAsync(IReadOnlyList<Track> tracks, DateTime nowUtc, CancellationToken ct) =>
        WriteAsync(connection =>
        {
            if (tracks.Count == 0) return 0;

            using var transaction = connection.BeginTransaction();
            foreach (var track in tracks) Upsert(connection, transaction, track, nowUtc);
            transaction.Commit();
            return tracks.Count;
        }, ct);

    public Task<int> DeleteByPathsAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        WriteAsync(connection =>
        {
            if (paths.Count == 0) return 0;

            using var transaction = connection.BeginTransaction();
            int removed = 0;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tracks WHERE path = $path RETURNING id";
            var parameter = command.Parameters.Add("$path", SqliteType.Text);

            foreach (string path in paths)
            {
                parameter.Value = path;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    DeleteFts(connection, transaction, reader.GetInt64(0));
                    removed++;
                }
            }

            transaction.Commit();
            return removed;
        }, ct);

    /// <summary>Path, size and modification time for one folder, so a rescan can skip what has not changed.</summary>
    public Task<IReadOnlyDictionary<string, (long Size, long ModifiedUtc)>> GetFileStampsAsync(
        string? folder, CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = folder is null
            ? "SELECT path, file_size, modified_utc FROM tracks WHERE path IS NOT NULL"
            : """
              SELECT path, file_size, modified_utc FROM tracks
              WHERE path IS NOT NULL AND (folder = $folder OR folder LIKE $prefix ESCAPE '\')
              """;

        if (folder is not null)
        {
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$prefix", SubfolderPattern(folder));
        }

        var stamps = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) stamps[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        return (IReadOnlyDictionary<string, (long, long)>)stamps;
    }, ct);

    public Task<Track?> GetTrackAsync(long id, CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectTrack + " WHERE t.id = $id";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrack(reader) : null;
    }, ct);

    public Task<Track?> GetTrackByPathAsync(string path, CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectTrack + " WHERE t.path = $path";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTrack(reader) : null;
    }, ct);

    public Task<IReadOnlyList<Track>> GetTracksAsync(TrackQuery query, CancellationToken ct) =>
        ReadAsync(connection =>
        {
            var sql = new StringBuilder(SelectTrack);
            sql.Append(" WHERE t.hidden = 0");

            using var command = connection.CreateCommand();

            if (query.AlbumId is { } album)
            {
                sql.Append(" AND t.album_id = $album");
                command.Parameters.AddWithValue("$album", album);
            }
            if (query.ArtistId is { } artist)
            {
                sql.Append(" AND EXISTS (SELECT 1 FROM track_artists ta WHERE ta.track_id = t.id AND ta.artist_id = $artist)");
                command.Parameters.AddWithValue("$artist", artist);
            }
            if (query.GenreId is { } genre)
            {
                sql.Append(" AND EXISTS (SELECT 1 FROM track_genres tg WHERE tg.track_id = t.id AND tg.genre_id = $genre)");
                command.Parameters.AddWithValue("$genre", genre);
            }
            if (!string.IsNullOrEmpty(query.Folder))
            {
                sql.Append(@" AND (t.folder = $folder OR t.folder LIKE $folderPrefix ESCAPE '\')");
                command.Parameters.AddWithValue("$folder", query.Folder);
                command.Parameters.AddWithValue("$folderPrefix", SubfolderPattern(query.Folder));
            }
            if (query.LikedOnly) sql.Append(" AND t.liked = 1");

            // The sort column comes from a fixed map, never from the caller's text.
            sql.Append(" ORDER BY ").Append(SortColumn(query.Sort)).Append(query.Descending ? " DESC" : " ASC");
            sql.Append(" LIMIT $limit OFFSET $offset");
            command.Parameters.AddWithValue("$limit", query.Limit == int.MaxValue ? -1 : query.Limit);
            command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));

            command.CommandText = sql.ToString();
            return ReadTracks(command);
        }, ct);

    /// <summary>
    /// Prefix search across title, artists, album and genre. Each word the user typed becomes a
    /// prefix term and the terms are ANDed, so "dark si" finds "Dark Side of the Moon".
    /// </summary>
    public Task<IReadOnlyList<Track>> SearchAsync(string text, int limit, CancellationToken ct) =>
        ReadAsync(connection =>
        {
            string match = BuildMatchQuery(text);
            if (match.Length == 0) return (IReadOnlyList<Track>)Array.Empty<Track>();

            using var command = connection.CreateCommand();
            // The MATCH is done in a subquery where the table is named outright: FTS5 will not
            // accept an aliased table on the left of MATCH, and the join needs the alias.
            command.CommandText = SelectTrack + "\n" + """
                 JOIN (SELECT rowid AS fts_id, rank AS fts_rank
                       FROM tracks_fts WHERE tracks_fts MATCH $match) m ON m.fts_id = t.id
                 WHERE t.hidden = 0
                 ORDER BY m.fts_rank
                 LIMIT $limit
                 """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            return ReadTracks(command);
        }, ct);

    // ── Browsing ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AlbumSummary>> GetAlbumsAsync(AlbumQuery query, CancellationToken ct) =>
        ReadAsync(connection =>
        {
            var sql = new StringBuilder("""
                SELECT a.id, a.title, a.album_artist, a.year,
                       COUNT(t.id), COALESCE(SUM(t.duration_ms), 0), a.cover_key
                FROM albums a
                JOIN tracks t ON t.album_id = a.id AND t.hidden = 0
                """);

            using var command = connection.CreateCommand();

            if (query.ArtistId is { } artist)
            {
                sql.Append(" WHERE EXISTS (SELECT 1 FROM track_artists ta WHERE ta.track_id = t.id AND ta.artist_id = $artist)");
                command.Parameters.AddWithValue("$artist", artist);
            }
            else if (query.GenreId is { } genre)
            {
                sql.Append(" WHERE EXISTS (SELECT 1 FROM track_genres tg WHERE tg.track_id = t.id AND tg.genre_id = $genre)");
                command.Parameters.AddWithValue("$genre", genre);
            }

            sql.Append(" GROUP BY a.id ORDER BY ")
               .Append(query.Sort switch
               {
                   AlbumSortField.Artist => "a.album_artist COLLATE NOCASE, a.year",
                   AlbumSortField.Year => "a.year",
                   _ => "a.title COLLATE NOCASE"
               })
               .Append(query.Descending ? " DESC" : " ASC");

            command.CommandText = sql.ToString();

            var albums = new List<AlbumSummary>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                albums.Add(new AlbumSummary(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetInt32(4),
                    TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
            return (IReadOnlyList<AlbumSummary>)albums;
        }, ct);

    public Task<IReadOnlyList<ArtistSummary>> GetArtistsAsync(CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ar.id, ar.name,
                   COUNT(DISTINCT t.album_id),
                   COUNT(DISTINCT t.id)
            FROM artists ar
            JOIN track_artists ta ON ta.artist_id = ar.id
            JOIN tracks t ON t.id = ta.track_id AND t.hidden = 0
            GROUP BY ar.id
            ORDER BY ar.name COLLATE NOCASE
            """;

        var artists = new List<ArtistSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            artists.Add(new ArtistSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        return (IReadOnlyList<ArtistSummary>)artists;
    }, ct);

    public Task<IReadOnlyList<GenreSummary>> GetGenresAsync(CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.id, g.name, COUNT(DISTINCT t.id)
            FROM genres g
            JOIN track_genres tg ON tg.genre_id = g.id
            JOIN tracks t ON t.id = tg.track_id AND t.hidden = 0
            GROUP BY g.id
            ORDER BY g.name COLLATE NOCASE
            """;

        var genres = new List<GenreSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            genres.Add(new GenreSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return (IReadOnlyList<GenreSummary>)genres;
    }, ct);

    // ── Flags and history ───────────────────────────────────────────────────

    public Task SetLikedAsync(long trackId, bool liked, CancellationToken ct) => WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tracks SET liked = $liked WHERE id = $id";
        command.Parameters.AddWithValue("$liked", liked ? 1 : 0);
        command.Parameters.AddWithValue("$id", trackId);
        command.ExecuteNonQuery();
    }, ct);

    /// <summary>Excludes a track from the library. The file itself is never touched.</summary>
    public Task HideTrackAsync(long trackId, CancellationToken ct) => WriteAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tracks SET hidden = 1 WHERE id = $id";
        command.Parameters.AddWithValue("$id", trackId);
        command.ExecuteNonQuery();
    }, ct);

    /// <summary>
    /// Records one play in full. The play counter only moves for a play that was not a skip, so
    /// "most played" means most listened to rather than most started.
    /// </summary>
    public Task RecordPlayAsync(PlayRecord play, CancellationToken ct) => WriteAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO play_history
                (track_id, started_utc, ended_utc, listened_ms, duration_ms, completed, skipped, seek_count, pause_count)
            VALUES
                ($track, $started, $ended, $listened, $duration, $completed, $skipped, $seeks, $pauses)
            """;
        insert.Parameters.AddWithValue("$track", play.TrackId);
        insert.Parameters.AddWithValue("$started", Ticks(play.StartedUtc));
        insert.Parameters.AddWithValue("$ended", Ticks(play.EndedUtc));
        insert.Parameters.AddWithValue("$listened", (long)play.Listened.TotalMilliseconds);
        insert.Parameters.AddWithValue("$duration", (long)play.Duration.TotalMilliseconds);
        insert.Parameters.AddWithValue("$completed", play.Completed ? 1 : 0);
        insert.Parameters.AddWithValue("$skipped", play.Skipped ? 1 : 0);
        insert.Parameters.AddWithValue("$seeks", play.SeekCount);
        insert.Parameters.AddWithValue("$pauses", play.PauseCount);
        insert.ExecuteNonQuery();

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE tracks
            SET last_played_utc = $ended,
                play_count = play_count + CASE WHEN $skipped = 1 THEN 0 ELSE 1 END
            WHERE id = $track
            """;
        update.Parameters.AddWithValue("$ended", Ticks(play.EndedUtc));
        update.Parameters.AddWithValue("$skipped", play.Skipped ? 1 : 0);
        update.Parameters.AddWithValue("$track", play.TrackId);
        update.ExecuteNonQuery();

        transaction.Commit();
    }, ct);

    public Task<IReadOnlyList<PlayRecord>> GetHistoryAsync(int limit, CancellationToken ct) =>
        ReadAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT track_id, started_utc, ended_utc, listened_ms, duration_ms,
                       completed, skipped, seek_count, pause_count
                FROM play_history
                ORDER BY started_utc DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100_000));

            var plays = new List<PlayRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                plays.Add(new PlayRecord(
                    reader.GetInt64(0),
                    FromTicks(reader.GetInt64(1)),
                    reader.IsDBNull(2) ? FromTicks(reader.GetInt64(1)) : FromTicks(reader.GetInt64(2)),
                    TimeSpan.FromMilliseconds(reader.GetInt64(3)),
                    TimeSpan.FromMilliseconds(reader.GetInt64(4)),
                    reader.GetInt32(5) == 1,
                    reader.GetInt32(6) == 1,
                    reader.GetInt32(7),
                    reader.GetInt32(8)));
            }
            return (IReadOnlyList<PlayRecord>)plays;
        }, ct);

    public Task<int> CountTracksAsync(CancellationToken ct) => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tracks WHERE hidden = 0";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }, ct);

    // ── Writing one track ───────────────────────────────────────────────────

    private long Upsert(SqliteConnection connection, SqliteTransaction transaction, Track track, DateTime nowUtc)
    {
        long? albumId = track.Album is { Length: > 0 }
            ? LookupId(connection, transaction, "albums", "title", track.Album,
                ("album_artist", track.AlbumArtist ?? track.Artists.FirstOrDefault() ?? "Unknown artist"))
            : null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // ON CONFLICT targets the partial index that matches this kind of track, so a rescan
        // updates the existing row instead of failing or duplicating it.
        command.CommandText = track.IsLocal
            ? UpsertSql + " ON CONFLICT(path) WHERE path IS NOT NULL DO UPDATE SET " + UpdateSet + " RETURNING id"
            : UpsertSql + " ON CONFLICT(source_provider, source_id) WHERE source_id IS NOT NULL DO UPDATE SET " + UpdateSet + " RETURNING id";

        Bind(command, track, albumId, nowUtc);

        long id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        WriteArtists(connection, transaction, id, track);
        WriteGenres(connection, transaction, id, track);
        WriteFts(connection, transaction, id, track);
        return id;
    }

    private const string UpsertSql = """
        INSERT INTO tracks
            (path, folder, source_provider, source_id, source_url, thumbnail_url,
             file_size, modified_utc, title, artists_display, album_id, track_no, disc_no, year,
             genres_display, duration_ms, codec, sample_rate, bits_per_sample, channels,
             bitrate_kbps, lossless, rg_track_gain, rg_track_peak, rg_album_gain, rg_album_peak,
             cover_key, added_utc, liked)
        VALUES
            ($path, $folder, $provider, $sourceId, $sourceUrl, $thumb,
             $size, $modified, $title, $artists, $album, $trackNo, $discNo, $year,
             $genres, $duration, $codec, $rate, $bits, $channels,
             $bitrate, $lossless, $rgTrackGain, $rgTrackPeak, $rgAlbumGain, $rgAlbumPeak,
             $cover, $added, $liked)
        """;

    /// <summary>
    /// What a rescan refreshes. Deliberately absent: <c>added_utc</c>, <c>play_count</c>,
    /// <c>last_played_utc</c>, <c>liked</c> and <c>hidden</c> — those belong to the listener,
    /// not to the file, and re-reading a tag must never reset them.
    /// </summary>
    private const string UpdateSet = """
        folder = excluded.folder,
        source_url = excluded.source_url,
        thumbnail_url = excluded.thumbnail_url,
        file_size = excluded.file_size,
        modified_utc = excluded.modified_utc,
        title = excluded.title,
        artists_display = excluded.artists_display,
        album_id = excluded.album_id,
        track_no = excluded.track_no,
        disc_no = excluded.disc_no,
        year = excluded.year,
        genres_display = excluded.genres_display,
        duration_ms = excluded.duration_ms,
        codec = excluded.codec,
        sample_rate = excluded.sample_rate,
        bits_per_sample = excluded.bits_per_sample,
        channels = excluded.channels,
        bitrate_kbps = excluded.bitrate_kbps,
        lossless = excluded.lossless,
        rg_track_gain = excluded.rg_track_gain,
        rg_track_peak = excluded.rg_track_peak,
        rg_album_gain = excluded.rg_album_gain,
        rg_album_peak = excluded.rg_album_peak,
        cover_key = excluded.cover_key
        """;

    private static void Bind(SqliteCommand command, Track track, long? albumId, DateTime nowUtc)
    {
        string? folder = track.IsLocal && !string.IsNullOrEmpty(track.Path)
            ? System.IO.Path.GetDirectoryName(track.Path)
            : null;

        command.Parameters.AddWithValue("$path", Null(track.IsLocal ? track.Path : null));
        command.Parameters.AddWithValue("$folder", Null(folder));
        command.Parameters.AddWithValue("$provider", Null(track.Source?.Provider));
        command.Parameters.AddWithValue("$sourceId", Null(track.Source?.Id));
        command.Parameters.AddWithValue("$sourceUrl", Null(track.Source?.Url));
        command.Parameters.AddWithValue("$thumb", Null(track.ThumbnailUrl));
        command.Parameters.AddWithValue("$size", 0L);
        command.Parameters.AddWithValue("$modified", 0L);
        command.Parameters.AddWithValue("$title", track.Title);
        command.Parameters.AddWithValue("$artists", string.Join("; ", track.Artists));
        command.Parameters.AddWithValue("$album", Null(albumId));
        command.Parameters.AddWithValue("$trackNo", Null(track.TrackNumber));
        command.Parameters.AddWithValue("$discNo", Null(track.DiscNumber));
        command.Parameters.AddWithValue("$year", Null(track.Year));
        command.Parameters.AddWithValue("$genres", Null(track.Genres.Count == 0 ? null : string.Join("; ", track.Genres)));
        command.Parameters.AddWithValue("$duration", (long)track.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$codec", Null(track.Format.Codec));
        command.Parameters.AddWithValue("$rate", track.Format.SampleRate);
        command.Parameters.AddWithValue("$bits", track.Format.BitsPerSample);
        command.Parameters.AddWithValue("$channels", track.Format.Channels);
        command.Parameters.AddWithValue("$bitrate", track.Format.BitrateKbps);
        command.Parameters.AddWithValue("$lossless", track.Format.IsLossless ? 1 : 0);
        command.Parameters.AddWithValue("$rgTrackGain", Null(track.ReplayGain?.TrackGainDb));
        command.Parameters.AddWithValue("$rgTrackPeak", Null(track.ReplayGain?.TrackPeak));
        command.Parameters.AddWithValue("$rgAlbumGain", Null(track.ReplayGain?.AlbumGainDb));
        command.Parameters.AddWithValue("$rgAlbumPeak", Null(track.ReplayGain?.AlbumPeak));
        command.Parameters.AddWithValue("$cover", Null(track.CoverKey));
        command.Parameters.AddWithValue("$added", Ticks(nowUtc));
        command.Parameters.AddWithValue("$liked", track.Liked ? 1 : 0);
    }

    /// <summary>Sets the file's size and time, which only the scanner knows.</summary>
    public Task SetFileStampAsync(long trackId, long size, DateTime modifiedUtc, CancellationToken ct) =>
        WriteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tracks SET file_size = $size, modified_utc = $modified WHERE id = $id";
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$modified", Ticks(modifiedUtc));
            command.Parameters.AddWithValue("$id", trackId);
            command.ExecuteNonQuery();
        }, ct);

    private static void WriteArtists(SqliteConnection connection, SqliteTransaction transaction, long trackId, Track track)
    {
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM track_artists WHERE track_id = $track";
        clear.Parameters.AddWithValue("$track", trackId);
        clear.ExecuteNonQuery();

        foreach (string artist in track.Artists.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
            LinkArtist(connection, transaction, trackId, artist, role: 0);

        if (!string.IsNullOrWhiteSpace(track.AlbumArtist))
            LinkArtist(connection, transaction, trackId, track.AlbumArtist, role: 1);
    }

    private static void LinkArtist(SqliteConnection connection, SqliteTransaction transaction, long trackId, string name, int role)
    {
        long artistId = LookupId(connection, transaction, "artists", "name", name);

        using var link = connection.CreateCommand();
        link.Transaction = transaction;
        link.CommandText = "INSERT OR IGNORE INTO track_artists(track_id, artist_id, role) VALUES ($track, $artist, $role)";
        link.Parameters.AddWithValue("$track", trackId);
        link.Parameters.AddWithValue("$artist", artistId);
        link.Parameters.AddWithValue("$role", role);
        link.ExecuteNonQuery();
    }

    private static void WriteGenres(SqliteConnection connection, SqliteTransaction transaction, long trackId, Track track)
    {
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM track_genres WHERE track_id = $track";
        clear.Parameters.AddWithValue("$track", trackId);
        clear.ExecuteNonQuery();

        foreach (string genre in track.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            long genreId = LookupId(connection, transaction, "genres", "name", genre);

            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO track_genres(track_id, genre_id) VALUES ($track, $genre)";
            link.Parameters.AddWithValue("$track", trackId);
            link.Parameters.AddWithValue("$genre", genreId);
            link.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Finds or creates a lookup row. The table and column names are supplied by this file
    /// only, never by a caller, so they can safely be interpolated.
    /// </summary>
    private static long LookupId(
        SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, string value, params (string Column, string Value)[] extra)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;

        string columns = column + string.Concat(extra.Select(e => ", " + e.Column));
        string values = "$v0" + string.Concat(extra.Select((_, i) => $", $v{i + 1}"));

        insert.CommandText = $"INSERT OR IGNORE INTO {table}({columns}) VALUES ({values})";
        insert.Parameters.AddWithValue("$v0", value);
        for (int i = 0; i < extra.Length; i++) insert.Parameters.AddWithValue($"$v{i + 1}", extra[i].Value);
        insert.ExecuteNonQuery();

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"SELECT id FROM {table} WHERE {column} = $v0"
            + string.Concat(extra.Select((e, i) => $" AND {e.Column} = $v{i + 1}"));
        select.Parameters.AddWithValue("$v0", value);
        for (int i = 0; i < extra.Length; i++) select.Parameters.AddWithValue($"$v{i + 1}", extra[i].Value);

        return Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // ── Full-text index ─────────────────────────────────────────────────────

    /// <summary>
    /// An external-content FTS table is not kept in step by SQLite, so every write has to
    /// delete the old row and insert the new one by rowid.
    /// </summary>
    private static void WriteFts(SqliteConnection connection, SqliteTransaction transaction, long trackId, Track track)
    {
        DeleteFts(connection, transaction, trackId);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tracks_fts(rowid, title, artists_display, album, genres_display)
            VALUES ($id, $title, $artists, $album, $genres)
            """;
        insert.Parameters.AddWithValue("$id", trackId);
        insert.Parameters.AddWithValue("$title", track.Title);
        insert.Parameters.AddWithValue("$artists", string.Join(" ", track.Artists));
        insert.Parameters.AddWithValue("$album", (object?)track.Album ?? string.Empty);
        insert.Parameters.AddWithValue("$genres", string.Join(" ", track.Genres));
        insert.ExecuteNonQuery();
    }

    private static void DeleteFts(SqliteConnection connection, SqliteTransaction transaction, long trackId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM tracks_fts WHERE rowid = $id";
        command.Parameters.AddWithValue("$id", trackId);
        command.ExecuteNonQuery();
    }

    private static void RebuildFtsFor(SqliteConnection connection, SqliteTransaction transaction, long? _)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM tracks_fts;
            INSERT INTO tracks_fts(rowid, title, artists_display, album, genres_display)
            SELECT t.id, t.title, t.artists_display, COALESCE(a.title, ''), COALESCE(t.genres_display, '')
            FROM tracks t LEFT JOIN albums a ON a.id = t.album_id;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Turns what the user typed into an FTS query. Everything that is not a letter or a digit
    /// is dropped, so quotes, asterisks and operators can never change the query's meaning.
    /// </summary>
    internal static string BuildMatchQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var terms = new List<string>();
        foreach (string raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c)) cleaned.Append(c);
            }

            if (cleaned.Length > 0) terms.Add("\"" + cleaned + "\"*");
        }

        return string.Join(" AND ", terms);
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    private const string SelectTrack = """
        SELECT t.id, t.path, t.title, t.artists_display, a.title, t.track_no, t.disc_no, t.year,
               t.genres_display, t.duration_ms, t.codec, t.sample_rate, t.bits_per_sample,
               t.channels, t.bitrate_kbps, t.lossless, t.rg_track_gain, t.rg_track_peak,
               t.rg_album_gain, t.rg_album_peak, t.cover_key, t.liked,
               t.source_provider, t.source_id, t.source_url, t.thumbnail_url, a.album_artist
        FROM tracks t LEFT JOIN albums a ON a.id = t.album_id
        """;

    private static IReadOnlyList<Track> ReadTracks(SqliteCommand command)
    {
        var tracks = new List<Track>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) tracks.Add(ReadTrack(reader));
        return tracks;
    }

    private static Track ReadTrack(SqliteDataReader reader)
    {
        string? provider = reader.IsDBNull(22) ? null : reader.GetString(22);
        string? sourceId = reader.IsDBNull(23) ? null : reader.GetString(23);

        var track = new Track(
            Id: reader.GetInt64(0),
            Path: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Title: reader.GetString(2),
            Artists: Split(reader.GetString(3)),
            Album: reader.IsDBNull(4) ? null : reader.GetString(4),
            AlbumArtist: reader.IsDBNull(26) ? null : reader.GetString(26),
            TrackNumber: reader.IsDBNull(5) ? null : reader.GetInt32(5),
            DiscNumber: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Year: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Genres: reader.IsDBNull(8) ? Array.Empty<string>() : Split(reader.GetString(8)),
            Duration: TimeSpan.FromMilliseconds(reader.GetInt64(9)),
            Format: new AudioFormatInfo(
                reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                reader.GetInt32(15) == 1),
            ReplayGain: reader.IsDBNull(16) && reader.IsDBNull(18)
                ? null
                : new ReplayGainInfo(
                    reader.IsDBNull(16) ? null : reader.GetDouble(16),
                    reader.IsDBNull(17) ? null : reader.GetDouble(17),
                    reader.IsDBNull(18) ? null : reader.GetDouble(18),
                    reader.IsDBNull(19) ? null : reader.GetDouble(19)),
            CoverKey: reader.IsDBNull(20) ? null : reader.GetString(20),
            Liked: reader.GetInt32(21) == 1);

        if (sourceId is null) return track;

        return track with
        {
            Source = new ProviderRef(provider!, sourceId, reader.IsDBNull(24) ? null : reader.GetString(24)),
            ThumbnailUrl = reader.IsDBNull(25) ? null : reader.GetString(25)
        };
    }

    private static IReadOnlyList<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string SortColumn(TrackSortField sort) => sort switch
    {
        TrackSortField.Artist => "t.artists_display COLLATE NOCASE",
        TrackSortField.Album => "a.title COLLATE NOCASE, t.disc_no, t.track_no",
        TrackSortField.Year => "t.year",
        TrackSortField.DateAdded => "t.added_utc",
        TrackSortField.Duration => "t.duration_ms",
        _ => "t.title COLLATE NOCASE"
    };

    // ── Plumbing ────────────────────────────────────────────────────────────

    private async Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken ct) =>
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var connection = _database.Open();
            return read(connection);
        }, ct);

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, T> write, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var connection = _database.Open();
                return write(connection);
            }, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task WriteAsync(Action<SqliteConnection> write, CancellationToken ct) =>
        WriteAsync<object?>(connection => { write(connection); return null; }, ct);

    private static object Null(object? value) => value ?? DBNull.Value;

    private static long Ticks(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromTicks(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;

    /// <summary>
    /// A LIKE pattern matching everything inside a folder. The path itself is escaped so a
    /// folder whose name contains % or _ still matches only itself, and the trailing wildcard
    /// is added afterwards, where it is meant to stay a wildcard.
    /// </summary>
    private static string SubfolderPattern(string folder) =>
        Escape(folder.TrimEnd('\\') + '\\') + "%";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
