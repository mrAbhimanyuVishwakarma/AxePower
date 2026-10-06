using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Result of streaming an uploaded file to disk.
/// </summary>
public sealed class StagedUploadResult
{
    public required string FinalFilePath { get; init; }
    public required string OriginalFileName { get; init; }
    public required long FileSizeBytes { get; init; }
}

/// <summary>
/// Memory-bounded, streaming multipart/form-data parser.
/// Streams files directly to uniquely named temporary staging files on disk in chunks,
/// enforcing strict size limits, timeout guards, and atomic promotion upon completion.
/// </summary>
public static class StreamingMultipartParser
{
    private static readonly byte[] CrlfBytes = "\r\n"u8.ToArray();
    private static readonly byte[] DoubleCrlfBytes = "\r\n\r\n"u8.ToArray();

    public const int MaxMultipartParts = 500;
    public const int MaxBoundaryLength = 70;

    public static async Task<List<StagedUploadResult>> ParseAndStageAsync(
        Stream inputStream,
        string boundary,
        string downloadDirectory,
        LanDropSecurityLimits limits,
        Func<string, long> diskSpaceChecker,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new ArgumentException("Multipart boundary cannot be empty or whitespace.", nameof(boundary));
        }

        // RFC 2046 caps a boundary at 70 characters. The delimiter scan costs body length times
        // boundary length, so a 15,000-character boundary from a large request head turned a
        // 2 GB upload into hours of CPU (found by MultipartStagingFuzzTests).
        if (boundary.Length > MaxBoundaryLength)
        {
            throw new InvalidDataException($"Multipart boundary is {boundary.Length} characters; the limit is {MaxBoundaryLength}.");
        }

