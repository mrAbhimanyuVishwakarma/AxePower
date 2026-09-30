using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// LRCLIB synchronized lyrics with verified fuzzy search, Kugou, and YouTube lyric fallbacks.
/// Noisy upload titles are cleaned before catalogue lookup so record-label YouTube channels do
/// not prevent real artist/title matches.
/// </summary>
public sealed class LrcLibLyricsService : ILyricsService, IDisposable
{
    private const string LrcLibEndpoint = "https://lrclib.net/api/get";
    private const string LrcLibSearchEndpoint = "https://lrclib.net/api/search";
    private const string KugouSearchEndpoint = "https://lyrics.kugou.com/search";
    private const string KugouDownloadEndpoint = "https://lyrics.kugou.com/download";

    private static readonly Regex ComparisonNoise = new(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
    private static readonly Regex ParenthesizedNoise = new(
        @"\((?:official|music|video|audio|lyrics?|hd|4k|remastered|full|song|feat|ft)[^\)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketedNoise = new(
        @"\[(?:official|music|video|audio|lyrics?|hd|4k|remastered|full|song|feat|ft)[^\]]*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>How long to wait before asking a busy LRCLIB again. Settable so tests do not wait.</summary>
    internal TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    private readonly HttpClient _http;
    private readonly LogService _log;
    private readonly IYouTubeCaptionLyricsProvider? _youtubeLyrics;
    private readonly bool _ownsClient;

    public LrcLibLyricsService(LogService log, IYouTubeCaptionLyricsProvider youtubeLyrics)
        : this(CreateClient(), log, youtubeLyrics, ownsClient: true)
    {
    }

    public LrcLibLyricsService(HttpClient http, LogService log, bool ownsClient = false)
        : this(http, log, youtubeLyrics: null, ownsClient)
    {
    }

    public LrcLibLyricsService(
        HttpClient http,
        LogService log,
        IYouTubeCaptionLyricsProvider? youtubeLyrics,
        bool ownsClient = false)
    {
        _http = http;
        _log = log;
        _youtubeLyrics = youtubeLyrics;
        _ownsClient = ownsClient;
    }

    public async Task<LyricsResult?> GetLyricsAsync(Track track, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;

        LyricsMetadata metadata = ExtractMetadata(track);
        LyricsResult? primary = await TryLrcLibAsync(track, metadata, ct);
        if (primary?.IsSynchronized == true) return primary;

        string title = metadata.Titles.FirstOrDefault() ?? track.Title;
        string artist = metadata.Artists.FirstOrDefault() ?? track.Artists.FirstOrDefault() ?? string.Empty;
        LyricsResult? kugou = await TryKugouAsync(title, artist, track.Duration, ct);
        if (kugou?.IsSynchronized == true) return kugou;

        LyricsResult? youtube = track.Source?.Provider.Equals("youtube", StringComparison.OrdinalIgnoreCase) == true
            ? await (_youtubeLyrics?.GetLyricsAsync(track.Source.Id, ct)
                ?? Task.FromResult<LyricsResult?>(null))
            : null;

        return youtube ?? kugou ?? primary;
    }

    private async Task<LyricsResult?> TryLrcLibAsync(
        Track track,
        LyricsMetadata metadata,
        CancellationToken ct)
    {
        try
        {
            var candidates = SearchCandidates(metadata).Take(4).ToList();
            foreach (var candidate in candidates)
            {
                // The exact-match endpoint answers 400 without an artist; the search below covers
                // title-only candidates instead.
                if (string.IsNullOrWhiteSpace(candidate.Artist)) continue;

                string url = LrcLibEndpoint
                    + "?track_name=" + Uri.EscapeDataString(candidate.Title)
                    + "&artist_name=" + Uri.EscapeDataString(candidate.Artist);
                if (track.Duration > TimeSpan.Zero)
                    url += "&duration=" + (int)Math.Round(track.Duration.TotalSeconds);

                // One failed candidate (not found, busy, rejected) must not end the lookup:
                // the fuzzy search after this loop is what matches most YouTube uploads.
                using var response = await GetLrcLibAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var exact = await response.Content.ReadFromJsonAsync<LrcLibResponse>(cancellationToken: ct);
                LyricsResult? result = ToResult(exact);
                if (result is not null) return result;
            }

            var verified = new List<(LrcLibResponse Record, double Score)>();
            var queries = candidates
                .SelectMany(candidate => new[]
                {
                    (candidate.Title + " " + candidate.Artist).Trim(),
                    candidate.Title
                })
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4);

            foreach (string query in queries)
            {
                string url = LrcLibSearchEndpoint + "?q=" + Uri.EscapeDataString(query);
                using var response = await GetLrcLibAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var results = await response.Content.ReadFromJsonAsync<List<LrcLibResponse>>(cancellationToken: ct);
                foreach (var result in results ?? Enumerable.Empty<LrcLibResponse>())
                {
                    if (!IsAcceptable(result, track, metadata, out double score)) continue;
                    int existing = verified.FindIndex(item => item.Record.Id == result.Id);
                    if (existing < 0) verified.Add((result, score));
                    else if (score > verified[existing].Score) verified[existing] = (result, score);
                }
            }

            var best = verified
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.Record.SyncedLyrics))
                .ThenByDescending(item => item.Score)
                .Select(item => ToResult(item.Record))
                .FirstOrDefault(result => result is not null);
            return best;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] LRCLIB lyrics lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>LRCLIB answers 503 or 429 when it is busy; one short retry usually gets through.</summary>
    private async Task<HttpResponseMessage> GetLrcLibAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        if (response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
            return response;

        response.Dispose();
        await Task.Delay(BusyRetryDelay, ct);
        return await _http.GetAsync(url, ct);
    }

