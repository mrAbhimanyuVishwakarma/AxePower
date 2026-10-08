using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AxePower.Core.Services.Skins;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// Every shipped skin keeps its text readable (Prompt 28): WCAG AA, 4.5:1, for body, secondary,
/// muted and status text on the window, cards and raised cards, and for the primary button's text.
/// Disabled text is exempt, as WCAG allows. Translucent surfaces are measured the way the app makes
/// its pop-ups opaque: laid over the window colour, and that over black (dark) or white (light).
/// For acrylic skins the real backdrop is the blurred desktop, so the owner still checks them by eye.
/// </summary>
public class SkinContrastTests
{
    private const double AA = 4.5;

    private static readonly (string Text, string Name)[] TextColours =
    {
        ("PrimaryTextBrush", "primary text"), ("SecondaryTextBrush", "secondary text"), ("MutedTextBrush", "muted text"),
        ("SuccessBrush", "success"), ("WarningBrush", "warning"), ("ErrorBrush", "error"), ("AccentBrush", "accent"),
    };

    private static readonly (string Surface, string Name)[] Surfaces =
    {
        ("WindowBackgroundColor", "window"), ("CardBackgroundColor", "card"), ("ElevatedCardBackgroundColor", "raised card"),
    };

    /// <summary>Every skin in the repo's skins\ folder, shipped or not yet, so a later ship is already checked.</summary>
    private static IReadOnlyList<SkinEntry> ShippedSkins()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AxePower.sln"))) dir = dir.Parent;
        var folder = Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("AxePower.sln not found"), "skins");
        var catalog = new SkinCatalog(null, new[] { folder });
        Assert.Empty(catalog.Errors);
        return catalog.Skins;
    }

    public static double Ratio(string a, string b)
    {
        double la = Hex.Luminance(a), lb = Hex.Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Argb(CompiledSkin skin, string key) => skin.Find(key) switch
    {
        ColorResource c => c.Argb,
        SolidBrushResource b => b.Argb,
        var other => throw new InvalidOperationException($"{skin.Id}: {key} is {other?.GetType().Name ?? "missing"}, not a solid colour."),
    };

    [Fact]
    public void AllShippedSkinsAreChecked() => Assert.True(ShippedSkins().Count >= 40, $"Only {ShippedSkins().Count} skins loaded.");

    [Theory]
    [InlineData("#FF000000", "#FFFFFFFF", 21.0)]
    [InlineData("#FF777777", "#FFFFFFFF", 4.48)]
    [InlineData("#FF767676", "#FFFFFFFF", 4.54)]
    public void Ratio_MatchesWcag(string a, string b, double expected) => Assert.Equal(expected, Ratio(a, b), 2);

    [Fact]
    public void TextAndStatusColours_Reach4Point5ToOne_OnEverySurface_InEverySkin()
    {
        var failures = new List<string>();
        foreach (var entry in ShippedSkins())
        {
            var skin = entry.Compiled;
            string floor = skin.IsLight ? "#FFFFFFFF" : "#FF000000";
            string window = Hex.Over(Argb(skin, "WindowBackgroundColor"), floor);
            foreach (var (surfaceKey, surfaceName) in Surfaces)
            {
                string surface = surfaceKey == "WindowBackgroundColor" ? window : Hex.Over(Argb(skin, surfaceKey), window);
                foreach (var (textKey, textName) in TextColours)
                {
                    double ratio = Ratio(Hex.Over(Argb(skin, textKey), surface), surface);
                    if (ratio < AA) failures.Add($"{skin.Id}: {textName} on {surfaceName} is {ratio:0.00}:1");
                }
            }
        }
        Assert.True(failures.Count == 0, "Below 4.5:1 (fix the colour in skins/<id>/skin.json):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void PrimaryButtonText_Reaches4Point5ToOne_InEverySkin()
    {
        var failures = ShippedSkins()
            .Select(e => e.Compiled)
            .Select(s => (s.Id, Ratio(Argb(s, "CtaButtonForegroundBrush"), Argb(s, "CtaButtonBackgroundBrush"))))
            .Where(x => x.Item2 < AA)
            .Select(x => $"{x.Id}: {x.Item2:0.00}:1")
            .ToList();
        Assert.True(failures.Count == 0, "Primary button text below 4.5:1:\n" + string.Join("\n", failures));
    }
}
