using System;
using System.Collections.Generic;

namespace AxePower.Core.Models.Music;

/// <summary>A single synchronized lyric line. Timestamps retain millisecond precision.</summary>
public sealed record LyricLine(TimeSpan Timestamp, string Text);

/// <summary>Lyrics returned by an online or embedded provider.</summary>
public sealed record LyricsResult(
    string Provider,
    IReadOnlyList<LyricLine> Lines,
    string? PlainLyrics = null)
{
    public bool IsSynchronized => Lines.Count > 0;
}

