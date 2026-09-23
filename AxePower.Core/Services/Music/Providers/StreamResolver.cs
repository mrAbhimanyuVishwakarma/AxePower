using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>Why a track could not be turned into audio.</summary>
public sealed class StreamResolutionException : Exception
{
    public StreamResolutionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Turns a track into something the engine can open. Local files pass straight through; provider
/// tracks are searched, resolved and re-resolved as their URLs expire.
/// </summary>
public interface IStreamResolver
{
    /// <summary>
    /// A copy of the track with a usable <see cref="Track.ResolvedStream"/>. Throws
    /// <see cref="StreamResolutionException"/> when no candidate could be played.
    /// </summary>
    Task<Track> PrepareAsync(Track track, bool preferLocalCopy, CancellationToken ct);

    /// <summary>The candidate list for a track, searching for it if that has not happened yet.</summary>
    Task<Track> EnsureCandidatesAsync(Track track, CancellationToken ct);

    /// <summary>Switches a track to a candidate the listener picked, dropping the old stream.</summary>
    Track SelectCandidate(Track track, string candidateId);
}

/// <inheritdoc />
public sealed class StreamResolver : IStreamResolver
{
    private readonly ProviderRegistry _registry;
    private readonly IClock _clock;
    private readonly LogService _log;

    public StreamResolver(ProviderRegistry registry, IClock clock, LogService log)
    {
        _registry = registry;
        _clock = clock;
        _log = log;
    }

    public async Task<Track> PrepareAsync(Track track, bool preferLocalCopy, CancellationToken ct)
    {
        if (track.IsLocal) return track;

        var provider = _registry.Active<IStreamingProvider>(ProviderKind.Streaming)
            ?? throw new StreamResolutionException("No streaming source is switched on. Turn one on in Settings → Music → Sources.");

        // A stream that is still fresh is reused, unless a local copy was asked for and this one
        // is not already a file — that is the retry path after a progressive open failed.
        if (track.ResolvedStream is { } existing
            && !existing.IsExpired(_clock.UtcNow, _registry.StreamLifetime)
            && (!preferLocalCopy || existing.IsFile))
        {
            return track;
        }

        track = await EnsureCandidatesAsync(track, ct);
        var candidates = track.Candidates ?? Array.Empty<StreamCandidate>();
        if (candidates.Count == 0)
            throw new StreamResolutionException($"{provider.Info.Name} found no audio for '{track.Title}'.");

        // Start at the listener's pick when there is one, then work down the rest of the list.
        var ordered = Order(candidates, track.SelectedCandidateId);
        var updated = candidates.ToList();
        int attempts = 0;
        Exception? last = null;

        foreach (var candidate in ordered)
        {
            if (candidate.Failed) continue;
            if (attempts++ >= _registry.ResolutionRetries) break;
            ct.ThrowIfCancellationRequested();

            try
            {
                var stream = await provider.ResolveAsync(candidate, preferLocalCopy, ct);
                if (stream != null)
                {
                    var resolved = candidate with { Stream = stream, Failed = false };
                    Replace(updated, resolved);
                    return track with
                    {
                        Candidates = updated,
                        SelectedCandidateId = candidate.Id,
                        ResolvedStream = stream with { ResolvedUtc = _clock.UtcNow }
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                _log.LogWarning($"[Music] '{candidate.Title}' could not be resolved: {ex.Message}");
            }

            // Only a candidate that was actually tried and failed is struck off, so a
            // cancelled or skipped one stays available next time.
            Replace(updated, candidate with { Failed = true });
        }

        throw new StreamResolutionException(
            $"None of the {attempts} source{(attempts == 1 ? "" : "s")} for '{track.Title}' could be played.", last);
    }

    public async Task<Track> EnsureCandidatesAsync(Track track, CancellationToken ct)
    {
        if (track.IsLocal) return track;
        if (track.Candidates is { Count: > 0 } existing && existing.Any(c => !c.Failed)) return track;

        var provider = _registry.Active<IStreamingProvider>(ProviderKind.Streaming)
            ?? throw new StreamResolutionException("No streaming source is switched on.");

        IReadOnlyList<StreamCandidate> found;
        try
        {
            found = await provider.FindCandidatesAsync(track, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new StreamResolutionException($"{provider.Info.Name} could not be searched: {ex.Message}", ex);
        }

        return track with { Candidates = found, SelectedCandidateId = null, ResolvedStream = null };
    }

    public Track SelectCandidate(Track track, string candidateId)
    {
        if (track.Candidates is null || track.Candidates.All(c => c.Id != candidateId)) return track;

        // Picking again clears the failure mark, because the listener may know better than
        // the last attempt did — a transient network error should not blacklist a take forever.
        var candidates = track.Candidates
            .Select(c => c.Id == candidateId ? c with { Failed = false } : c)
            .ToList();

        return track with { Candidates = candidates, SelectedCandidateId = candidateId, ResolvedStream = null };
    }

    private static IEnumerable<StreamCandidate> Order(IReadOnlyList<StreamCandidate> candidates, string? preferredId)
    {
        if (preferredId is null) return candidates;
        return candidates
            .Where(c => c.Id == preferredId)
            .Concat(candidates.Where(c => c.Id != preferredId));
    }

    private static void Replace(List<StreamCandidate> list, StreamCandidate candidate)
    {
        int index = list.FindIndex(c => c.Id == candidate.Id);
        if (index >= 0) list[index] = candidate;
    }
}
