using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AxePower.Core.Models.Skins;

namespace AxePower.Core.Services.Skins;

/// <summary>A theme resource, without any WPF types, so it can be built and checked in Core.</summary>
public abstract record SkinResource(string Key);
public sealed record ColorResource(string Key, string Argb) : SkinResource(Key);
public sealed record SolidBrushResource(string Key, string Argb) : SkinResource(Key);
public sealed record GradientStopValue(string Argb, double Offset);
public sealed record GradientBrushResource(string Key, double StartX, double StartY, double EndX, double EndY,
    IReadOnlyList<GradientStopValue> Stops) : SkinResource(Key);
public sealed record ShadowResource(string Key, double BlurRadius, double ShadowDepth, double Direction, double Opacity, string Argb)
    : SkinResource(Key);
public sealed record NullResource(string Key) : SkinResource(Key);
public sealed record ImageResource(string Key, string UriSource) : SkinResource(Key);

/// <summary>A skin turned into everything the app needs to apply it.</summary>
public sealed record CompiledSkin(
    string Id,
    string Name,
    bool IsLight,
    bool UsesAcrylic,
    string WindowArgb,
    IReadOnlyList<SkinResource> Resources)
{
    public SkinResource? Find(string key) => Resources.FirstOrDefault(r => r.Key == key);
}

public sealed class SkinException : Exception
{
    public SkinException(string message) : base(message) { }
}

/// <summary>
/// Builds the 61 theme resources from a skin's ~20 core colours. Colours a skin leaves out come
/// from its base skin (the built-in dark or light), and <see cref="SkinDefinition.Overrides"/>
/// replaces any derived value last.
/// </summary>
public static class SkinCompiler
{
    // The music player's sheen: white at these alphas, fading diagonally from the top-left
    private static readonly (int Alpha, double Offset)[] FullSheen = { (0x21, 0), (0x0C, 0.44), (0x03, 0.45) };

