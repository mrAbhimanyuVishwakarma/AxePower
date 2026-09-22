using System;
using System.IO;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Builds a <see cref="Track"/> for a file that is not in the library (opened directly or
/// dropped). A file whose tags cannot be read is still playable: it gets its file name as the
/// title and "Unknown artist".
/// </summary>
public sealed class LooseTrackReader
{
    public const string UnknownArtist = "Unknown artist";

    private readonly ITagReader _tags;
    private readonly SettingsService? _settings;
    private readonly LogService? _log;

    public LooseTrackReader(ITagReader tags, SettingsService? settings = null, LogService? log = null)
    {
        _tags = tags;
        _settings = settings;
        _log = log;
    }

    public Track Read(string path)
    {
        string fileTitle = Path.GetFileNameWithoutExtension(path);
        bool splitAmpersand = _settings?.GetSettings().Music.SplitArtistsOnAmpersandAndComma ?? false;

        try
        {
            var t = _tags.Read(path);
            var artists = ArtistGenreSplitter.SplitArtists(t.Artists, splitAmpersand);

            return new Track(
                Id: 0,
                Path: path,
                Title: t.Title ?? fileTitle,
                Artists: artists.Count > 0 ? artists : new[] { UnknownArtist },
                Album: t.Album,
                AlbumArtist: t.AlbumArtist,
                TrackNumber: t.TrackNumber,
                DiscNumber: t.DiscNumber,
                Year: t.Year,
                Genres: ArtistGenreSplitter.SplitGenres(t.Genres),
                Duration: t.Duration,
                Format: t.Format,
                ReplayGain: t.ReplayGain,
                CoverKey: null,
                Liked: false);
        }
        catch (TagReadException ex)
        {
            // Log only the file name, never the full personal path
            _log?.LogWarning($"[Music] Tags unreadable for '{Path.GetFileName(path)}': {ex.Message}");
            return new Track(0, path, fileTitle, new[] { UnknownArtist }, null, null, null, null, null,
                Array.Empty<string>(), TimeSpan.Zero, new AudioFormatInfo(Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), 0, 0, 0, 0, false),
                null, null, false);
        }
    }
}
