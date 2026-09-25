using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Plays files that are already on this PC. It is always present and always enabled, so the
/// player keeps working with no online source configured and no network at all.
/// </summary>
public sealed class LocalFileProvider : IStreamingProvider
{
    public ProviderInfo Info { get; } = new(
        Id: ProviderRegistry.LocalProviderId,
        Name: "This PC",
        Description: "Plays music files from your own drives. Always available, works offline.",
        Author: "AxePower",
        Kinds: new[] { ProviderKind.Streaming },
        Capabilities: new[] { "Local files", "Gapless", "Offline" },
        RequiresSetup: false,
        CanBeDisabled: false);

    public bool SupportsLocalFiles => true;

    public Task<ProviderState> GetStateAsync(CancellationToken ct) => Task.FromResult(ProviderState.Ready);

    public Task<IReadOnlyList<StreamCandidate>> FindCandidatesAsync(Track track, CancellationToken ct)
    {
        // A local track is its own candidate; there is nothing to search for.
        if (string.IsNullOrEmpty(track.Path))
            return Task.FromResult<IReadOnlyList<StreamCandidate>>(Array.Empty<StreamCandidate>());

        var source = new ProviderRef(Info.Id, track.Path);
        return Task.FromResult<IReadOnlyList<StreamCandidate>>(new[]
        {
            new StreamCandidate(
                Id: track.Path,
                Title: track.Title,
                Source: source,
                Artist: track.Artists.Count > 0 ? track.Artists[0] : null,
                Duration: track.Duration,
                Stream: new AudioStream(track.Path, StreamProtocol.File, source))
        });
    }

    public Task<AudioStream?> ResolveAsync(StreamCandidate candidate, bool preferLocalCopy, CancellationToken ct) =>
        Task.FromResult<AudioStream?>(
            System.IO.File.Exists(candidate.Id)
                ? new AudioStream(candidate.Id, StreamProtocol.File, candidate.Source)
                : null);
}
