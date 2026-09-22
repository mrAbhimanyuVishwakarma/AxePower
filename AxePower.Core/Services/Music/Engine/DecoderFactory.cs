using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace AxePower.Core.Services.Music;

/// <summary>A file AxePower Music cannot decode (e.g. APE, DSD, WavPack).</summary>
public sealed class UnsupportedFormatException : Exception
{
    public string Extension { get; }

    public UnsupportedFormatException(string extension)
        : base($"Unsupported format ({(string.IsNullOrEmpty(extension) ? "unknown" : extension)})")
    {
        Extension = extension;
    }
}

public enum DecoderKind
{
    Wave,
    Aiff,
    MediaFoundation,
    Vorbis,
    Opus
}

/// <summary>
/// Picks and opens the decoder for a file. Media Foundation objects must only be created and
/// read on background (MTA) threads, never on the WPF UI thread — see <see cref="Open"/>.
/// </summary>
public static class DecoderFactory
{
    private static readonly Dictionary<string, DecoderKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".wav"] = DecoderKind.Wave,
        [".aif"] = DecoderKind.Aiff,
        [".aiff"] = DecoderKind.Aiff,
        [".mp3"] = DecoderKind.MediaFoundation,
        [".m4a"] = DecoderKind.MediaFoundation,
        [".mp4"] = DecoderKind.MediaFoundation,
        [".aac"] = DecoderKind.MediaFoundation,
        [".flac"] = DecoderKind.MediaFoundation,
        [".wma"] = DecoderKind.MediaFoundation,
        [".ogg"] = DecoderKind.Vorbis,   // refined by sniffing: may contain Opus
        [".oga"] = DecoderKind.Vorbis,
        [".opus"] = DecoderKind.Opus,
    };

    /// <summary>Extensions AxePower Music can play, e.g. for file dialogs and drop filtering.</summary>
    public static IReadOnlyCollection<string> SupportedExtensions => ByExtension.Keys;

    public static bool IsSupported(string path) =>
        ByExtension.ContainsKey(Path.GetExtension(path ?? string.Empty));

    /// <summary>
    /// Decides which decoder handles a file. <paramref name="readHeader"/> supplies the first
    /// bytes of the file and is only called for Ogg containers, which can hold Vorbis or Opus.
    /// </summary>
    public static DecoderKind Classify(string path, Func<byte[]> readHeader)
    {
        string ext = Path.GetExtension(path ?? string.Empty);
        if (!ByExtension.TryGetValue(ext, out var kind))
            throw new UnsupportedFormatException(ext);

        if (kind == DecoderKind.Vorbis && ContainsOpusHead(readHeader()))
            return DecoderKind.Opus;

        return kind;
    }

    /// <summary>True when an Ogg stream's first page announces an Opus codec.</summary>
    public static bool ContainsOpusHead(byte[] header)
    {
        if (header == null || header.Length < 36) return false;
        // The identification packet sits right after the first page header; searching the
        // leading bytes is enough and tolerates a variable segment table.
        int limit = Math.Min(header.Length, 512);
        byte[] marker = Encoding.ASCII.GetBytes("OpusHead");
        for (int i = 0; i + marker.Length <= limit; i++)
        {
            int j = 0;
            while (j < marker.Length && header[i + j] == marker[j]) j++;
            if (j == marker.Length) return true;
        }
        return false;
    }

    /// <summary>Opens a decoder positioned at the start. Call on a background (MTA) thread.</summary>
    internal static IDecoder Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The file no longer exists.", path);

        var kind = Classify(path, () => ReadHeader(path, 512));
        return kind switch
        {
            DecoderKind.Wave => new WaveStreamDecoder(new WaveFileReader(path)),
            DecoderKind.Aiff => new WaveStreamDecoder(new AiffFileReader(path)),
            DecoderKind.MediaFoundation => OpenMediaFoundation(path),
            DecoderKind.Vorbis => new WaveStreamDecoder(new NAudio.Vorbis.VorbisWaveReader(path)),
            DecoderKind.Opus => new OpusDecoder(path),
            _ => throw new UnsupportedFormatException(Path.GetExtension(path))
        };
    }

    private static readonly object MfStartupLock = new();
    private static bool _mfStarted;

    private static IDecoder OpenMediaFoundation(string path)
    {
        lock (MfStartupLock)
        {
            if (!_mfStarted)
            {
                NAudio.MediaFoundation.MediaFoundationApi.Startup();
                _mfStarted = true;
            }
        }

        var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true
        });
        return new WaveStreamDecoder(reader);
    }

    private static byte[] ReadHeader(string path, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[count];
        int read = fs.Read(buffer, 0, count);
        Array.Resize(ref buffer, read);
        return buffer;
    }
}
