using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models.Skins;

/// <summary>
/// One skin, as written in <c>skins/&lt;id&gt;/skin.json</c>. About twenty core colours describe
/// it; <see cref="Services.Skins.SkinCompiler"/> works out the rest of the app's 61 theme
/// resources from them. Any colour left out is taken from the base skin (dark or light), so a
/// skin can be as small as a single accent. See skins/README.md for the full guide.
/// </summary>
public sealed class SkinDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Folder-style id: lower-case letters, digits and dashes.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }

    /// <summary>"dark" or "light": the text/contrast direction, the fallback for missing colours,
    /// the title bar mode and the logo variant.</summary>
    [JsonPropertyName("base")] public string Base { get; set; } = "dark";

    [JsonPropertyName("colors")] public SkinColors Colors { get; set; } = new();
    [JsonPropertyName("glass")] public SkinGlass Glass { get; set; } = new();

    /// <summary>Theme resource key → hex, for the rare case a derived value is not wanted.
    /// Only Color and SolidColorBrush resources can be overridden.</summary>
    [JsonPropertyName("overrides")] public Dictionary<string, string> Overrides { get; set; } = new();
}

/// <summary>The core palette. Every value is a hex colour: #RGB, #RRGGBB or #AARRGGBB.</summary>
public sealed class SkinColors
{
    [JsonPropertyName("window")] public string? Window { get; set; }
    [JsonPropertyName("sidebar")] public string? Sidebar { get; set; }
    [JsonPropertyName("card")] public string? Card { get; set; }
    [JsonPropertyName("cardElevated")] public string? CardElevated { get; set; }
    [JsonPropertyName("hover")] public string? Hover { get; set; }
    [JsonPropertyName("border")] public string? Border { get; set; }
    [JsonPropertyName("divider")] public string? Divider { get; set; }

    [JsonPropertyName("textPrimary")] public string? TextPrimary { get; set; }
    [JsonPropertyName("textSecondary")] public string? TextSecondary { get; set; }
    [JsonPropertyName("textMuted")] public string? TextMuted { get; set; }
    [JsonPropertyName("textDisabled")] public string? TextDisabled { get; set; }

    [JsonPropertyName("accent")] public string? Accent { get; set; }
    [JsonPropertyName("success")] public string? Success { get; set; }
    [JsonPropertyName("warning")] public string? Warning { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("eco")] public string? Eco { get; set; }
    [JsonPropertyName("smart")] public string? Smart { get; set; }
    [JsonPropertyName("beast")] public string? Beast { get; set; }

    /// <summary>The strong call-to-action button (e.g. Apply on the Dashboard) and its text.</summary>
    [JsonPropertyName("buttonPrimary")] public string? ButtonPrimary { get; set; }
    [JsonPropertyName("buttonPrimaryText")] public string? ButtonPrimaryText { get; set; }
}

/// <summary>How much glass the skin uses. All off is a solid, flat skin.</summary>
public sealed class SkinGlass
{
    /// <summary>"none" (solid window) or "acrylic" (the window blurs what is behind it).</summary>
    [JsonPropertyName("backdrop")] public string? Backdrop { get; set; }

    /// <summary>0–100: strength of the top-left sheen on cards, as a percentage of the music
    /// player's. 0 is none.</summary>
    [JsonPropertyName("sheen")] public int? Sheen { get; set; }

    /// <summary>A lit card edge, brightest at the top-left, instead of the flat border colour.</summary>
    [JsonPropertyName("rim")] public bool? Rim { get; set; }

    /// <summary>A soft drop shadow under cards that use the shared card style.</summary>
    [JsonPropertyName("shadow")] public bool? Shadow { get; set; }
}
