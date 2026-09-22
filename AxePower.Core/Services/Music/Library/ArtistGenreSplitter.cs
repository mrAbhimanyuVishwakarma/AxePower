using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Splits multi-value artist and genre tags. Artists split on ";", "/", " feat. ", " ft. " and
/// " featuring "; "&amp;" and "," only when enabled, because names like "Simon &amp; Garfunkel"
/// are one artist. Genres split on ";", "/" and ",". Results are trimmed and de-duplicated
/// case-insensitively.
/// </summary>
public static class ArtistGenreSplitter
{
    private static readonly Regex ArtistSeparators = new(
        @"\s*;\s*|\s*/\s*|\s+(?:feat\.|ft\.|featuring)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ArtistSeparatorsWithAmpersandAndComma = new(
        @"\s*;\s*|\s*/\s*|\s+(?:feat\.|ft\.|featuring)\s+|\s+&\s+|\s*,\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenreSeparators = new(@"\s*[;/,]\s*", RegexOptions.Compiled);

    public static IReadOnlyList<string> SplitArtists(string? value, bool splitOnAmpersandAndComma)
    {
        var regex = splitOnAmpersandAndComma ? ArtistSeparatorsWithAmpersandAndComma : ArtistSeparators;
        return Split(value, regex);
    }

    public static IReadOnlyList<string> SplitGenres(string? value) => Split(value, GenreSeparators);

    private static IReadOnlyList<string> Split(string? value, Regex separators)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in separators.Split(value))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed)) result.Add(trimmed);
        }
        return result;
    }
}
