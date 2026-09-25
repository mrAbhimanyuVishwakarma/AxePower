using System.Collections.Generic;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

public sealed class PaletteQuantizerTests
{
    [Fact]
    public void Extract_PrefersVibrantArtworkColoursAndDarkensTheGlow()
    {
        var pixels = new List<RgbColor>();
        pixels.AddRange(System.Linq.Enumerable.Repeat(new RgbColor(235, 35, 75), 80));
        pixels.AddRange(System.Linq.Enumerable.Repeat(new RgbColor(30, 90, 230), 50));
        pixels.AddRange(System.Linq.Enumerable.Repeat(new RgbColor(255, 255, 255), 100));

        var palette = PaletteQuantizer.Extract(pixels);

        Assert.True(palette.Primary.Red > palette.Primary.Blue);
        Assert.True(palette.Secondary.Blue > palette.Secondary.Red);
        Assert.True(palette.BackgroundGlow.Luminance < palette.Primary.Luminance);
    }

    [Fact]
    public void Extract_UsesSafeDefaultForMonochromeArtwork()
    {
        var palette = PaletteQuantizer.Extract(new[] { new RgbColor(0, 0, 0), new RgbColor(255, 255, 255) });

        Assert.Equal(AlbumPalette.Default, palette);
    }
}

