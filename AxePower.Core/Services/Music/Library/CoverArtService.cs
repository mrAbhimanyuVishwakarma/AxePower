using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Finds a track's cover — embedded image first, then cover/folder/front/album.jpg|png in its
/// folder — and caches it as JPEG at 128, 256 and 512 px under Music\covers. Resizing uses
/// Windows imaging (no WPF in Core). Covers are never uploaded anywhere.
/// </summary>
public sealed class CoverArtService : ICoverArtService
{
    private const int MaxRemoteArtworkBytes = 10 * 1024 * 1024;
    private static readonly int[] Sizes = { 128, 256, 512 };
    private static readonly string[] FolderImageNames = { "cover", "folder", "front", "album" };
    private static readonly string[] FolderImageExtensions = { ".jpg", ".jpeg", ".png" };
    private static readonly HttpClient ArtworkClient = CreateArtworkClient();

    private readonly ITagReader _tags;
    private readonly MusicStoragePaths _paths;
    private readonly LogService? _log;

    // Track path → cover key ("" = no cover), so embedded art is hashed once per session
    private readonly ConcurrentDictionary<string, string> _keyByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _generate = new(2, 2);

    public CoverArtService(ITagReader tags, MusicStoragePaths paths, LogService? log = null)
    {
        _tags = tags;
        _paths = paths;
        _log = log;
    }

    public async Task<string?> GetCoverFileAsync(Track track, int size, CancellationToken ct)
    {
        size = Sizes.FirstOrDefault(s => s >= size, 512);

        try
        {
            if (!track.IsLocal && Uri.TryCreate(track.ThumbnailUrl, UriKind.Absolute, out var thumbnailUri)
                && (thumbnailUri.Scheme == Uri.UriSchemeHttps || thumbnailUri.Scheme == Uri.UriSchemeHttp))
            {
                return await GetRemoteCoverAsync(thumbnailUri, size, ct);
            }

            var (key, loadBytes) = ResolveSource(track);
            if (key == null) return null;

            string target = Path.Combine(_paths.CoversDirectory, $"{key}_{size}.jpg");
            if (File.Exists(target)) return target;

            await _generate.WaitAsync(ct);
            try
            {
                if (File.Exists(target)) return target;

                var bytes = loadBytes();
                if (bytes == null || bytes.Length == 0) return null;

                Directory.CreateDirectory(_paths.CoversDirectory);
                await WriteResizedJpegAsync(bytes, size, target, ct);
                return target;
            }
            finally
            {
                _generate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[Music] Cover art unavailable for '{track.DisplayName}': {ex.Message}");
            return null;
        }
    }

    private async Task<string?> GetRemoteCoverAsync(Uri uri, int size, CancellationToken ct)
    {
        string key = Sha1(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
        string target = Path.Combine(_paths.CoversDirectory, $"{key}_{size}.jpg");
        if (File.Exists(target)) return target;

        await _generate.WaitAsync(ct);
        try
        {
            if (File.Exists(target)) return target;

            using var response = await ArtworkClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxRemoteArtworkBytes)
                throw new InvalidDataException("Remote artwork exceeds the 10 MB safety limit.");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;
                total += read;
                if (total > MaxRemoteArtworkBytes)
                    throw new InvalidDataException("Remote artwork exceeds the 10 MB safety limit.");
                await bytes.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            if (bytes.Length == 0) return null;
            Directory.CreateDirectory(_paths.CoversDirectory);
            await WriteResizedJpegAsync(bytes.ToArray(), size, target, ct);
            return target;
        }
        finally
        {
            _generate.Release();
        }
    }

    private static HttpClient CreateArtworkClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AxePower/2.0.9");
        return client;
    }

    /// <summary>Accent from artwork arrives in Phase 3.</summary>
    public Task<(byte R, byte G, byte B)?> GetDominantColorAsync(Track track, CancellationToken ct) =>
        Task.FromResult<(byte, byte, byte)?>(null);

    /// <summary>
    /// The cover key is the SHA-1 of the embedded image bytes, or of the lower-cased path of the
    /// folder image. Returns a loader for the original image bytes.
    /// </summary>
    private (string? Key, Func<byte[]?> LoadBytes) ResolveSource(Track track)
    {
        if (!string.IsNullOrEmpty(track.CoverKey))
            return (track.CoverKey, () => LoadOriginal(track.Path));

        if (_keyByPath.TryGetValue(track.Path, out var cached))
            return (cached.Length == 0 ? null : cached, () => LoadOriginal(track.Path));

        var embedded = _tags.ReadEmbeddedCover(track.Path);
        if (embedded != null)
        {
            string key = Sha1(embedded);
            _keyByPath[track.Path] = key;
            return (key, () => embedded);
        }

        var folderImage = FindFolderImage(track.Path);
        if (folderImage != null)
        {
            string key = Sha1(Encoding.UTF8.GetBytes(folderImage.ToLowerInvariant()));
            _keyByPath[track.Path] = key;
            return (key, () => File.ReadAllBytes(folderImage));
        }

        _keyByPath[track.Path] = string.Empty;
        return (null, () => null);
    }

    private byte[]? LoadOriginal(string trackPath)
    {
        var embedded = _tags.ReadEmbeddedCover(trackPath);
        if (embedded != null) return embedded;

        var folderImage = FindFolderImage(trackPath);
        return folderImage != null ? File.ReadAllBytes(folderImage) : null;
    }

    /// <summary>First match in name order: cover, folder, front, album (jpg before png).</summary>
    public static string? FindFolderImage(string trackPath)
    {
        string? folder = Path.GetDirectoryName(trackPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

        var files = new HashSet<string>(
            Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(n => n != null)!,
            StringComparer.OrdinalIgnoreCase);

        foreach (var name in FolderImageNames)
        {
            foreach (var ext in FolderImageExtensions)
            {
                string candidate = name + ext;
                if (files.Contains(candidate)) return Path.Combine(folder, candidate);
            }
        }
        return null;
    }

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    private static async Task WriteResizedJpegAsync(byte[] image, int size, string target, CancellationToken ct)
    {
        using var input = new MemoryStream(image).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct);

        double scale = Math.Min(1.0, (double)size / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);

        using var output = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties).AsTask(ct);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            transform.ScaledWidth, transform.ScaledHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(ct);

        // Write to a temp file first so a half-written cover is never picked up
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        output.Seek(0);
        using (var source = output.AsStreamForRead())
        using (var file = File.Create(temp))
        {
            await source.CopyToAsync(file, ct);
        }

        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
