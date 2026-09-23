using System;

namespace AxePower.Core.Models.Music;

/// <summary>
/// One entry in the play queue. The key is unique per entry, so the same track can be queued
/// twice and each copy is still addressable.
/// </summary>
public sealed record QueueItem(Guid Key, Track Track)
{
    public static QueueItem For(Track track) => new(Guid.NewGuid(), track);
}
