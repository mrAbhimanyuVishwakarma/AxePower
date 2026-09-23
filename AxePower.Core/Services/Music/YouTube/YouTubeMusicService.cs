using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;
using YoutubeExplode.Videos.Streams;

namespace AxePower.Core.Services.Music;

public sealed record YouTubeTrackInfo(
    string VideoId,
    string Title,
    string Artist,
    TimeSpan? Duration,
    string ThumbnailUrl);

/// <summary>
/// Free in-app YouTube Music streaming and search without a browser.
/// Uses YoutubeExplode to search tracks and stream audio directly into the NAudio player.
/// </summary>
public class YouTubeMusicService
{
    private readonly YoutubeClient _youtube = new();
    private readonly string _cacheDir;

    public YouTubeMusicService()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "AxePower", "yt_music_cache");
        try
        {
            if (!Directory.Exists(_cacheDir))
                Directory.CreateDirectory(_cacheDir);
        }
        catch { }
    }

    /// <summary>
    /// Searches YouTube Music tracks.
    /// </summary>
    public async Task<List<YouTubeTrackInfo>> SearchTracksAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        var results = new List<YouTubeTrackInfo>();
        try
        {
            string searchQuery = query.Contains("music", StringComparison.OrdinalIgnoreCase) || query.Contains("song", StringComparison.OrdinalIgnoreCase)
                ? query
                : $"{query} song";

            await foreach (var video in _youtube.Search.GetVideosAsync(searchQuery, ct))
            {
                if (results.Count >= limit) break;
                string title = video.Title;
                string artist = video.Author.ChannelTitle;

                // If title contains "Artist - Title", clean it up
                int sep = title.IndexOf(" - ", StringComparison.Ordinal);
                if (sep > 0 && artist.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
                {
                    artist = title[..sep].Trim();
                    title = title[(sep + 3)..].Trim();
                }

                results.Add(new YouTubeTrackInfo(
                    VideoId: video.Id.Value,
                    Title: title,
                    Artist: artist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim(),
                    Duration: video.Duration,
                    ThumbnailUrl: video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? string.Empty
                ));
            }
        }
        catch { }

        return results;
    }

    /// <summary>
    /// Retrieves or downloads the audio stream for a YouTube track into a local cached audio file.
    /// Once cached, NAudio plays it natively with gapless seeking, volume, and visualizer.
    /// </summary>
    public async Task<string?> GetOrDownloadAudioStreamAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            string cachedFile = Path.Combine(_cacheDir, $"{videoId}.m4a");
            if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 10000)
                return cachedFile;

            var manifest = await _youtube.Videos.Streams.GetManifestAsync(videoId, ct);
            var audioStream = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .FirstOrDefault();

            if (audioStream == null)
                return null;

            string tempFile = cachedFile + ".part";
            await _youtube.Videos.Streams.DownloadAsync(audioStream, tempFile, cancellationToken: ct);

            if (File.Exists(cachedFile)) File.Delete(cachedFile);
            File.Move(tempFile, cachedFile);

            return cachedFile;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
