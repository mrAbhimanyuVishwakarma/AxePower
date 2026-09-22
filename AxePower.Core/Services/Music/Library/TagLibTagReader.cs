using System;
using System.IO;
using System.Linq;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Reads tags, covers and lyrics with TagLib#. Never writes to the file.</summary>
public sealed class TagLibTagReader : ITagReader
{
    public TrackTags Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            var props = file.Properties;
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            string codec = DescribeCodec(ext, props?.Description);
            int bits = props?.BitsPerSample ?? 0;
            bool lossless = codec is "FLAC" or "WAV" or "AIFF" or "ALAC";

            var format = new AudioFormatInfo(
                codec,
                props?.AudioSampleRate ?? 0,
                bits,
                props?.AudioChannels ?? 0,
                props?.AudioBitrate ?? 0,
                lossless);

            return new TrackTags(
                Title: NullIfBlank(tag.Title),
                Artists: JoinOrNull(tag.Performers),
                AlbumArtist: JoinOrNull(tag.AlbumArtists),
                Album: NullIfBlank(tag.Album),
                TrackNumber: tag.Track > 0 ? (int)tag.Track : null,
                DiscNumber: tag.Disc > 0 ? (int)tag.Disc : null,
                Year: tag.Year > 0 ? (int)tag.Year : null,
                Genres: JoinOrNull(tag.Genres),
                Duration: props?.Duration ?? TimeSpan.Zero,
                Format: format,
                ReplayGain: ReadReplayGain(tag),
                HasEmbeddedCover: tag.Pictures?.Length > 0,
                HasEmbeddedLyrics: !string.IsNullOrWhiteSpace(tag.Lyrics));
        }
        catch (Exception ex) when (ex is not TagReadException)
        {
            throw new TagReadException($"Could not read tags: {ex.Message}", ex);
        }
    }

    public byte[]? ReadEmbeddedCover(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var pictures = file.Tag.Pictures;
            if (pictures == null || pictures.Length == 0) return null;

            var front = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
            return front.Data?.Data is { Length: > 0 } data ? data : null;
        }
        catch
        {
            return null;
        }
    }

    public string? ReadEmbeddedLyrics(string path)
    {
        // Synced (SYLT → LRC) lyrics arrive with the lyrics feature in Phase 6
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            return NullIfBlank(file.Tag.Lyrics);
        }
        catch
        {
            return null;
        }
    }

    private static ReplayGainInfo? ReadReplayGain(TagLib.Tag tag)
    {
        double? Value(double v) => double.IsNaN(v) ? null : v;

        var info = new ReplayGainInfo(
            Value(tag.ReplayGainTrackGain),
            Value(tag.ReplayGainTrackPeak),
            Value(tag.ReplayGainAlbumGain),
            Value(tag.ReplayGainAlbumPeak));

        return info.TrackGainDb == null && info.AlbumGainDb == null ? null : info;
    }

    private static string DescribeCodec(string ext, string? description)
    {
        if (!string.IsNullOrEmpty(description) && description.Contains("ALAC", StringComparison.OrdinalIgnoreCase)) return "ALAC";

        return ext switch
        {
            "mp3" => "MP3",
            "flac" => "FLAC",
            "wav" => "WAV",
            "aif" or "aiff" => "AIFF",
            "wma" => "WMA",
            "ogg" or "oga" => description?.Contains("Opus", StringComparison.OrdinalIgnoreCase) == true ? "Opus" : "Vorbis",
            "opus" => "Opus",
            "m4a" or "mp4" or "aac" => "AAC",
            _ => ext.ToUpperInvariant()
        };
    }

    private static string? JoinOrNull(string[]? values)
    {
        var parts = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();
        return parts is { Length: > 0 } ? string.Join("; ", parts) : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
