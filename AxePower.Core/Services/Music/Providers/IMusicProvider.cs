using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// A source of music. Providers are registered in DI and listed in Settings → Music → Sources;
/// they are never loaded from disk as third-party code, because AxePower runs elevated.
/// </summary>
public interface IMusicProvider
{
    ProviderInfo Info { get; }

    /// <summary>Must not throw. An unreachable provider reports <see cref="ProviderState.Unavailable"/>.</summary>
    Task<ProviderState> GetStateAsync(CancellationToken ct);
}

/// <summary>
/// Turns a track into playable audio in two phases: find candidates, then resolve one of them
/// just before it is needed. Splitting the two keeps expired URLs out of the queue and lets the
/// listener swap a wrong match without a new search.
/// </summary>
public interface IStreamingProvider : IMusicProvider
{
    /// <summary>True for the local-file provider, which plays a track's own path.</summary>
    bool SupportsLocalFiles { get; }

    /// <summary>
    /// Best matches for a track, best first. Returns an empty list when nothing matches;
    /// failures throw so the caller can report them.
    /// </summary>
    Task<IReadOnlyList<StreamCandidate>> FindCandidatesAsync(Track track, CancellationToken ct);

    /// <summary>
    /// The playable URL for one candidate, or null when this candidate cannot be played.
    /// When <paramref name="preferLocalCopy"/> is set the provider downloads to a cache file
    /// instead of returning a remote URL — the fallback for sources that will not stream.
    /// </summary>
    Task<AudioStream?> ResolveAsync(StreamCandidate candidate, bool preferLocalCopy, CancellationToken ct);
}

/// <summary>
/// Search and browse. A metadata provider does not have to supply audio: pairing one with a
/// streaming provider (Spotify's catalogue, YouTube's audio) is the normal arrangement.
/// </summary>
public interface IMetadataProvider : IMusicProvider
{
    Task<IReadOnlyList<Track>> SearchTracksAsync(string query, int limit, CancellationToken ct);

    /// <summary>The signed-in user's playlists. Empty when the provider has no concept of them.</summary>
    Task<IReadOnlyList<ProviderPlaylist>> GetPlaylistsAsync(CancellationToken ct);

    Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, int limit, CancellationToken ct);
}

/// <summary>A provider the user signs into.</summary>
public interface IAuthenticatedProvider
{
    bool IsSignedIn { get; }

    string? AccountName { get; }

    event EventHandler? AuthChanged;

    Task<bool> SignInAsync(CancellationToken ct);

    void SignOut();
}
