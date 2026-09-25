using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Puts the search hits for a track in the order most likely to be the recording that was
/// actually asked for.
///
/// Length alone is not enough: a live take, a lyric video and the studio track are usually
/// within seconds of each other, so the closest match by duration is regularly the wrong one.
/// What separates them is the wording of the upload and who posted it, so both are scored, and
/// a word is only held against a candidate when the track itself did not ask for it — searching
/// for a live album must still return the live takes.
/// </summary>
public static class CandidateRanker
{
    /// <summary>Uploads that are the recording itself. Strong evidence.</summary>
    private static readonly string[] OfficialMarkers =
    {
        "official audio", "official music video", "official video", "official lyric",
        "audio oficial", "full song", "official song"
    };

    /// <summary>
    /// Wordings that mean this is some other recording than the one asked for, with how much
    /// each one counts against a candidate.
    /// </summary>
    private static readonly (string Word, double Penalty)[] VariantMarkers =
    {
        ("karaoke", 1.0),
        ("instrumental", 1.0),
        ("nightcore", 1.0),
        ("8d audio", 1.0),
        ("reaction", 1.0),
        ("behind the scenes", 1.0),
        ("making of", 1.0),
        ("tutorial", 1.0),
        ("cover by", 0.9),
        ("cover)", 0.9),
        ("remix", 0.8),
        ("mashup", 0.8),
        ("live at", 0.8),
        ("live from", 0.8),
        ("live on", 0.8),
        ("live in", 0.8),
        ("(live", 0.8),
        ("concert", 0.7),
        ("acoustic", 0.6),
        ("sped up", 0.6),
        ("slowed", 0.6),
        ("reverb", 0.5),
        ("mix)", 0.5),
        ("version)", 0.35),
        ("lyrics", 0.6),
        ("lyric video", 0.6),
        ("letra", 0.4)
    };

    /// <summary>
    /// Uploads that are not a single play of the track: an hour loop, a full-album upload or a
    /// mix. These are rejected outright when the length gives them away.
    /// </summary>
    private static readonly string[] LengthMarkers =
    {
        "1 hour", "one hour", "10 hours", "hour loop", "loop", "full album", "megamix",
        "playlist", "compilation", "best of", "mix -", "dj mix", "nonstop"
    };