    public static CompiledSkin Compile(SkinDefinition skin, SkinDefinition? baseSkin = null)
    {
        if (skin.SchemaVersion > SkinDefinition.CurrentSchemaVersion)
            throw new SkinException($"skin.json uses schema {skin.SchemaVersion}; this AxePower reads up to {SkinDefinition.CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(skin.Id) || !skin.Id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            throw new SkinException($"id \"{skin.Id}\" must use only lower-case letters, digits and dashes.");

        bool isLight = skin.Base.Trim().ToLowerInvariant() switch
        {
            "light" => true,
            "dark" => false,
            _ => throw new SkinException($"base must be \"dark\" or \"light\", not \"{skin.Base}\".")
        };

        var c = skin.Colors;
        var b = baseSkin?.Colors;
        string Pick(string name, string? value, string? fallback) =>
            Hex.Normalize(value ?? fallback ?? throw new SkinException($"colors.{name} is missing and the base skin has none."), $"colors.{name}");

        string window = Pick("window", c.Window, b?.Window);
        string sidebar = Pick("sidebar", c.Sidebar, b?.Sidebar);
        string card = Pick("card", c.Card, b?.Card);
        string elevated = Pick("cardElevated", c.CardElevated, b?.CardElevated);
        string hover = Pick("hover", c.Hover, b?.Hover);
        string border = Pick("border", c.Border, b?.Border);
        string divider = Pick("divider", c.Divider, b?.Divider);
        string text1 = Pick("textPrimary", c.TextPrimary, b?.TextPrimary);
        string text2 = Pick("textSecondary", c.TextSecondary, b?.TextSecondary);
        string text3 = Pick("textMuted", c.TextMuted, b?.TextMuted);
        string text4 = Pick("textDisabled", c.TextDisabled, b?.TextDisabled);
        string accent = Pick("accent", c.Accent, b?.Accent);
        string success = Pick("success", c.Success, b?.Success);
        string warning = Pick("warning", c.Warning, b?.Warning);
        string error = Pick("error", c.Error, b?.Error);
        string eco = Pick("eco", c.Eco, b?.Eco);
        string smart = Pick("smart", c.Smart, b?.Smart);
        string beast = Pick("beast", c.Beast, b?.Beast);
        string cta = Pick("buttonPrimary", c.ButtonPrimary, b?.ButtonPrimary);
        string ctaText = Pick("buttonPrimaryText", c.ButtonPrimaryText, b?.ButtonPrimaryText);

        var g = skin.Glass;
        var bg = baseSkin?.Glass;
        string backdrop = (g.Backdrop ?? bg?.Backdrop ?? "none").Trim().ToLowerInvariant();
        if (backdrop is not ("none" or "acrylic"))
            throw new SkinException($"glass.backdrop must be \"none\" or \"acrylic\", not \"{g.Backdrop}\".");
        bool acrylic = backdrop == "acrylic";
        int sheen = Math.Clamp(g.Sheen ?? bg?.Sheen ?? 0, 0, 100);
        bool rim = g.Rim ?? bg?.Rim ?? false;
        bool shadow = g.Shadow ?? bg?.Shadow ?? false;

        var r = new List<SkinResource>();
        void Color(string key, string argb) => r.Add(new ColorResource(key, argb));
        void Solid(string key, string argb) => r.Add(new SolidBrushResource(key, argb));

        // Core backgrounds
        string titleBar = acrylic ? "#00000000" : window;
        Color("WindowBackgroundColor", window);
        Color("WindowSecondaryBackgroundColor", sidebar);
        Color("SidebarBackgroundColor", sidebar);
        Color("TitleBarBackgroundColor", titleBar);
        Solid("WindowBackgroundBrush", window);
        Solid("WindowSecondaryBackgroundBrush", sidebar);
        Solid("SidebarBackgroundBrush", sidebar);
        Solid("TitleBarBackgroundBrush", titleBar);

        // Surfaces
        Color("CardBackgroundColor", card);
        Color("ElevatedCardBackgroundColor", elevated);
        Color("HoverSurfaceColor", hover);
        Surface("CardBackgroundBrush", card);
        Surface("ElevatedCardBackgroundBrush", elevated);
        Solid("CardHoverBrush", hover);
        Solid("HoverBackgroundBrush", hover);
        Solid("CtaButtonBackgroundBrush", cta);
        Solid("CtaButtonForegroundBrush", ctaText);

        // Borders
        Color("CardBorderColor", border);
        Color("DividerColor", divider);
        if (rim)
            r.Add(new GradientBrushResource("CardBorderBrush", 0, 0, 1, 1, new[]
            {
                new GradientStopValue("#40FFFFFF", 0), new GradientStopValue("#0FFFFFFF", 0.6), new GradientStopValue("#1AFFFFFF", 1)
            }));
        else
            Solid("CardBorderBrush", border);
        r.Add(shadow ? new ShadowResource("CardShadowEffect", 24, 4, 270, 0.28, "#FF000000") : new NullResource("CardShadowEffect"));
        Solid("DividerBrush", divider);

        // Text
        Solid("PrimaryTextBrush", text1);
        Solid("SecondaryTextBrush", text2);
        Solid("MutedTextBrush", text3);
        Solid("DisabledTextBrush", text4);

        // Accents
        Solid("PrimaryBlueBrush", accent);
        Solid("AccentBrush", accent);
        Solid("AccentHoverBrush", isLight ? Hex.Mix(accent, "#FF000000", 0.15) : Hex.Mix(accent, "#FFFFFFFF", 0.25));
        Solid("AccentPressedBrush", Hex.Mix(accent, "#FF000000", isLight ? 0.3 : 0.2));
        Solid("FocusBrush", accent);

        // Semantic and power modes
        Solid("SuccessBrush", success);
        Solid("WarningBrush", warning);
        Solid("ErrorBrush", error);
        Solid("EcoBrush", eco);
        Solid("SmartBrush", smart);
        Solid("BeastBrush", beast);

        // Tray menu
        if (isLight)
        {
            Solid("TrayMenuBackgroundBrush", "#FFFFFFFF");
            Solid("TrayMenuBorderBrush", "#FFCBD5E1");
            Solid("TrayMenuForegroundBrush", "#FF000000");
            Solid("TrayMenuHoverBackgroundBrush", "#FFE2E8F0");
            Solid("TrayMenuHoverForegroundBrush", "#FF000000");
            Solid("TrayMenuSeparatorBrush", "#FFE2E8F0");
        }
        else
        {
            Solid("TrayMenuBackgroundBrush", "#FF3D4450");
            Solid("TrayMenuBorderBrush", "#FF282828");
            Solid("TrayMenuForegroundBrush", "#FFDCDEDF");
            Solid("TrayMenuHoverBackgroundBrush", "#FFDCDEDF");
            Solid("TrayMenuHoverForegroundBrush", "#FF1E2228");
            Solid("TrayMenuSeparatorBrush", "#FF5E6063");
        }

        r.Add(new ImageResource("AppIconImage", isLight ? "/Resources/AxePower_black.ico" : "/Resources/AxePower_white.ico"));

        // Music
        Solid("MusicAccentBrush", accent);
        Solid("MusicGlassSurfaceBrush", Hex.WithAlphaScaled(elevated, isLight ? 0xEB : 0xE6));
        Solid("MusicTrackBrush", isLight ? "#26000000" : "#33FFFFFF");
        Solid("MusicPlayGlyphBrush", ctaText);
        r.Add(new GradientBrushResource("MusicPlaceholderArtBrush", 1, 0, 0, 1, new[]
        {
            new GradientStopValue(accent, 0), new GradientStopValue("#FF7C5CE6", 0.55), new GradientStopValue("#FFEC4899", 1)
        }));

        // Share Screen: a video surface stays dark in every skin, so it recedes around the picture
        Solid("ShareStageBackgroundBrush", "#FF0A0B0D");
        Solid("ShareTileBackgroundBrush", "#FF16181C");
        Solid("ShareTileBorderBrush", "#FF2A2E37");
        Solid("ShareControlBarBrush", "#F21E2129");
        Solid("ShareControlButtonBrush", "#2EFFFFFF");
        Solid("ShareControlButtonHoverBrush", "#52FFFFFF");
        Solid("ShareControlForegroundBrush", "#FFF2F3F5");
        Solid("ShareControlMutedBrush", "#FF9BA3AF");
        Solid("ShareNamePillBrush", "#B3000000");
        Solid("ShareDangerBrush", "#FFDA373C");
        Solid("ShareDangerHoverBrush", "#FFB02B2F");
        Solid("ShareLiveBrush", "#FF23A55A");

        ApplyOverrides(skin, r);

        var windowArgb = ((ColorResource)r.First(x => x.Key == "WindowBackgroundColor")).Argb;
        return new CompiledSkin(skin.Id, string.IsNullOrWhiteSpace(skin.Name) ? skin.Id : skin.Name, isLight, acrylic, windowArgb, r);

        void Surface(string key, string baseArgb)
        {
            if (sheen == 0)
            {
                Solid(key, baseArgb);
                return;
            }

            var stops = FullSheen
                .Select(s => new GradientStopValue(Hex.OverlayWhite(baseArgb, (int)Math.Round(s.Alpha * sheen / 100.0, MidpointRounding.ToEven)), s.Offset))
                .Append(new GradientStopValue(baseArgb, 1))
                .ToList();
            r.Add(new GradientBrushResource(key, 0, 0, 0.9, 0.65, stops));
        }
    }

