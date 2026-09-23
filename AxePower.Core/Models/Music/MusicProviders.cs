using System;
using System.Collections.Generic;

namespace AxePower.Core.Models.Music;

/// <summary>What a provider contributes. A single provider may cover several kinds.</summary>
public enum ProviderKind
{
    /// <summary>Turns a track into playable audio.</summary>
    Streaming,

    /// <summary>Supplies search results, playlists and track details.</summary>
    Metadata,

    /// <summary>Supplies lyrics.</summary>
    Lyrics,

    /// <summary>Suggests what to play next, to keep a queue going on its own.</summary>
    Discovery
}

/// <summary>
/// How adventurous a set of recommendations should be. <see cref="Variety"/> runs 0–1: at the
/// bottom it stays with the artists already playing, at the top it looks for music around them.
/// </summary>
public sealed record DiscoveryOptions(double Variety, int Limit)
{
    public static DiscoveryOptions Default { get; } = new(0.5, 10);
}

/// <summary>Where an item came from. <see cref="Provider"/> is a provider id such as "youtube".</summary>
public sealed record ProviderRef(string Provider, string Id, string? Url = null);

/// <summary>How the audio for a stream is reached.</summary>
public enum StreamProtocol
{
    File,
    Http,
    Https
}

/// <summary>
/// A resolved, playable audio location. Remote streams expire, so
/// <see cref="ResolvedUtc"/> decides when one has to be fetched again.
/// </summary>
public sealed record AudioStream(
    string Url,
    StreamProtocol Protocol,
    ProviderRef Source,
    string? Codec = null,
    string? Container = null,
    int? BitrateKbps = null,
    TimeSpan? Duration = null,
    DateTime? ResolvedUtc = null)
{
    public bool IsFile => Protocol == StreamProtocol.File;

    /// <summary>A file stream never expires; a remote one does, because signed URLs are short-lived.</summary>
    public bool IsExpired(DateTime utcNow, TimeSpan lifetime) =>
        !IsFile && (ResolvedUtc is null || utcNow - ResolvedUtc.Value >= lifetime);
}

/// <summary>
/// A possible audio source for a track, before its URL is resolved. Providers usually return
/// several, so playback can move down the list when one turns out to be unplayable, and so the
/// listener can pick a different take when the automatic match is wrong.
/// </summary>
public sealed record StreamCandidate(
    string Id,
    string Title,
    ProviderRef Source,
    string? Artist = null,
    TimeSpan? Duration = null,
    string? ThumbnailUrl = null,
    AudioStream? Stream = null,
    bool Failed = false);

/// <summary>Whether a provider can be used right now, and why not when it cannot.</summary>
public enum ProviderState
{
    /// <summary>Enabled and usable.</summary>
    Ready,

    /// <summary>Enabled, but the user has to sign in or supply a key first.</summary>
    NeedsSetup,

    /// <summary>Switched off by the user.</summary>
    Disabled,

    /// <summary>Enabled and set up, but unreachable (no network, service down).</summary>
    Unavailable
}

/// <summary>
/// The provider's card in the Sources list: what it is, who wrote it and what it can do.
/// Everything here is static; live status comes from <see cref="ProviderState"/>.
/// </summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    string Description,
    string Author,
    IReadOnlyList<ProviderKind> Kinds,
    IReadOnlyList<string> Capabilities,
    bool RequiresSetup = false,
    string? SetupHint = null,
    bool CanBeDisabled = true,
    string? PairedStreamingProviderId = null);

/// <summary>A playlist offered by a metadata provider, before its tracks are fetched.</summary>
public sealed record ProviderPlaylist(
    string Id,
    string Name,
    ProviderRef Source,
    int TrackCount = 0,
    string? ImageUrl = null,
    string? Owner = null);
