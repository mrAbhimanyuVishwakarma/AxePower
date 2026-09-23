using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Spotify as a catalogue: search it, and bring your own playlists in. It supplies no audio,
/// because no third-party player is allowed to decode Spotify's streams — the audio for a
/// Spotify track comes from whichever streaming source is active, which is why this provider
/// pairs itself with YouTube.
///
/// Controlling the Spotify desktop app itself is a separate feature (the universal media
/// controller); this provider never launches or drives it.
/// </summary>
public sealed class SpotifyProvider : IMetadataProvider, IAuthenticatedProvider
{
    private const string ApiBase = "https://api.spotify.com/v1/";

    private readonly SpotifyAuth _auth;
    private readonly HttpClient _http;

    private string? _accountName;

    public SpotifyProvider(SpotifyAuth auth, HttpClient? http = null)
    {
        _auth = auth;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _auth.Changed += (_, _) =>
        {
            if (!_auth.IsSignedIn) _accountName = null;
            AuthChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public ProviderInfo Info { get; } = new(
        Id: "spotify",
        Name: "Spotify",
        Description:
            "Search Spotify's catalogue and import your own playlists. Spotify does not let other " +
            "players decode its audio, so the sound comes from the streaming source below.",
        Author: "AxePower",
        Kinds: new[] { ProviderKind.Metadata },
        Capabilities: new[] { "Search", "Your playlists" },
        RequiresSetup: true,
        SetupHint:
            "Create an app at developer.spotify.com, add " + SpotifyAuth.RedirectUri +
            " as a redirect URI, then paste the Client ID here and sign in.",
        PairedStreamingProviderId: "youtube");

    public bool IsSignedIn => _auth.IsSignedIn;

    public string? AccountName => _accountName;

    public event EventHandler? AuthChanged;

    public Task<ProviderState> GetStateAsync(CancellationToken ct)
    {
        if (!_auth.HasClientId || !_auth.IsSignedIn) return Task.FromResult(ProviderState.NeedsSetup);
        return Task.FromResult(ProviderState.Ready);
    }

    public async Task<bool> SignInAsync(CancellationToken ct)
    {
        bool ok = await _auth.SignInAsync(ct);
        if (ok)
        {
            try { _accountName = (await GetAsync("me", ct)).TryGetProperty("display_name", out var name) ? name.GetString() : null; }
            catch { _accountName = null; }
            AuthChanged?.Invoke(this, EventArgs.Empty);
        }
        return ok;
    }

    public void SignOut() => _auth.SignOut();

    // ── Metadata ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Track>> SearchTracksAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<Track>();

        string path = $"search?q={Uri.EscapeDataString(query)}&type=track&limit={Math.Clamp(limit, 1, 50)}";
        var root = await GetAsync(path, ct);

        var tracks = new List<Track>();
        if (root.TryGetProperty("tracks", out var section) && section.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ReadTrack(item) is { } track) tracks.Add(track);
            }
        }
        return tracks;
    }

    public async Task<IReadOnlyList<ProviderPlaylist>> GetPlaylistsAsync(CancellationToken ct)
    {
        var playlists = new List<ProviderPlaylist>();
        var root = await GetAsync("me/playlists?limit=50", ct);

        if (!root.TryGetProperty("items", out var items)) return playlists;

        foreach (var item in items.EnumerateArray())
        {
            string? id = Text(item, "id");
            if (id is null) continue;

            playlists.Add(new ProviderPlaylist(
                Id: id,
                Name: Text(item, "name") ?? "Untitled playlist",
                Source: new ProviderRef(Info.Id, id, Text(item, "external_urls", "spotify")),
                TrackCount: item.TryGetProperty("tracks", out var t) && t.TryGetProperty("total", out var total) ? total.GetInt32() : 0,
                ImageUrl: FirstImage(item),
                Owner: item.TryGetProperty("owner", out var owner) ? Text(owner, "display_name") : null));
        }

        return playlists;
    }

    public async Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, int limit, CancellationToken ct)
    {
        var tracks = new List<Track>();
        int pageSize = Math.Clamp(limit, 1, 100);
        string? path = $"playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit={pageSize}";

        // Spotify pages long playlists, and "next" already carries the offset, so it is
        // followed rather than recomputed.
        while (path is not null && tracks.Count < limit)
        {
            var root = await GetAsync(path, ct);
            if (!root.TryGetProperty("items", out var items)) break;

            foreach (var entry in items.EnumerateArray())
            {
                if (tracks.Count >= limit) break;

                // A removed or local-only entry has a null track, and podcast episodes have a
                // different shape; both are skipped rather than turned into empty rows.
                if (!entry.TryGetProperty("track", out var item) || item.ValueKind != JsonValueKind.Object) continue;
                if (ReadTrack(item) is { } track) tracks.Add(track);
            }

            string? next = Text(root, "next");
            path = next is null ? null : next.Replace(ApiBase, string.Empty, StringComparison.Ordinal);
        }

        return tracks;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        string token = await _auth.GetAccessTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Spotify rejected the sign-in. Sign in again.",
                HttpStatusCode.TooManyRequests => "Spotify is rate-limiting AxePower. Try again shortly.",
                HttpStatusCode.Forbidden => "This Spotify account is not allowed to use your app yet.",
                _ => $"Spotify returned {(int)response.StatusCode}."
            });
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A Spotify track carries no playable URL, so the result has a source but no candidates:
    /// the active streaming provider finds the audio when the track is played.
    /// </summary>
    private Track? ReadTrack(JsonElement item)
    {
        string? id = Text(item, "id");
        string? title = Text(item, "name");
        if (id is null || title is null) return null;

        var artists = new List<string>();
        if (item.TryGetProperty("artists", out var artistArray))
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                if (Text(artist, "name") is { Length: > 0 } name) artists.Add(name);
            }
        }
        if (artists.Count == 0) artists.Add("Unknown artist");

        var duration = item.TryGetProperty("duration_ms", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromMilliseconds(ms.GetDouble())
            : TimeSpan.Zero;

        string? album = item.TryGetProperty("album", out var albumElement) ? Text(albumElement, "name") : null;
        string? art = item.TryGetProperty("album", out var coverElement) ? FirstImage(coverElement) : null;

        return Track.ForProvider(
            source: new ProviderRef(Info.Id, id, Text(item, "external_urls", "spotify")),
            title: title,
            artists: artists,
            duration: duration,
            album: album,
            thumbnailUrl: art);
    }

    private static string? FirstImage(JsonElement element) =>
        element.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array
            ? FirstUrl(images)
            : null;

    private static string? FirstUrl(JsonElement images)
    {
        foreach (var image in images.EnumerateArray())
        {
            if (Text(image, "url") is { Length: > 0 } url) return url;
        }
        return null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Text(JsonElement element, string property, string nested) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, nested)
            : null;
}
