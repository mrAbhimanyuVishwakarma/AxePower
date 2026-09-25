using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;

namespace AxePower.App.Services;

/// <summary>Loads and downsamples artwork with WPF; colour selection remains in Core.</summary>
public sealed class MusicPaletteExtractor : IPaletteExtractor
{
    private const int SampleSize = 32;

    public Task<AlbumPalette> ExtractPaletteAsync(string imagePath, CancellationToken ct = default) =>
        Task.Run(() => Extract(imagePath, ct), ct);

    private static AlbumPalette Extract(string imagePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return AlbumPalette.Default;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = SampleSize;
        image.UriSource = new Uri(Path.GetFullPath(imagePath), UriKind.Absolute);
        image.EndInit();
        image.Freeze();

        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int stride = source.PixelWidth * 4;
        var bytes = new byte[stride * source.PixelHeight];
        source.CopyPixels(bytes, stride, 0);

        var pixels = new List<RgbColor>(source.PixelWidth * source.PixelHeight);
        for (int index = 0; index < bytes.Length; index += 4)
        {
            ct.ThrowIfCancellationRequested();
            if (bytes[index + 3] < 128) continue;
            pixels.Add(new RgbColor(bytes[index + 2], bytes[index + 1], bytes[index]));
        }

        return PaletteQuantizer.Extract(pixels);
    }
}