    /// <summary>
    /// The candidates, most likely first. Ties keep the source's own order, which already
    /// reflects its relevance ranking.
    /// </summary>
    public static IReadOnlyList<StreamCandidate> Rank(Track track, IReadOnlyList<StreamCandidate> candidates)
    {
        if (candidates.Count < 2) return candidates;

        return candidates
            .Select((candidate, order) => (candidate, order, score: Score(track, candidate)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.order)
            .Select(x => x.candidate)
            .ToList();
    }

    /// <summary>
    /// How well a candidate matches. Higher is better; the scale has no meaning beyond
    /// comparing candidates for the same track.
    /// </summary>
    public static double Score(Track track, StreamCandidate candidate)
    {
        string haystack = Normalize(candidate.Title + " " + (candidate.Artist ?? string.Empty));
        string wanted = Normalize(track.Title + " " + string.Join(" ", track.Artists) + " " + (track.Album ?? string.Empty));

        double score = 0;

        score += DurationScore(track.Duration, candidate.Duration);
        score += TitleOverlapScore(track.Title, candidate.Title);
        score += ArtistScore(track, candidate, haystack);
        score += OfficialScore(haystack, candidate);
        score += VariantScore(haystack, wanted);
        score -= LengthPenalty(haystack, wanted, track.Duration, candidate.Duration);

        return score;
    }

    /// <summary>
    /// Matching length is good evidence but not decisive, because the wrong recording is often
    /// the same length. An unknown length is neither rewarded nor punished.
    /// </summary>
    private static double DurationScore(TimeSpan wanted, TimeSpan? found)
    {
        if (wanted <= TimeSpan.Zero || found is null || found.Value <= TimeSpan.Zero) return 0;

        double seconds = Math.Abs((found.Value - wanted).TotalSeconds);
        return seconds switch
        {
            <= 2 => 1.6,
            <= 5 => 1.2,
            <= 12 => 0.7,
            <= 30 => 0.2,
            <= 90 => -0.4,
            _ => -1.5
        };
    }

    /// <summary>How much of the wanted title the candidate actually contains.</summary>
    private static double TitleOverlapScore(string wantedTitle, string candidateTitle)
    {
        var wanted = Words(wantedTitle);
        if (wanted.Count == 0) return 0;

        var found = Words(candidateTitle).ToHashSet(StringComparer.Ordinal);
        double matched = wanted.Count(word => found.Contains(word));
        return 1.4 * (matched / wanted.Count);
    }

    /// <summary>
    /// The uploader being the artist is the single most reliable signal there is, and an
    /// auto-generated "Topic" channel means the recording came from the label itself.
    /// </summary>
    private static double ArtistScore(Track track, StreamCandidate candidate, string haystack)
    {
        string channel = Normalize(candidate.Artist);
        string title = Normalize(candidate.Title);
        double best = 0;

        foreach (string artist in track.Artists)
        {
            string normalized = Normalize(artist);
            if (normalized.Length < 2) continue;

            // Who posted it matters far more than who is named in the title: a re-upload by a
            // lyric-video channel names the artist too, but the artist's own channel is the
            // only one that carries their actual release.
            if (channel.Contains(normalized, StringComparison.Ordinal)) best = Math.Max(best, 1.4);
            else if (title.Contains(normalized, StringComparison.Ordinal)) best = Math.Max(best, 0.5);
        }

        if ((candidate.Artist ?? string.Empty).EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            best += 1.5;

        return best;
    }

    private static double OfficialScore(string haystack, StreamCandidate candidate)
    {
        foreach (string marker in OfficialMarkers)
        {
            if (!haystack.Contains(marker, StringComparison.Ordinal)) continue;

            // A plain audio upload beats an official video, which is often an edit with an
            // intro, an outro or dialogue over the music.
            return marker.Contains("audio", StringComparison.Ordinal) ? 1.3 : 0.6;
        }
        return 0;
    }

    /// <summary>
    /// Counts the wording that marks a different recording. A word the request itself asked for
    /// flips from a penalty to a bonus: wanting the live take should prefer the live uploads,
    /// not merely stop punishing them, or an official studio upload still wins on its own badge.
    /// </summary>
    private static double VariantScore(string haystack, string wanted)
    {
        double score = 0;
        foreach (var (word, weight) in VariantMarkers)
        {
            string bare = word.Trim('(', ')');
            bool asked = wanted.Contains(bare, StringComparison.Ordinal);
            bool present = haystack.Contains(word, StringComparison.Ordinal);

            if (asked) score += present ? weight : -weight;
            else if (present) score -= weight;
        }
        return score;
    }

    /// <summary>
    /// An hour-long upload of a three-minute song is never what was wanted. The wording alone
    /// is not enough (a title can say "loop" and still be one play), so the length has to agree.
    /// </summary>
    private static double LengthPenalty(string haystack, string wanted, TimeSpan trackDuration, TimeSpan? candidateDuration)
    {
        bool marked = LengthMarkers.Any(m => haystack.Contains(m, StringComparison.Ordinal)
                                             && !wanted.Contains(m, StringComparison.Ordinal));

        bool muchLonger = candidateDuration is { } found
                          && trackDuration > TimeSpan.Zero
                          && found > trackDuration + TimeSpan.FromMinutes(5);

        // Either signal on its own is weak; together they are conclusive.
        if (marked && muchLonger) return 6.0;
        if (muchLonger) return 3.0;
        if (marked) return 1.0;
        return 0;
    }

    private static List<string> Words(string value) =>
        Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => word.Length > 1)
            .ToList();

    /// <summary>
    /// Lower-cased, accent-stripped and punctuation-free, so "Beyoncé — Halo (Official)" and
    /// "beyonce halo official" compare as the same text.
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool lastWasSpace = false;

        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            // Brackets are kept, because markers like "(live" and "version)" rely on them.
            if (char.IsLetterOrDigit(c) || c is '(' or ')')
            {
                builder.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim().Normalize(NormalizationForm.FormC);
    }
}
