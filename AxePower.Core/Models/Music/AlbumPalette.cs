using System;

namespace AxePower.Core.Models.Music;

/// <summary>A UI-framework-neutral RGB colour sampled from album art.</summary>
public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public double Luminance => (0.2126 * Red + 0.7152 * Green + 0.0722 * Blue) / 255d;
}

/// <summary>Vibrant colours used by the ambient player.</summary>
public sealed record AlbumPalette(RgbColor Primary, RgbColor Secondary, RgbColor BackgroundGlow)
{
    public static AlbumPalette Default { get; } = new(
        new RgbColor(80, 101, 214),
        new RgbColor(126, 63, 180),
        new RgbColor(20, 28, 58));
}

