using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Reads tags, covers and lyrics from audio files. Tag writing (the tag editor) is added in
/// Phase 6 together with its backup and undo support.
/// </summary>
public interface ITagReader
{
    /// <summary>Throws <see cref="TagReadException"/> when the file cannot be read.</summary>
    TrackTags Read(string path);

    byte[]? ReadEmbeddedCover(string path);

    /// <summary>Synced lyrics (SYLT) as LRC text, else unsynced lyrics (USLT / LYRICS), else null.</summary>
    string? ReadEmbeddedLyrics(string path);
}