    private async Task<LyricsResult?> TryKugouAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken ct)
    {
        try
        {
            string keyword = string.IsNullOrWhiteSpace(artist) ? title : artist + " - " + title;
            string searchUrl = KugouSearchEndpoint
                + "?ver=1&man=yes&client=pc&keyword=" + Uri.EscapeDataString(keyword)
                + "&duration=" + Math.Max(0, (long)duration.TotalMilliseconds);

            var search = await _http.GetFromJsonAsync<KugouSearchResponse>(searchUrl, ct);
            var match = search?.Candidates?.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Id) && !string.IsNullOrWhiteSpace(candidate.AccessKey));
            if (match is null) return null;

            string downloadUrl = KugouDownloadEndpoint
                + "?ver=1&client=pc&fmt=lrc&charset=utf8&id=" + Uri.EscapeDataString(match.Id!)
                + "&accesskey=" + Uri.EscapeDataString(match.AccessKey!);

            var download = await _http.GetFromJsonAsync<KugouDownloadResponse>(downloadUrl, ct);
            if (string.IsNullOrWhiteSpace(download?.Content)) return null;

            string lrc = Encoding.UTF8.GetString(Convert.FromBase64String(download.Content));
            var lines = LrcParser.Parse(lrc);
            return lines.Count == 0 ? null : new LyricsResult("Kugou", lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Kugou lyrics lookup failed: {ex.Message}");
            return null;
        }
    }

    private static LyricsResult? ToResult(LrcLibResponse? response)
    {
        if (response is null) return null;
        var lines = LrcParser.Parse(response.SyncedLyrics);
        string? plain = NullIfBlank(response.PlainLyrics);
        return lines.Count == 0 && plain is null ? null : new LyricsResult("LRCLIB", lines, plain);
    }

    private static LyricsMetadata ExtractMetadata(Track track)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string rawTitle = track.Title ?? string.Empty;

        if (!rawTitle.Contains('|')) Add(titles, CleanTitle(rawTitle));
        foreach (string artist in track.Artists)
        {
            string cleaned = CleanArtist(artist);
            if (IsUsableArtist(cleaned)) Add(artists, cleaned);
        }

        if (rawTitle.Contains('|'))
        {
            string[] parts = rawTitle.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) Add(titles, CleanTitle(parts[0]));
            foreach (string part in parts.Skip(1))
            {
                string cleaned = CleanArtist(part);
                if (IsUsableArtist(cleaned)) Add(artists, cleaned);
            }
        }

        int separator = rawTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0)
        {
            string possibleArtist = CleanArtist(rawTitle[..separator]);
            string possibleTitle = CleanTitle(rawTitle[(separator + 3)..]);
            if (IsUsableArtist(possibleArtist)) Add(artists, possibleArtist);
            Add(titles, possibleTitle);
        }

        var featured = Regex.Match(
            rawTitle,
            @"(?:\bfeat\.?|\bft\.?|\bfeaturing\b)\s*([^\]\)\|\-]+)",
            RegexOptions.IgnoreCase);
        if (featured.Success)
        {
            string featuredArtist = CleanArtist(featured.Groups[1].Value);
            if (IsUsableArtist(featuredArtist)) Add(artists, featuredArtist);
        }

        // "Song (Full Video) Artist": uploads often put the singer after the noise tag
        string firstPart = rawTitle.Split('|')[0];
        var tag = ParenthesizedNoise.Match(firstPart) is { Success: true } paren ? paren : BracketedNoise.Match(firstPart);
        if (tag.Success)
        {
            string before = CleanTitle(firstPart[..tag.Index]);
            string after = CleanArtist(firstPart[(tag.Index + tag.Length)..]);
            if (!string.IsNullOrWhiteSpace(before)) Add(titles, before);
            if (IsUsableArtist(after)) Add(artists, after);
        }

        if (titles.Count == 0) Add(titles, rawTitle.Trim());
        return new LyricsMetadata(titles.ToList(), artists.ToList(), Normalize(rawTitle));
    }

    private static IEnumerable<(string Title, string Artist)> SearchCandidates(LyricsMetadata metadata)
    {
        foreach (string title in metadata.Titles)
        {
            foreach (string artist in metadata.Artists) yield return (title, artist);
            yield return (title, string.Empty);
        }
    }

    private static bool IsAcceptable(
        LrcLibResponse candidate,
        Track track,
        LyricsMetadata metadata,
        out double score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(candidate.TrackName)) return false;

        string candidateTitle = Normalize(candidate.TrackName);
        double titleScore = metadata.Titles
            .Select(title => Similarity(candidateTitle, Normalize(title)))
            .DefaultIfEmpty(0)
            .Max();
        if (titleScore < 0.55) return false;

        double artistScore;
        if (metadata.Artists.Count > 0)
        {
            string candidateArtist = Normalize(candidate.ArtistName ?? string.Empty);
            artistScore = metadata.Artists
                .Select(artist => ArtistSimilarity(candidateArtist, Normalize(artist)))
                .DefaultIfEmpty(0)
                .Max();

            // Uploads name the singer somewhere in the title even when the channel is a label
            if (NamedInTitle(candidateArtist, metadata.RawTitle)) artistScore = Math.Max(artistScore, 0.8);
            if (artistScore < 0.45) return false;
        }
        else if (NamedInTitle(Normalize(candidate.ArtistName ?? string.Empty), metadata.RawTitle))
        {
            artistScore = 0.8;
        }
        else
        {
            if (titleScore < 0.75) return false;
            artistScore = 0.5;
        }

        double durationScore = 0;
        if (track.Duration > TimeSpan.FromSeconds(30) && candidate.Duration > 0)
        {
            double difference = Math.Abs(candidate.Duration - track.Duration.TotalSeconds);
            if (difference <= 4) durationScore = 0.2;
            else if (difference <= 8) durationScore = 0.1;
            else if (difference > 25 && titleScore < 0.85) return false;
        }

        score = titleScore * 0.5 + artistScore * 0.4 + durationScore;
        if (!string.IsNullOrWhiteSpace(candidate.SyncedLyrics)) score += 0.1;
        return score >= 0.65;
    }

    private static double ArtistSimilarity(string candidate, string expected)
    {
        double score = Similarity(candidate, expected);
        string[] candidateWords = Words(candidate);
        string[] expectedWords = Words(expected);
        if (candidateWords.Any(left => expectedWords.Any(right =>
                left == right || left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))))
        {
            score = Math.Max(score, 0.8);
        }
        return score;
    }

    /// <summary>True when a whole word of the artist (4+ letters) appears in the upload title.</summary>
    private static bool NamedInTitle(string candidateArtist, string rawTitle)
    {
        var titleWords = new HashSet<string>(rawTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Words(candidateArtist).Any(word => word.Length >= 4 && titleWords.Contains(word));
    }

    private static string[] Words(string value) => value
        .Split(new[] { ' ', '&', ',', '/', '-', '+' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(word => word.Length >= 3)
        .ToArray();

    private static double Similarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
        if (left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase)) return 0.85;

        int maximum = Math.Max(left.Length, right.Length);
        return Math.Max(0, 1.0 - (double)Levenshtein(left, right) / maximum);
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= right.Length; column++)
            {
                int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static string CleanTitle(string title)
    {
        string cleaned = ParenthesizedNoise.Replace(title ?? string.Empty, string.Empty);
        cleaned = BracketedNoise.Replace(cleaned, string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s*-\s*official\s*(?:video|audio)?", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(
            cleaned,
            @"(?:\blatest\s+punjabi\s+song|\bpunjabi\s+song|\bnew\s+song\b|\bvideo\s+song\b|\bvideo\b|\blyrics?\b).*",
            string.Empty,
            RegexOptions.IgnoreCase);
        return cleaned.Trim(' ', '-', '|');
    }

    private static string CleanArtist(string artist)
    {
        string cleaned = Regex.Replace(artist ?? string.Empty, @"\s*-\s*Topic\s*$", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s*VEVO\s*$", string.Empty, RegexOptions.IgnoreCase);
        return cleaned.Trim(' ', '-', '|');
    }

    private static bool IsRecordLabel(string name) =>
        Regex.IsMatch(
            name ?? string.Empty,
            @"\b(?:records|music|company|entertainment|studio|films|series|official)\b",
            RegexOptions.IgnoreCase);

    private static bool IsUsableArtist(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !IsRecordLabel(name)
        && !Regex.IsMatch(
            name,
            @"\b(?:latest|song|video|audio|lyrics?|full|hd|4k|new)\b",
            RegexOptions.IgnoreCase);

    private static string Normalize(string value) =>
        ComparisonNoise.Replace((value ?? string.Empty).ToLowerInvariant(), string.Empty).Trim();

    private static void Add(HashSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values.Add(value.Trim());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AxePower/2.1.0 (https://github.com/AxePower)");
        return client;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    /// <param name="RawTitle">The whole upload title, normalized, for spotting an artist named anywhere in it.</param>
    private sealed record LyricsMetadata(IReadOnlyList<string> Titles, IReadOnlyList<string> Artists, string RawTitle);

    private sealed record LrcLibResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("trackName")] string? TrackName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);

    private sealed record KugouSearchResponse(
        [property: JsonPropertyName("candidates")] IReadOnlyList<KugouCandidate>? Candidates);

    private sealed record KugouCandidate(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("accesskey")] string? AccessKey);

    private sealed record KugouDownloadResponse(
        [property: JsonPropertyName("content")] string? Content);
}
