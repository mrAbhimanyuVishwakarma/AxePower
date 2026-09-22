using System;
using System.Collections.Generic;

namespace AxePower.Core.Models.Music;

/// <summary>What the audio device is actually doing. Built only from real engine state.</summary>
public sealed record OutputInfo(
    string DeviceName,
    bool Exclusive,
    int SampleRate,
    int BitsPerSample,
    int Channels,
    bool BitPerfect,                        // true ONLY when: exclusive, native rate, no active DSP, output bits >= source bits
    string? FallbackReason);                // e.g. "Exclusive mode unavailable: device in use"

/// <summary>
/// An immutable snapshot of the player, broadcast to every integration and surface.
/// </summary>
public sealed record PlayerState(
    IReadOnlyList<QueueItem> Queue,
    int Index,                              // -1 when the queue is empty
    bool Playing,
    bool Buffering,
    bool Completed,
    TimeSpan Position,
    TimeSpan Duration,
    double Volume,                          // 0.0–2.0 (1.0 = 100%)
    bool Muted,
    bool Shuffle,
    RepeatMode Repeat,
    double Speed,                           // 0.5–2.0
    double PitchSemitones,                  // -12..+12
    int? MixOffset,
    OutputInfo? Output,
    string? Error)
{
    public QueueItem? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

    public static PlayerState Empty { get; } = new(Array.Empty<QueueItem>(), -1, false, false, false,
        TimeSpan.Zero, TimeSpan.Zero, 1.0, false, false, RepeatMode.Off, 1.0, 0, null, null, null);
}