        var results = new List<StagedUploadResult>();
        byte[] firstBoundary = Encoding.ASCII.GetBytes("--" + boundary);
        byte[] delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);

        var reader = new ChunkedStreamReader(inputStream, limits.ReadTimeout);

        // 1. Seek to first boundary
        bool foundStart = await reader.SkipToBoundaryAsync(firstBoundary, ct);
        if (!foundStart) return results;

        // Skip the initial boundary line ending (\r\n)
        await reader.ConsumeLineEndingAsync(ct);

        long totalUploadedAllParts = 0;
        int partCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (++partCount > MaxMultipartParts)
            {
                throw new InvalidDataException($"Multipart request exceeded maximum allowed part count of {MaxMultipartParts}.");
            }

            // 2. Read part headers (bounded by MaxMultipartPartHeaderSizeBytes)
            byte[]? headerBytes = await reader.ReadUntilPatternAsync(DoubleCrlfBytes, limits.MaxMultipartPartHeaderSizeBytes, ct);
            if (headerBytes == null)
            {
                // No more parts or stream ended
                break;
            }

            string headerText = Encoding.UTF8.GetString(headerBytes);
            string? filename = ExtractFilenameFromHeaders(headerText);

            if (filename != null)
            {
                // Check traversal attack in filename
                if (LanDropPathSanitizer.IsPathTraversalAttempt(filename))
                {
                    throw new SecurityException($"Malicious filename traversal attempt rejected: {filename}");
                }

                // Check free disk space before staging
                long freeSpace = diskSpaceChecker(downloadDirectory);
                if (freeSpace < limits.MinFreeDiskSpaceBytes)
                {
                    throw new IOException($"Insufficient disk space on destination. Available: {freeSpace}, required headroom: {limits.MinFreeDiskSpaceBytes}");
                }

                string stagingPath = Path.Combine(downloadDirectory, $".landrop_{Guid.NewGuid():N}.tmp");
                bool committed = false;
                long writtenBytes = 0;

                try
                {
                    await using (var stagingFs = new FileStream(
                        stagingPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 32768,
                        useAsync: true))
                    {
                        writtenBytes = await reader.StreamUntilDelimiterAsync(
                            delimiter,
                            stagingFs,
                            maxBytes: limits.MaxFileSizeBytes,
                            ct: ct);

                        await stagingFs.FlushAsync(ct);
                    }

                    totalUploadedAllParts += writtenBytes;
                    if (totalUploadedAllParts > limits.MaxTotalRequestSizeBytes)
                    {
                        throw new IOException($"Total upload size exceeded maximum total request limit of {limits.MaxTotalRequestSizeBytes} bytes.");
                    }

                    // Check disk space after writing
                    long remainingSpace = diskSpaceChecker(downloadDirectory);
                    if (remainingSpace < limits.MinFreeDiskSpaceBytes)
                    {
                        throw new IOException("Insufficient disk space remained after streaming part.");
                    }

                    // Normalize filename and promote atomically
                    string safeFileName = LanDropPathSanitizer.NormalizeFileName(filename);
                    string finalPath = LanDropPathSanitizer.GetUniqueContainedPath(downloadDirectory, safeFileName);

                    File.Move(stagingPath, finalPath);
                    committed = true;

                    results.Add(new StagedUploadResult
                    {
                        FinalFilePath = finalPath,
                        OriginalFileName = safeFileName,
                        FileSizeBytes = writtenBytes
                    });
                }
                finally
                {
                    if (!committed && File.Exists(stagingPath))
                    {
                        try { File.Delete(stagingPath); } catch (Exception caught) when (ExpectedFailures.FileSystem(caught)) { /* Best-effort: an orphaned staging file is removed when the download folder is next set. */ }
                    }
                }
            }
            else
            {
                // Non-file field: discard content up to next delimiter
                using var devNull = Stream.Null;
                await reader.StreamUntilDelimiterAsync(delimiter, devNull, maxBytes: limits.MaxTextMessageSizeBytes, ct: ct);
            }

            // Check if boundary is followed by "--" (end of multipart) or "\r\n"
            bool isEnd = await reader.CheckAndConsumeEndBoundaryAsync(ct);
            if (isEnd) break;
        }

        return results;
    }

    internal static string? ExtractFilenameFromHeaders(string headerText)
    {
        int fnIdx = headerText.IndexOf("filename=\"", StringComparison.OrdinalIgnoreCase);
        if (fnIdx != -1)
        {
            int fnEnd = headerText.IndexOf('\"', fnIdx + 10);
            if (fnEnd != -1)
            {
                return headerText.Substring(fnIdx + 10, fnEnd - (fnIdx + 10));
            }
        }

        // Try filename*=UTF-8''...
        int fnStarIdx = headerText.IndexOf("filename*=", StringComparison.OrdinalIgnoreCase);
        if (fnStarIdx != -1)
        {
            int lineEnd = headerText.IndexOf("\r\n", fnStarIdx, StringComparison.Ordinal);
            string line = lineEnd != -1
                ? headerText.Substring(fnStarIdx + 10, lineEnd - (fnStarIdx + 10))
                : headerText.Substring(fnStarIdx + 10);

            line = line.Trim('\"', ' ', ';');
            int utf8Prefix = line.IndexOf("UTF-8''", StringComparison.OrdinalIgnoreCase);
            if (utf8Prefix != -1)
            {
                string encoded = line.Substring(utf8Prefix + 7);
                try
                {
                    return Uri.UnescapeDataString(encoded);
                }
                catch (UriFormatException)
                {
                    return WebUtility.UrlDecode(encoded);
                }
            }
            return line;
        }

        return null;
    }

    /// <summary>
    /// Sliding window buffered reader for streaming delimiter scanning with memory bounds.
    /// </summary>
    private sealed class ChunkedStreamReader
    {
        private readonly Stream _stream;
        private readonly TimeSpan _readTimeout;
        private readonly byte[] _readBuffer = new byte[32768];
        private readonly List<byte> _internalBuffer = new(65536);

        public ChunkedStreamReader(Stream stream, TimeSpan readTimeout)
        {
            _stream = stream;
            _readTimeout = readTimeout;
        }

        private async Task<int> ReadMoreAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_readTimeout);

            int read = await _stream.ReadAsync(_readBuffer.AsMemory(0, _readBuffer.Length), cts.Token);
            if (read > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    _internalBuffer.Add(_readBuffer[i]);
                }
            }
            return read;
        }

        public async Task<bool> SkipToBoundaryAsync(byte[] boundary, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int idx = FindPattern(_internalBuffer, boundary, 0);
                if (idx != -1)
                {
                    _internalBuffer.RemoveRange(0, idx + boundary.Length);
                    return true;
                }

                if (_internalBuffer.Count > boundary.Length)
                {
                    _internalBuffer.RemoveRange(0, _internalBuffer.Count - boundary.Length);
                }

                int read = await ReadMoreAsync(ct);
                if (read == 0) return false;
            }
            return false;
        }

        public async Task ConsumeLineEndingAsync(CancellationToken ct)
        {
            while (_internalBuffer.Count < 2 && !ct.IsCancellationRequested)
            {
                int read = await ReadMoreAsync(ct);
                if (read == 0) break;
            }

            if (_internalBuffer.Count >= 2 && _internalBuffer[0] == '\r' && _internalBuffer[1] == '\n')
            {
                _internalBuffer.RemoveRange(0, 2);
            }
            else if (_internalBuffer.Count >= 1 && _internalBuffer[0] == '\n')
            {
                _internalBuffer.RemoveRange(0, 1);
            }
        }

        public async Task<byte[]?> ReadUntilPatternAsync(byte[] pattern, int maxBytes, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                int idx = FindPattern(_internalBuffer, pattern, 0);
                if (idx != -1)
                {
                    if (idx > maxBytes)
                    {
                        throw new InvalidDataException($"Header size of {idx} bytes exceeded limit of {maxBytes} bytes.");
                    }

                    byte[] matched = _internalBuffer.GetRange(0, idx).ToArray();
                    _internalBuffer.RemoveRange(0, idx + pattern.Length);
                    return matched;
                }

                if (_internalBuffer.Count > maxBytes + pattern.Length)
                {
                    throw new InvalidDataException($"Header exceeded maximum allowed size of {maxBytes} bytes.");
                }

                int read = await ReadMoreAsync(ct);
                if (read == 0)
                {
                    if (_internalBuffer.Count == 0) return null;
                    throw new InvalidDataException("Unexpected end of stream while reading headers.");
                }
            }
            return null;
        }

        public async Task<long> StreamUntilDelimiterAsync(
            byte[] delimiter,
            Stream outputStream,
            long maxBytes,
            CancellationToken ct)
        {
            long totalWritten = 0;
            int delLen = delimiter.Length;

            while (!ct.IsCancellationRequested)
            {
                int searchStart = 0;
                int matchedIdx = -1;

                while (true)
                {
                    int idx = FindPattern(_internalBuffer, delimiter, searchStart);
                    if (idx == -1) break;

                    // Ensure we have at least 1 byte after the delimiter to verify it's followed by a valid boundary trailer
                    while (_internalBuffer.Count < idx + delLen + 1 && !ct.IsCancellationRequested)
                    {
                        int read = await ReadMoreAsync(ct);
                        if (read == 0) break;
                    }

                    if (_internalBuffer.Count >= idx + delLen + 1)
                    {
                        byte trailer = _internalBuffer[idx + delLen];
                        // In RFC 2046, a valid boundary must be followed by CRLF (\r or \n), close boundary "--", or optional whitespace
                        if (trailer is (byte)'-' or (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
                        {
                            matchedIdx = idx;
                            break;
                        }
                    }
                    else
                    {
                        // Stream ended immediately after delimiter
                        matchedIdx = idx;
                        break;
                    }

                    // False boundary match (e.g. part of file data sharing a prefix with the boundary); continue searching
                    searchStart = idx + 1;
                }

                if (matchedIdx != -1)
                {
                    // Found delimiter! Write all bytes before delimiter
                    if (matchedIdx > 0)
                    {
                        totalWritten += matchedIdx;
                        if (totalWritten > maxBytes)
                        {
                            throw new IOException($"Upload file exceeded maximum allowed size of {maxBytes} bytes.");
                        }

                        byte[] chunk = _internalBuffer.GetRange(0, matchedIdx).ToArray();
                        await outputStream.WriteAsync(chunk.AsMemory(0, matchedIdx), ct);
                    }

                    _internalBuffer.RemoveRange(0, matchedIdx + delLen);
                    return totalWritten;
                }

                // If delimiter not yet found, we can safely write all bytes except the trailing (delLen - 1) bytes
                int safeToWrite = _internalBuffer.Count - delLen;
                if (safeToWrite > 0)
                {
                    totalWritten += safeToWrite;
                    if (totalWritten > maxBytes)
                    {
                        throw new IOException($"Upload file exceeded maximum allowed size of {maxBytes} bytes.");
                    }

                    byte[] chunk = _internalBuffer.GetRange(0, safeToWrite).ToArray();
                    await outputStream.WriteAsync(chunk.AsMemory(0, safeToWrite), ct);
                    _internalBuffer.RemoveRange(0, safeToWrite);
                }

                int readMore = await ReadMoreAsync(ct);
                if (readMore == 0)
                {
                    throw new InvalidDataException("Unexpected end of multipart stream: ending delimiter not found.");
                }
            }

            return totalWritten;
        }

        public async Task<bool> CheckAndConsumeEndBoundaryAsync(CancellationToken ct)
        {
            while (_internalBuffer.Count < 2 && !ct.IsCancellationRequested)
            {
                int read = await ReadMoreAsync(ct);
                if (read == 0) break;
            }

            if (_internalBuffer.Count >= 2 && _internalBuffer[0] == '-' && _internalBuffer[1] == '-')
            {
                _internalBuffer.RemoveRange(0, 2);
                await ConsumeLineEndingAsync(ct);
                return true;
            }

            await ConsumeLineEndingAsync(ct);
            return false;
        }

        private static int FindPattern(List<byte> buffer, byte[] pattern, int start)
        {
            int max = buffer.Count - pattern.Length;
            for (int i = start; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }
}
