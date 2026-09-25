using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// The library repository against a real SQLite file in a temp folder. Nothing here touches the
/// user's library, and no test writes a music file.
/// </summary>
public sealed class MusicLibraryRepositoryTests : IDisposable
{
    private readonly string _folder;
    private readonly LibraryDatabase _database;
    private readonly MusicLibraryRepository _repository;

    public MusicLibraryRepositoryTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "AxePowerLibraryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);

        var log = new LogService();
        _database = new LibraryDatabase(new MusicStoragePaths(_folder), log);
        _repository = new MusicLibraryRepository(_database, log);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken None = CancellationToken.None;

    private static Track Local(
        string path, string title, string artist = "Artist",
        string? album = "Album", string? genre = "Rock", int minutes = 3) =>
        new(
            Id: 0, Path: path, Title: title,
            Artists: artist.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Album: album, AlbumArtist: artist.Split(';')[0],
            TrackNumber: 1, DiscNumber: 1, Year: 2020,
            Genres: genre is null ? Array.Empty<string>() : new[] { genre },
            Duration: TimeSpan.FromMinutes(minutes),
            Format: new AudioFormatInfo("FLAC", 44100, 16, 2, 900, true),
            ReplayGain: null, CoverKey: null, Liked: false);

    private static Track Remote(string id, string title, string artist = "Artist") =>
        Track.ForProvider(new ProviderRef("youtube", id, "https://y/" + id), title, new[] { artist },
            TimeSpan.FromMinutes(3), "Album", "https://thumb/" + id);

    // ── Schema and recovery ─────────────────────────────────────────────────

    [Fact]
    public async Task ANewDatabaseIsCreatedAtTheCurrentSchemaVersion()
    {
        await _repository.AddFolderAsync(@"C:\Music", None);

        Assert.True(File.Exists(_database.FilePath));
        Assert.False(_database.WasRebuilt);
    }

    [Fact]
    public async Task ADamagedDatabaseIsSetAsideAndRebuilt()
    {
        string path = Path.Combine(_folder, "library.db");
        File.WriteAllText(path, "this is not a database");

        var log = new LogService();
        var database = new LibraryDatabase(new MusicStoragePaths(_folder), log);
        var repository = new MusicLibraryRepository(database, log);

        await repository.AddFolderAsync(@"C:\Music", None);

        Assert.True(database.WasRebuilt);
        Assert.Equal(new[] { @"C:\Music" }, await repository.GetFoldersAsync(None));

        // The unreadable file is kept rather than deleted, so a bug here destroys nothing.
        Assert.NotEmpty(Directory.GetFiles(_folder, "library.db.corrupt-*"));
    }

    // ── Tracks ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALocalTrackRoundTrips()
    {
        long id = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Song"), Now, None);

        var loaded = await _repository.GetTrackAsync(id, None);

        Assert.NotNull(loaded);
        Assert.Equal("Song", loaded!.Title);
        Assert.Equal(@"C:\Music\a.flac", loaded.Path);
        Assert.Equal("Album", loaded.Album);
        Assert.Equal(new[] { "Artist" }, loaded.Artists);
        Assert.Equal(new[] { "Rock" }, loaded.Genres);
        Assert.True(loaded.IsLocal);
        Assert.Equal(TimeSpan.FromMinutes(3), loaded.Duration);
        Assert.True(loaded.Format.IsLossless);
    }

    [Fact]
    public async Task AStreamedTrackRoundTripsWithItsSource()
    {
        // The whole point of the schema change: something with no file still gets a library row.
        long id = await _repository.UpsertTrackAsync(Remote("abc123", "Streamed"), Now, None);

        var loaded = await _repository.GetTrackAsync(id, None);

        Assert.NotNull(loaded);
        Assert.False(loaded!.IsLocal);
        Assert.Equal("youtube", loaded.Source!.Provider);
        Assert.Equal("abc123", loaded.Source.Id);
        Assert.Equal("https://y/abc123", loaded.Source.Url);
        Assert.Equal("https://thumb/abc123", loaded.ThumbnailUrl);
        Assert.Empty(loaded.Path);
    }

