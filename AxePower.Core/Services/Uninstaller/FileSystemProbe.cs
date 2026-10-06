using System.Collections.Concurrent;
using System.Text;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>
/// The file-system questions discovery asks. Tests pass a fake so classification rules run
/// against fixture data instead of whatever is installed on the machine.
/// </summary>
public interface IFileSystemProbe
{
    /// <summary>True for an existing file. Relative paths always return false.</summary>
    bool FileExists(string path);

    /// <summary>True for an existing directory. Relative paths always return false.</summary>
    bool DirectoryExists(string path);

    /// <summary>True when the first <paramref name="maxBytes"/> of the file contain the ASCII text.</summary>
    bool FileContainsAscii(string path, string text, long maxBytes);
}

/// <summary>The real file system. Content scans are cached by path, size and last-write time.</summary>
public sealed class FileSystemProbe : IFileSystemProbe
{
    private readonly ConcurrentDictionary<(string Path, long Size, DateTime WriteUtc, string Text), bool> _scanCache = new();

    public bool FileExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);

    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path);

    public bool FileContainsAscii(string path, string text, long maxBytes)
    {
        if (!FileExists(path) || string.IsNullOrEmpty(text)) return false;

        try
        {
            var info = new FileInfo(path);
            var key = (path.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc, text);
            return _scanCache.GetOrAdd(key, _ => Scan(path, Encoding.ASCII.GetBytes(text), maxBytes));
        }
        catch (Exception caught) when (ExpectedFailures.FileSystem(caught))
        {
            // An unreadable file counts as not containing the text, so it is not taken as evidence.
            return false;
        }
    }

    private static bool Scan(string path, byte[] needle, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, FileOptions.SequentialScan);

        // Keep the last (needle.Length - 1) bytes of each chunk so a match across a chunk
        // boundary is still found.
        var buffer = new byte[(1 << 16) + needle.Length];
        int carried = 0;
        long total = 0;

        while (total < maxBytes)
        {
            int toRead = (int)Math.Min(buffer.Length - carried, maxBytes - total);
            int read = stream.Read(buffer, carried, toRead);
            if (read <= 0) break;
            total += read;

            var window = buffer.AsSpan(0, carried + read);
            if (window.IndexOf(needle) >= 0) return true;

            carried = Math.Min(needle.Length - 1, window.Length);
            window[^carried..].CopyTo(buffer);
        }

        return false;
    }
}
