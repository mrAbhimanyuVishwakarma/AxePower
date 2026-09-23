using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Opens the music library database and keeps its schema current.
///
/// The version lives in <c>PRAGMA user_version</c> rather than a table, so a database from a
/// newer build is recognisable before anything tries to read it. A file that cannot be opened
/// or upgraded is moved aside and replaced: a library is an index of files that still exist on
/// disk, so rebuilding it costs a scan, never any of the user's music.
/// </summary>
public sealed class LibraryDatabase
{
    /// <summary>Bump this and add a case to <see cref="Migrate"/> when the schema changes.</summary>
    public const int SchemaVersion = 1;

    private readonly MusicStoragePaths _paths;
    private readonly LogService _log;
    private readonly object _gate = new();
    private bool _ready;

    public LibraryDatabase(MusicStoragePaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public string FilePath => Path.Combine(_paths.Root, "library.db");

    /// <summary>Set when a damaged database had to be replaced, so the UI can say so once.</summary>
    public bool WasRebuilt { get; private set; }

    /// <summary>
    /// A connection with the schema in place. Readers open their own; writes are serialised by
    /// the repository, and WAL lets a scan write while the UI reads.
    /// </summary>
    public SqliteConnection Open()
    {
        EnsureReady();
        return Connect();
    }

    private SqliteConnection Connect()
    {
        Directory.CreateDirectory(_paths.Root);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true
        }.ToString());

