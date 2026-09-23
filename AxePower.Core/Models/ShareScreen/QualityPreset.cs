using System.Collections.Generic;
using System.Linq;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// The encoder settings behind one <see cref="ShareQuality"/> rung. Bitrates come from the
/// blueprint's profile table; <see cref="MinBitrateKbps"/>/<see cref="MaxBitrateKbps"/> bound the
/// VBV so a scene change cannot spike the link into loss.
/// </summary>
public sealed record QualityPreset(
    ShareQuality Quality,
    string DisplayName,
    int Width,
    int Height,
    int Fps,
    int TargetBitrateKbps,
    int MinBitrateKbps,
    int MaxBitrateKbps,
    int AudioBitrateKbps)
{
    /// <summary>Short label for the stats line, e.g. "1080p60".</summary>
    public string ShortLabel => $"{Height}p{Fps}";

    public static readonly QualityPreset Smooth720 = new(
        ShareQuality.Smooth720, "Smooth — 720p60", 1280, 720, 60, 8_000, 4_000, 12_000, 128);

    public static readonly QualityPreset Balanced1080 = new(
        ShareQuality.Balanced1080, "Balanced — 1080p60", 1920, 1080, 60, 16_000, 8_000, 22_000, 160);

    public static readonly QualityPreset High1080 = new(
        ShareQuality.High1080, "High — 1080p60", 1920, 1080, 60, 22_000, 12_000, 30_000, 192);

    public static readonly QualityPreset Quad1440 = new(
        ShareQuality.Quad1440, "Sharp — 1440p60", 2560, 1440, 60, 28_000, 16_000, 40_000, 192);

    public static readonly QualityPreset Ultra2160 = new(
        ShareQuality.Ultra2160, "Ultra — 4K60", 3840, 2160, 60, 45_000, 30_000, 70_000, 192);

    public static IReadOnlyList<QualityPreset> All { get; } = new[]
    {
        Smooth720, Balanced1080, High1080, Quad1440, Ultra2160
    };

    public static QualityPreset For(ShareQuality quality) =>
        All.FirstOrDefault(p => p.Quality == quality) ?? Balanced1080;

    /// <summary>
    /// The next rung down, or null at the bottom. Adaptive quality walks this ladder rather than
    /// cutting frame rate, because for games motion smoothness is the thing being sold.
    /// </summary>
    public QualityPreset? StepDown() => Quality switch
    {
        ShareQuality.Ultra2160 => Quad1440,
        ShareQuality.Quad1440 => Balanced1080,
        ShareQuality.High1080 => Balanced1080,
        ShareQuality.Balanced1080 => Smooth720,
        _ => null
    };
}