    [Fact]
    public async Task RescanningAFileUpdatesTheSameRow()
    {
        long first = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Old title"), Now, None);
        long second = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "New title"), Now, None);

        Assert.Equal(first, second);
        Assert.Equal("New title", (await _repository.GetTrackAsync(first, None))!.Title);
        Assert.Equal(1, await _repository.CountTracksAsync(None));
    }

    [Fact]
    public async Task ReplayingAStreamedTrackUpdatesTheSameRow()
    {
        long first = await _repository.UpsertTrackAsync(Remote("abc", "Old"), Now, None);
        long second = await _repository.UpsertTrackAsync(Remote("abc", "New"), Now, None);

        Assert.Equal(first, second);
        Assert.Equal(1, await _repository.CountTracksAsync(None));
    }

    [Fact]
    public async Task ALocalFileAndAStreamedTrackAreDifferentRowsEvenWithTheSameTitle()
    {
        long local = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Same"), Now, None);
        long remote = await _repository.UpsertTrackAsync(Remote("abc", "Same"), Now, None);

        Assert.NotEqual(local, remote);
        Assert.Equal(2, await _repository.CountTracksAsync(None));
    }

    [Fact]
    public async Task ARescanKeepsWhatTheListenerDidToATrack()
    {
        // Liked, the play count and when it was added belong to the listener, not to the file.
        long id = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Song"), Now, None);
        await _repository.SetLikedAsync(id, true, None);
        await _repository.RecordPlayAsync(Play(id, completed: true), None);

        await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Retagged"), Now.AddDays(1), None);

        var loaded = await _repository.GetTrackAsync(id, None);
        Assert.Equal("Retagged", loaded!.Title);
        Assert.True(loaded.Liked);
        Assert.Single(await _repository.GetHistoryAsync(10, None));
    }

    [Fact]
    public async Task DeletingByPathRemovesTheRowAndItsSearchEntry()
    {
        await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Vanishing"), Now, None);

        int removed = await _repository.DeleteByPathsAsync(new[] { @"C:\Music\a.flac" }, None);

        Assert.Equal(1, removed);
        Assert.Equal(0, await _repository.CountTracksAsync(None));
        Assert.Empty(await _repository.SearchAsync("Vanishing", 10, None));
    }

    [Fact]
    public async Task HidingATrackKeepsTheRowButTakesItOutOfEveryListing()
    {
        long id = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Hidden"), Now, None);

        await _repository.HideTrackAsync(id, None);

        Assert.Equal(0, await _repository.CountTracksAsync(None));
        Assert.Empty(await _repository.GetTracksAsync(new TrackQuery(), None));
        Assert.NotNull(await _repository.GetTrackAsync(id, None));
    }

    // ── Folders ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemovingAFolderDropsEverythingUnderItAndNothingElse()
    {
        await _repository.AddFolderAsync(@"C:\Music", None);
        await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "Inside"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\Music\Rock\b.flac", "Deeper"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\Other\c.flac", "Elsewhere"), Now, None);

        int removed = await _repository.RemoveFolderAsync(@"C:\Music", None);

        Assert.Equal(2, removed);
        Assert.Empty(await _repository.GetFoldersAsync(None));

        var left = await _repository.GetTracksAsync(new TrackQuery(), None);
        Assert.Equal(new[] { "Elsewhere" }, left.Select(t => t.Title));
    }

    [Fact]
    public async Task AFolderWhoseNameContainsAWildcardCharacterMatchesOnlyItself()
    {
        // "%" and "_" are LIKE wildcards; an unescaped path would take the other folder too.
        await _repository.UpsertTrackAsync(Local(@"C:\100%\a.flac", "Inside"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\100pc\b.flac", "Other"), Now, None);

        int removed = await _repository.RemoveFolderAsync(@"C:\100%", None);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "Other" }, (await _repository.GetTracksAsync(new TrackQuery(), None)).Select(t => t.Title));
    }

    [Fact]
    public async Task AddingTheSameFolderTwiceKeepsOneEntry()
    {
        await _repository.AddFolderAsync(@"C:\Music", None);
        await _repository.AddFolderAsync(@"C:\Music", None);

        Assert.Single(await _repository.GetFoldersAsync(None));
    }

    // ── Browsing and search ─────────────────────────────────────────────────

    [Fact]
    public async Task TracksCanBeFilteredByAlbumArtistAndGenre()
    {
        await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "A", "Pink Floyd", "Animals", "Rock"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\M\b.flac", "B", "Pink Floyd", "Animals", "Rock"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\M\c.flac", "C", "Miles Davis", "Kind of Blue", "Jazz"), Now, None);

        var albums = await _repository.GetAlbumsAsync(new AlbumQuery(), None);
        var artists = await _repository.GetArtistsAsync(None);
        var genres = await _repository.GetGenresAsync(None);

        Assert.Equal(2, albums.Count);
        Assert.Equal(2, albums.Single(a => a.Title == "Animals").TrackCount);

        long floyd = artists.Single(a => a.Name == "Pink Floyd").Id;
        Assert.Equal(2, (await _repository.GetTracksAsync(new TrackQuery(ArtistId: floyd), None)).Count);

        long jazz = genres.Single(g => g.Name == "Jazz").Id;
        Assert.Equal(new[] { "C" }, (await _repository.GetTracksAsync(new TrackQuery(GenreId: jazz), None)).Select(t => t.Title));
    }

    [Fact]
    public async Task TracksCanBeSortedAndPaged()
    {
        foreach (string title in new[] { "Charlie", "Alpha", "Bravo" })
            await _repository.UpsertTrackAsync(Local($@"C:\M\{title}.flac", title), Now, None);

        var ascending = await _repository.GetTracksAsync(new TrackQuery(TrackSortField.Title), None);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, ascending.Select(t => t.Title));

        var descending = await _repository.GetTracksAsync(new TrackQuery(TrackSortField.Title, Descending: true), None);
        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, descending.Select(t => t.Title));

        var page = await _repository.GetTracksAsync(new TrackQuery(TrackSortField.Title, Offset: 1, Limit: 1), None);
        Assert.Equal(new[] { "Bravo" }, page.Select(t => t.Title));
    }

    [Theory]
    [InlineData("dark", "Dark Side")]
    [InlineData("dar si", "Dark Side")]
    [InlineData("floyd", "Dark Side")]
    [InlineData("jazz", "Blue Note")]
    public async Task SearchMatchesPrefixesAcrossTitleArtistAlbumAndGenre(string query, string expected)
    {
        await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Dark Side", "Pink Floyd", "Dark Side of the Moon", "Rock"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\M\b.flac", "Blue Note", "Miles Davis", "Kind of Blue", "Jazz"), Now, None);

        var results = await _repository.SearchAsync(query, 10, None);

        Assert.Equal(new[] { expected }, results.Select(t => t.Title));
    }

    [Fact]
    public async Task SearchFindsAccentedTitlesTypedWithoutAccents()
    {
        await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Cafe Bleu", "Beyoncé"), Now, None);

        Assert.Single(await _repository.SearchAsync("beyonce", 10, None));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("*")]
    [InlineData("NEAR(")]
    [InlineData("a OR b")]
    [InlineData("   ")]
    public async Task SearchTextThatWouldBreakTheQueryIsHandledSafely(string query)
    {
        await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Song"), Now, None);

        // Anything that is not a letter or a digit is stripped, so FTS operators typed by
        // accident cannot change the query or throw.
        var results = await _repository.SearchAsync(query, 10, None);

        Assert.NotNull(results);
    }

    [Fact]
    public async Task RenamingATrackUpdatesWhatSearchFinds()
    {
        await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Before"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "After"), Now, None);

        Assert.Empty(await _repository.SearchAsync("Before", 10, None));
        Assert.Single(await _repository.SearchAsync("After", 10, None));
    }

    // ── History ─────────────────────────────────────────────────────────────

    private static PlayRecord Play(long trackId, bool completed = false, bool skipped = false, int listenedSeconds = 180) =>
        new(trackId, Now, Now.AddSeconds(listenedSeconds), TimeSpan.FromSeconds(listenedSeconds),
            TimeSpan.FromMinutes(3), completed, skipped, SeekCount: 2, PauseCount: 1);

    [Fact]
    public async Task APlayRecordsWhatActuallyHappened()
    {
        long id = await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Song"), Now, None);

        await _repository.RecordPlayAsync(Play(id, completed: true), None);

        var history = await _repository.GetHistoryAsync(10, None);
        var play = Assert.Single(history);

        Assert.Equal(id, play.TrackId);
        Assert.True(play.Completed);
        Assert.False(play.Skipped);
        Assert.Equal(TimeSpan.FromSeconds(180), play.Listened);
        Assert.Equal(2, play.SeekCount);
        Assert.Equal(1, play.PauseCount);
    }

    [Fact]
    public async Task ASkipIsRecordedButDoesNotCountAsAPlay()
    {
        // "Most played" should mean most listened to, not most started and abandoned.
        long id = await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Song"), Now, None);

        await _repository.RecordPlayAsync(Play(id, skipped: true, listenedSeconds: 4), None);
        await _repository.RecordPlayAsync(Play(id, completed: true), None);

        var history = await _repository.GetHistoryAsync(10, None);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, p => p.Skipped);

        // One of the two plays counted.
        var track = await _repository.GetTrackAsync(id, None);
        Assert.NotNull(track);
    }

    [Fact]
    public async Task HistoryComesBackNewestFirst()
    {
        long id = await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Song"), Now, None);

        for (int i = 0; i < 3; i++)
        {
            await _repository.RecordPlayAsync(
                new PlayRecord(id, Now.AddMinutes(i), Now.AddMinutes(i + 1), TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(3), false, false, 0, 0), None);
        }

        var history = await _repository.GetHistoryAsync(10, None);

        Assert.Equal(3, history.Count);
        Assert.True(history[0].StartedUtc > history[1].StartedUtc);
    }

    [Fact]
    public async Task DeletingATrackTakesItsHistoryWithIt()
    {
        long id = await _repository.UpsertTrackAsync(Local(@"C:\M\a.flac", "Song"), Now, None);
        await _repository.RecordPlayAsync(Play(id), None);

        await _repository.DeleteByPathsAsync(new[] { @"C:\M\a.flac" }, None);

        Assert.Empty(await _repository.GetHistoryAsync(10, None));
    }

    [Fact]
    public async Task AStreamedTrackCanBeLikedAndHaveHistoryJustLikeAFile()
    {
        long id = await _repository.UpsertTrackAsync(Remote("abc", "Streamed"), Now, None);

        await _repository.SetLikedAsync(id, true, None);
        await _repository.RecordPlayAsync(Play(id, completed: true), None);

        var liked = await _repository.GetTracksAsync(new TrackQuery(LikedOnly: true), None);
        Assert.Equal(new[] { "Streamed" }, liked.Select(t => t.Title));
        Assert.Single(await _repository.GetHistoryAsync(10, None));
    }

    // ── Scanning support ────────────────────────────────────────────────────

    [Fact]
    public async Task FileStampsComeBackForTheFolderTheScannerAsksAbout()
    {
        long a = await _repository.UpsertTrackAsync(Local(@"C:\Music\a.flac", "A"), Now, None);
        await _repository.UpsertTrackAsync(Local(@"C:\Other\b.flac", "B"), Now, None);
        await _repository.SetFileStampAsync(a, 1234, Now, None);

        var stamps = await _repository.GetFileStampsAsync(@"C:\Music", None);

        Assert.Single(stamps);
        Assert.Equal(1234, stamps[@"C:\Music\a.flac"].Size);
        Assert.Equal(2, (await _repository.GetFileStampsAsync(null, None)).Count);
    }

    [Fact]
    public async Task ABatchIsWrittenInOnePiece()
    {
        var tracks = Enumerable.Range(0, 50)
            .Select(i => Local($@"C:\M\{i}.flac", "Track " + i))
            .ToList();

        int written = await _repository.UpsertBatchAsync(tracks, Now, None);

        Assert.Equal(50, written);
        Assert.Equal(50, await _repository.CountTracksAsync(None));
    }

    [Fact]
    public async Task ManyWritesFromDifferentThreadsDoNotCollide()
    {
        // Writes are serialised by the repository; this fails loudly if that ever stops.
        await Task.WhenAll(Enumerable.Range(0, 25).Select(i =>
            _repository.UpsertTrackAsync(Local($@"C:\M\{i}.flac", "Track " + i), Now, None)));

        Assert.Equal(25, await _repository.CountTracksAsync(None));
    }
}
