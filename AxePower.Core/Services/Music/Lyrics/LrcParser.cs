using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Parses standard and enhanced LRC while preserving millisecond timestamps.</summary>
public static class LrcParser
{
    private static readonly Regex Timestamp = new(
        @"\[(?<minute>\d{1,3}):(?<second>\d{2})(?:[\.:](?<fraction>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Offset = new(
        @"^\s*\[offset:(?<offset>[+-]?\d+)\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EnhancedTimestamp = new(
        @"<\d{1,3}:\d{2}(?:[\.:]\d{1,3})?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LyricLine>();

        var parsed = new List<LyricLine>();
        int offsetMs = 0;

        foreach (string rawLine in lrc.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var offset = Offset.Match(rawLine);
            if (offset.Success
                && int.TryParse(offset.Groups["offset"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value))
            {
                offsetMs = value;
                continue;
            }

            var matches = Timestamp.Matches(rawLine);
            if (matches.Count == 0) continue;

            string text = Timestamp.Replace(rawLine, string.Empty);
            text = EnhancedTimestamp.Replace(text, string.Empty).Trim();
            if (text.Length == 0) continue;

            foreach (Match match in matches)
            {
                int minutes = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
                int seconds = int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture);
                int milliseconds = FractionToMilliseconds(match.Groups["fraction"].Value);
                long totalMs = Math.Max(0, (minutes * 60L + seconds) * 1000L + milliseconds + offsetMs);
                parsed.Add(new LyricLine(TimeSpan.FromMilliseconds(totalMs), text));
            }
        }

        return parsed
            .OrderBy(line => line.Timestamp)
            .ThenBy(line => line.Text, StringComparer.Ordinal)
            .Distinct()
            .ToArray();
    }

    private static int FractionToMilliseconds(string fraction) => fraction.Length switch
    {
        0 => 0,
        1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
        2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
        _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture)
    };
}

