using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Small, deterministic colour quantizer used after artwork has been downsampled.</summary>
public static class PaletteQuantizer
{
    public static AlbumPalette Extract(IEnumerable<RgbColor> pixels)
    {
        var bins = pixels
            .Where(IsUseful)
            .GroupBy(Quantize)
            .Select(group => new ScoredColor(Average(group), group.Sum(Weight)))
            .OrderByDescending(item => item.Score)
            .Take(24)
            .ToArray();

        if (bins.Length == 0) return AlbumPalette.Default;

        var primary = bins[0].Color;
        var secondary = bins
            .Skip(1)
            .OrderByDescending(item => item.Score * (0.35 + Distance(primary, item.Color)))
            .FirstOrDefault()?.Color ?? Complement(primary);

        var glowSource = bins
            .OrderByDescending(item => Saturation(item.Color) * (0.4 + item.Score))
            .First().Color;

        return new AlbumPalette(primary, secondary, Darken(glowSource, 0.34));
    }

    private static bool IsUseful(RgbColor color)
    {
        double luminance = color.Luminance;
        return luminance is > 0.035 and < 0.96 && Saturation(color) > 0.12;
    }

    private static int Quantize(RgbColor color) =>
        (color.Red >> 4) << 8 | (color.Green >> 4) << 4 | (color.Blue >> 4);

    private static double Weight(RgbColor color) => 0.25 + Saturation(color) * 1.5 + color.Luminance * 0.35;

    private static RgbColor Average(IEnumerable<RgbColor> colors)
    {
        var array = colors.ToArray();
        return new RgbColor(
            (byte)array.Average(color => color.Red),
            (byte)array.Average(color => color.Green),
            (byte)array.Average(color => color.Blue));
    }

    private static double Saturation(RgbColor color)
    {
        double max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        double min = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        return max <= 0 ? 0 : (max - min) / max;
    }

    private static double Distance(RgbColor left, RgbColor right)
    {
        double red = (left.Red - right.Red) / 255d;
        double green = (left.Green - right.Green) / 255d;
        double blue = (left.Blue - right.Blue) / 255d;
        return Math.Sqrt(red * red + green * green + blue * blue) / Math.Sqrt(3);
    }

    private static RgbColor Complement(RgbColor color) =>
        new((byte)(255 - color.Red), (byte)(255 - color.Green), (byte)(255 - color.Blue));

    private static RgbColor Darken(RgbColor color, double factor) => new(
        (byte)Math.Clamp(color.Red * factor, 0, 255),
        (byte)Math.Clamp(color.Green * factor, 0, 255),
        (byte)Math.Clamp(color.Blue * factor, 0, 255));

    private sealed record ScoredColor(RgbColor Color, double Score);
}