        try
        {
            connection.Open();

            using var pragma = connection.CreateCommand();
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                """;
            pragma.ExecuteNonQuery();
        }
        catch
        {
            // Setting up a damaged file fails here. Without this the connection would be left
            // open, holding a lock on the very file the recovery path then tries to move.
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private void EnsureReady()
    {
        if (_ready) return;

        lock (_gate)
        {
            if (_ready) return;

            try
            {
                using var connection = Connect();
                Migrate(connection);
            }
            catch (Exception ex)
            {
                Rebuild(ex);
            }

            _ready = true;
        }
    }

    private void Migrate(SqliteConnection connection)
    {
        int version = ReadVersion(connection);

        if (version > SchemaVersion)
        {
            // Written by a newer AxePower. Reading it could mean silently dropping columns it
            // depends on, so it is set aside instead.
            throw new InvalidOperationException(
                $"The library was created by a newer version of AxePower (schema {version}).");
        }

        if (version == SchemaVersion) return;

        using var transaction = connection.BeginTransaction();

        if (version < 1) CreateV1(connection, transaction);

        using (var setVersion = connection.CreateCommand())
        {
            setVersion.Transaction = transaction;
            // PRAGMA takes no parameters, so the value is written from a constant, never input.
            setVersion.CommandText = "PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture);
            setVersion.ExecuteNonQuery();
        }

        transaction.Commit();
        _log.LogInfo($"[Music] Library schema is now version {SchemaVersion}.");
    }

    private static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void CreateV1(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaV1;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves a database that cannot be used out of the way and starts again. The old file is
    /// kept rather than deleted, so a bug here never destroys anything irreversibly.
    /// </summary>
    private void Rebuild(Exception cause)
    {
        _log.LogWarning($"[Music] The library could not be opened ({cause.Message}); rebuilding it.");

        try
        {
            // Disposing a pooled connection hands it back to the pool rather than closing the
            // file, so the damaged database would still be locked when we try to move it.
            SqliteConnection.ClearAllPools();

            if (File.Exists(FilePath))
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(FilePath, FilePath + ".corrupt-" + stamp, overwrite: true);
            }

            // WAL side files belong to the old database and would be applied to the new one.
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                string side = FilePath + suffix;
                if (File.Exists(side)) File.Delete(side);
            }

            using var connection = Connect();
            Migrate(connection);
            WasRebuilt = true;
        }
        catch (Exception ex)
        {
            _log.LogError("[Music] The library could not be rebuilt.", ex);
            throw;
        }
    }

    /// <summary>
    /// The first schema. Two things differ from the original plan, both decided before any
    /// database existed because changing them later means a migration and lost history:
    ///
    /// 1. A track no longer has to be a file. <c>path</c> is null for anything from a streaming
    ///    source, which is identified by <c>source_provider</c> + <c>source_id</c> instead.
    ///    Partial unique indexes keep each kind unique without a second table, so liked songs,
    ///    playlists and history work the same for both.
    ///
    /// 2. History records what happened, not just that something played: whether it was
    ///    skipped or finished, how much was actually heard, and how many times the listener
    ///    jumped around. One play of a half-hour track is not ten plays of a three-minute one,
    ///    and "the tracks you skip" cannot be recovered later if it was never written down.
    /// </summary>
    private const string SchemaV1 = """
        CREATE TABLE library_folders (
            id   INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE);

        CREATE TABLE albums (
            id           INTEGER PRIMARY KEY,
            title        TEXT NOT NULL,
            album_artist TEXT NOT NULL,
            year         INTEGER,
            cover_key    TEXT,
            UNIQUE(title, album_artist));

        CREATE TABLE artists (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE);

        CREATE TABLE genres (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE);

        CREATE TABLE tracks (
            id                  INTEGER PRIMARY KEY,
            path                TEXT COLLATE NOCASE,
            folder              TEXT COLLATE NOCASE,
            source_provider     TEXT COLLATE NOCASE,
            source_id           TEXT,
            source_url          TEXT,
            thumbnail_url       TEXT,
            file_size           INTEGER NOT NULL DEFAULT 0,
            modified_utc        INTEGER NOT NULL DEFAULT 0,
            title               TEXT NOT NULL,
            artists_display     TEXT NOT NULL,
            album_id            INTEGER REFERENCES albums(id),
            track_no            INTEGER,
            disc_no             INTEGER,
            year                INTEGER,
            genres_display      TEXT,
            duration_ms         INTEGER NOT NULL,
            codec               TEXT,
            sample_rate         INTEGER,
            bits_per_sample     INTEGER,
            channels            INTEGER,
            bitrate_kbps        INTEGER,
            lossless            INTEGER NOT NULL DEFAULT 0,
            rg_track_gain       REAL,
            rg_track_peak       REAL,
            rg_album_gain       REAL,
            rg_album_peak       REAL,
            cover_key           TEXT,
            has_embedded_lyrics INTEGER NOT NULL DEFAULT 0,
            added_utc           INTEGER NOT NULL,
            play_count          INTEGER NOT NULL DEFAULT 0,
            last_played_utc     INTEGER,
            liked               INTEGER NOT NULL DEFAULT 0,
            hidden              INTEGER NOT NULL DEFAULT 0,

            -- Every row is one or the other, never both and never neither.
            CHECK ((path IS NOT NULL AND source_id IS NULL)
                OR (path IS NULL AND source_id IS NOT NULL AND source_provider IS NOT NULL)));

        CREATE UNIQUE INDEX ux_tracks_path ON tracks(path) WHERE path IS NOT NULL;
        CREATE UNIQUE INDEX ux_tracks_source ON tracks(source_provider, source_id) WHERE source_id IS NOT NULL;
        CREATE INDEX ix_tracks_folder ON tracks(folder) WHERE folder IS NOT NULL;
        CREATE INDEX ix_tracks_album ON tracks(album_id);
        CREATE INDEX ix_tracks_liked ON tracks(liked) WHERE liked = 1;

        CREATE TABLE track_artists (
            track_id  INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            artist_id INTEGER NOT NULL REFERENCES artists(id),
            role      INTEGER NOT NULL,           -- 0 artist, 1 album artist
            PRIMARY KEY(track_id, artist_id, role));

        CREATE INDEX ix_track_artists_artist ON track_artists(artist_id);

        CREATE TABLE track_genres (
            track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            genre_id INTEGER NOT NULL REFERENCES genres(id),
            PRIMARY KEY(track_id, genre_id));

        CREATE INDEX ix_track_genres_genre ON track_genres(genre_id);

        CREATE TABLE playlists (
            id           INTEGER PRIMARY KEY,
            name         TEXT NOT NULL,
            kind         INTEGER NOT NULL DEFAULT 0,   -- 0 user, 1 liked, 2 history
            created_utc  INTEGER NOT NULL,
            modified_utc INTEGER NOT NULL);

        CREATE TABLE playlist_items (
            playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            position    INTEGER NOT NULL,
            track_id    INTEGER REFERENCES tracks(id) ON DELETE SET NULL,
            path        TEXT,                          -- keeps a file playable after a rescan
            PRIMARY KEY(playlist_id, position));

        CREATE TABLE play_history (
            id             INTEGER PRIMARY KEY,
            track_id       INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            started_utc    INTEGER NOT NULL,
            ended_utc      INTEGER,
            listened_ms    INTEGER NOT NULL DEFAULT 0,  -- time actually heard, pauses excluded
            duration_ms    INTEGER NOT NULL DEFAULT 0,  -- length at the time, so edits do not rewrite the past
            completed      INTEGER NOT NULL DEFAULT 0,
            skipped        INTEGER NOT NULL DEFAULT 0,
            seek_count     INTEGER NOT NULL DEFAULT 0,
            pause_count    INTEGER NOT NULL DEFAULT 0);

        CREATE INDEX ix_play_history_track ON play_history(track_id);
        CREATE INDEX ix_play_history_started ON play_history(started_utc);

        CREATE TABLE lyrics_cache (
            track_id    INTEGER PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE,
            synced      INTEGER NOT NULL,
            body        TEXT,                          -- null is a remembered "not found"
            source      INTEGER NOT NULL,
            fetched_utc INTEGER NOT NULL);

        -- A plain FTS5 table, not an external-content one. A contentless table can only be
        -- deleted from by handing back the exact values that were indexed, which the row no
        -- longer has once a rescan has updated it; getting that wrong corrupts the index.
        -- Keeping a copy costs a little space and makes delete-by-rowid correct.
        CREATE VIRTUAL TABLE tracks_fts USING fts5(
            title, artists_display, album, genres_display,
            tokenize='unicode61 remove_diacritics 2');
        """;
}
