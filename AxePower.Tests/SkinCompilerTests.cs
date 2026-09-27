using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AxePower.Core.Models.Skins;
using AxePower.Core.Services.Skins;

namespace AxePower.Tests;

/// <summary>
/// The skin format replaced three hand-written theme dictionaries. These tests prove the built-in
/// skins reproduce them resource for resource, and cover what a new skin can and cannot do.
/// </summary>
public class SkinCompilerTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"axepower_skins_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch { }
    }

    /// <summary>skin id → resource → expected value, for changes made after the theme files were retired.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> DeliberateChanges = new()
    {
        // 2026-09-25: #E2E8F0 card borders vanished against the #F1F5F9 window
        ["light"] = new()
        {
            ["CardBorderColor"] = "Color #FFCBD5E1",
            ["CardBorderBrush"] = "Solid #FFCBD5E1",
            ["PopupBackgroundBrush"] = "Solid #FFFFFFFF",
            // 2026-09-27: text on accent buttons comes from the skin; the light blue accent keeps white
            ["AccentForegroundBrush"] = "Solid #FFFFFFFF",
        },
        // 2026-09-25: an opaque surface for dropdowns, which on glass showed the page through them
        // 2026-09-27: tray menu text is pure white on dark skins (owner's request)
        ["dark"] = new() { ["PopupBackgroundBrush"] = "Solid #FF1A1C23", ["TrayMenuForegroundBrush"] = "Solid #FFFFFFFF", ["AccentForegroundBrush"] = "Solid #FF000000" },
        ["glass"] = new() { ["PopupBackgroundBrush"] = "Solid #FF030405", ["TrayMenuForegroundBrush"] = "Solid #FFFFFFFF", ["AccentForegroundBrush"] = "Solid #FF000000" },
    };

    [Theory]
    [InlineData("dark", "DarkTheme.xaml")]
    [InlineData("light", "LightTheme.xaml")]
    [InlineData("glass", "GlassTheme.xaml")]
    public void BuiltInSkin_ReproducesTheThemeItReplaced_ResourceForResource(string id, string legacyFile)
    {
        var expected = ReadLegacy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyThemes", legacyFile));
        var actual = Describe(new SkinCatalog(null, Array.Empty<string>()).Find(id)!.Compiled);

        // Changes made on purpose after the move to skins; anything else that differs is a regression
        foreach (var (key, value) in DeliberateChanges.GetValueOrDefault(id, new()))
            expected[key] = value;

        var differences = expected.Keys.Union(actual.Keys)
            .Where(k => !expected.TryGetValue(k, out var e) || !actual.TryGetValue(k, out var a) || e != a)
            .Select(k => $"{k}: theme file {(expected.TryGetValue(k, out var e) ? e : "(none)")} / skin {(actual.TryGetValue(k, out var a) ? a : "(none)")}")
            .ToList();

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
        Assert.Equal(63, actual.Count);
    }

    [Theory]
    [InlineData("#38BDF8", "#FF000000")]   // glass: light blue
    [InlineData("#FCEE0A", "#FF000000")]   // neon-city: yellow, where white was unreadable
    [InlineData("#A855F7", "#FF000000")]   // royal-amethyst
    [InlineData("#2563EB", "#FFFFFFFF")]   // light: dark blue keeps white
    [InlineData("#B91C1C", "#FFFFFFFF")]   // paper-and-ink: deep red keeps white
    public void AccentButtonText_IsBlack_UnlessTheAccentIsTooDark(string accent, string text) =>
        Assert.Equal(text, Hex.TextOn(accent));

    [Fact]
    public void AnAccentOnlySkin_TakesEverythingElseFromItsBase()
    {
        var entry = LoadFolderSkin("ocean", """{ "id": "ocean", "name": "Ocean", "base": "dark", "colors": { "accent": "#06B6D4" } }""");

        Assert.Equal("#FF06B6D4", Solid(entry, "AccentBrush"));
        Assert.Equal("#FF06B6D4", Solid(entry, "MusicAccentBrush"));
        Assert.Equal("#FF14161C", Solid(entry, "CardBackgroundBrush"));
        Assert.False(entry.Compiled.IsLight);
    }

    [Fact]
    public void ALightBasedSkin_UsesTheLightLogoTrayMenuAndTitleBar()
    {
        var entry = LoadFolderSkin("paper", """{ "id": "paper", "name": "Paper", "base": "light", "colors": { "window": "#FAF7F0" } }""");

        Assert.True(entry.Compiled.IsLight);
        Assert.Equal("/Resources/AxePower_black.ico", ((ImageResource)entry.Compiled.Find("AppIconImage")!).UriSource);
        Assert.Equal("#FFFFFFFF", Solid(entry, "TrayMenuBackgroundBrush"));
        Assert.Equal("#FFFAF7F0", Solid(entry, "WindowBackgroundBrush"));
    }

    [Fact]
    public void GlassSwitches_TurnSurfacesIntoGradientsAndAddTheShadow()
    {
        var entry = LoadFolderSkin("frost", """
            { "id": "frost", "name": "Frost", "base": "dark",
              "glass": { "backdrop": "acrylic", "sheen": 100, "rim": true, "shadow": true } }
            """);

        Assert.True(entry.Compiled.UsesAcrylic);
        Assert.IsType<GradientBrushResource>(entry.Compiled.Find("CardBackgroundBrush"));
        Assert.IsType<GradientBrushResource>(entry.Compiled.Find("CardBorderBrush"));
        Assert.IsType<ShadowResource>(entry.Compiled.Find("CardShadowEffect"));
        Assert.Equal("#00000000", ((ColorResource)entry.Compiled.Find("TitleBarBackgroundColor")!).Argb);
    }

    [Fact]
    public void Overrides_ReplaceTheDerivedValue()
    {
        var entry = LoadFolderSkin("custom", """{ "id": "custom", "name": "Custom", "base": "dark", "overrides": { "AccentHoverBrush": "#123" } }""");

        Assert.Equal("#FF112233", Solid(entry, "AccentHoverBrush"));
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id", "name": "x" }""", "lower-case")]
    [InlineData("""{ "id": "bad", "name": "x", "base": "sepia" }""", "base")]
    [InlineData("""{ "id": "bad", "name": "x", "colors": { "accent": "blue" } }""", "hex")]
    [InlineData("""{ "id": "bad", "name": "x", "overrides": { "NoSuchBrush": "#FFF" } }""", "no theme resource")]
    [InlineData("""{ "id": "bad", "name": "x", "glass": { "backdrop": "mica" } }""", "backdrop")]
    public void ABrokenSkin_IsSkipped_WithAReasonAPersonCanAct_On(string json, string reasonContains)
    {
        Directory.CreateDirectory(Path.Combine(_folder, "broken"));
        File.WriteAllText(Path.Combine(_folder, "broken", "skin.json"), json);

        var catalog = new SkinCatalog(null, new[] { _folder });

        Assert.Equal(SkinCatalog.BuiltInIds.Length, catalog.Skins.Count);
        Assert.Contains(reasonContains, Assert.Single(catalog.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FoldersStartingWithAnUnderscore_AndCopiesOfBuiltIns_AreIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "_template"));
        File.WriteAllText(Path.Combine(_folder, "_template", "skin.json"), """{ "id": "template", "name": "Template" }""");
        Directory.CreateDirectory(Path.Combine(_folder, "dark"));
        File.WriteAllText(Path.Combine(_folder, "dark", "skin.json"), """{ "id": "dark", "name": "Dark copy" }""");

        var catalog = new SkinCatalog(null, new[] { _folder });

        Assert.Equal(new[] { "dark", "light", "glass" }, catalog.Skins.Select(s => s.Id));
        Assert.Equal("Dark", catalog.Find("dark")!.Name);
        Assert.Empty(catalog.Errors);
    }

    [Fact]
    public void TheShippedTemplate_IsAValidSkin()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "skins", "_template", "skin.json");
        var def = SkinCatalog.Read(File.ReadAllText(file), file);
        var dark = new SkinCatalog(null, Array.Empty<string>()).Find("dark")!.Definition;

        var compiled = SkinCompiler.Compile(def, dark);

        Assert.Equal(63, compiled.Resources.Count);
    }

    [Fact]
    public void AnUnknownSkinId_ResolvesToTheDefault()
    {
        Assert.Equal("dark", new SkinCatalog(null, Array.Empty<string>()).Resolve("deleted-skin").Id);
    }

    private SkinEntry LoadFolderSkin(string id, string json)
    {
        Directory.CreateDirectory(Path.Combine(_folder, id));
        File.WriteAllText(Path.Combine(_folder, id, "skin.json"), json);
        var catalog = new SkinCatalog(null, new[] { _folder });
        Assert.Empty(catalog.Errors);
        return catalog.Find(id)!;
    }

    private static string Solid(SkinEntry entry, string key) => ((SolidBrushResource)entry.Compiled.Find(key)!).Argb;

    // ----- one comparable string per resource, from either side -----

    private static Dictionary<string, string> Describe(CompiledSkin skin) =>
        skin.Resources.ToDictionary(r => r.Key, r => r switch
        {
            ColorResource c => $"Color {c.Argb}",
            SolidBrushResource s => $"Solid {s.Argb}",
            GradientBrushResource g => Gradient(g.StartX, g.StartY, g.EndX, g.EndY, g.Stops.Select(s => (s.Argb, s.Offset))),
            ShadowResource d => Shadow(d.BlurRadius, d.ShadowDepth, d.Direction, d.Opacity, d.Argb),
            NullResource => "Null",
            ImageResource i => $"Image {i.UriSource}",
            _ => throw new InvalidOperationException(r.GetType().Name)
        });

    private static Dictionary<string, string> ReadLegacy(string path)
    {
        var root = XDocument.Load(path).Root!;
        var colors = root.Elements().Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => Norm(e.Value));
        string ColorOf(string value) =>
            value.StartsWith("{StaticResource ") ? colors[value["{StaticResource ".Length..].TrimEnd('}').Trim()] : Norm(value);

        var result = new Dictionary<string, string>();
        foreach (var e in root.Elements())
        {
            var key = (string?)e.Attribute(X + "Key");
            if (key == null) continue;
            result[key] = e.Name.LocalName switch
            {
                "Color" => $"Color {colors[key]}",
                "SolidColorBrush" => $"Solid {ColorOf((string)e.Attribute("Color")!)}",
                "LinearGradientBrush" => Gradient(Point(e, "StartPoint").x, Point(e, "StartPoint").y, Point(e, "EndPoint").x, Point(e, "EndPoint").y,
                    e.Elements().Where(s => s.Name.LocalName == "GradientStop")
                        .Select(s => (ColorOf((string)s.Attribute("Color")!), D((string)s.Attribute("Offset")!)))),
                "DropShadowEffect" => Shadow(D((string)e.Attribute("BlurRadius")!), D((string)e.Attribute("ShadowDepth")!),
                    D((string)e.Attribute("Direction")!), D((string)e.Attribute("Opacity")!), Norm((string)e.Attribute("Color")!)),
                "Null" => "Null",
                "BitmapImage" => $"Image {(string)e.Attribute("UriSource")!}",
                var other => throw new InvalidOperationException($"{key}: {other}")
            };
        }
        return result;
    }

    private static (double x, double y) Point(XElement e, string name)
    {
        var parts = ((string)e.Attribute(name)!).Split(',');
        return (D(parts[0]), D(parts[1]));
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    private static string Norm(string hex) => Hex.Normalize(hex);
    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Gradient(double sx, double sy, double ex, double ey, IEnumerable<(string Argb, double Offset)> stops) =>
        $"Gradient {F(sx)},{F(sy)}>{F(ex)},{F(ey)} " + string.Join(" ", stops.Select(s => $"{s.Argb}@{F(s.Offset)}"));

    private static string Shadow(double blur, double depth, double dir, double opacity, string argb) =>
        $"Shadow {F(blur)} {F(depth)} {F(dir)} {F(opacity)} {argb}";
}