    /// <summary>
    /// Solid and Color resources take one hex value; a gradient takes its stop colours as a
    /// comma-separated list of the same length.
    /// </summary>
    private static void ApplyOverrides(SkinDefinition skin, List<SkinResource> resources)
    {
        foreach (var (key, value) in skin.Overrides)
        {
            int i = resources.FindIndex(x => x.Key == key);
            if (i < 0) throw new SkinException($"overrides.{key}: there is no theme resource with that name.");

            resources[i] = resources[i] switch
            {
                ColorResource => new ColorResource(key, Hex.Normalize(value, $"overrides.{key}")),
                SolidBrushResource => new SolidBrushResource(key, Hex.Normalize(value, $"overrides.{key}")),
                GradientBrushResource grad => OverrideStops(grad, value),
                _ => throw new SkinException($"overrides.{key}: this resource is not a colour and cannot be overridden.")
            };
        }

        GradientBrushResource OverrideStops(GradientBrushResource grad, string value)
        {
            var colors = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (colors.Length != grad.Stops.Count)
                throw new SkinException($"overrides.{grad.Key}: give {grad.Stops.Count} comma-separated colours, one per gradient stop.");
            return grad with
            {
                Stops = grad.Stops.Select((s, n) => s with { Argb = Hex.Normalize(colors[n], $"overrides.{grad.Key}") }).ToList()
            };
        }
    }
}

/// <summary>Hex colour helpers. Colours are carried as "#AARRGGBB", upper case.</summary>
public static class Hex
{
    public static string Normalize(string value, string where = "colour")
    {
        var s = value.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (!s.All(Uri.IsHexDigit))
            throw new SkinException($"{where}: \"{value}\" is not a hex colour.");

        s = s.Length switch
        {
            3 => "FF" + string.Concat(s.Select(ch => $"{ch}{ch}")),
            6 => "FF" + s,
            8 => s,
            _ => throw new SkinException($"{where}: \"{value}\" must be #RGB, #RRGGBB or #AARRGGBB.")
        };
        return "#" + s.ToUpperInvariant();
    }

    public static (int A, int R, int G, int B) Parse(string argb)
    {
        var s = Normalize(argb);
        int P(int i) => int.Parse(s.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (P(1), P(3), P(5), P(7));
    }

    public static string Format(int a, int r, int g, int b) => $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary>Linear blend of the colour channels toward <paramref name="other"/>; alpha kept.</summary>
    public static string Mix(string argb, string other, double amount)
    {
        var (a, r, g, b) = Parse(argb);
        var (_, r2, g2, b2) = Parse(other);
        int M(int x, int y) => (int)Math.Round(x + (y - x) * amount, MidpointRounding.ToEven);
        return Format(a, M(r, r2), M(g, g2), M(b, b2));
    }

    /// <summary>Premultiplied composite of white at <paramref name="whiteAlpha"/> over the colour.</summary>
    public static string OverlayWhite(string argb, int whiteAlpha)
    {
        var (A8, R, G, B) = Parse(argb);
        double A = A8 / 255.0, a = whiteAlpha / 255.0;
        double oa = a + A * (1 - a);
        int Ch(int c) => (int)Math.Round((255 * a + c * A * (1 - a)) / oa, MidpointRounding.ToEven);
        return Format((int)Math.Round(oa * 255, MidpointRounding.ToEven), Ch(R), Ch(G), Ch(B));
    }

    /// <summary>Scales the colour's alpha by <paramref name="alpha"/>/255 (an opaque colour takes it as is).</summary>
    public static string WithAlphaScaled(string argb, int alpha)
    {
        var (a, r, g, b) = Parse(argb);
        return Format((int)Math.Round(a * alpha / 255.0, MidpointRounding.ToEven), r, g, b);
    }
}
