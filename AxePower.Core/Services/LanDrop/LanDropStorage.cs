using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Everything LAN Drop keeps on disk or in memory about files: the download folder, files the PC
/// offers to phones, files received from them, and the staging that makes an upload appear only
/// once it is complete. No networking here: uploads arrive as a <see cref="Stream"/>.
/// </summary>
internal sealed class LanDropStorage
{
    private readonly LanDropSecurityLimits _limits;
    private readonly Func<string, long> _diskSpaceChecker;
    private readonly ConcurrentDictionary<string, LanDropFileItem> _sharedFiles = new();
    private readonly List<LanDropFileItem> _receivedFiles = new();
    private string _downloadDirectory;

    public LanDropStorage(string downloadDirectory, LanDropSecurityLimits limits, Func<string, long>? diskSpaceChecker = null)
    {
        _limits = limits;
        _diskSpaceChecker = diskSpaceChecker ?? DefaultDiskSpaceChecker;
        Directory.CreateDirectory(downloadDirectory);
        _downloadDirectory = downloadDirectory;
        CleanUpOrphanStagingFiles(downloadDirectory);
    }

    /// <summary>Where received files land. Setting it clears staging files a crash left there.</summary>
    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set
        {
            _downloadDirectory = value;
            if (Directory.Exists(value)) CleanUpOrphanStagingFiles(value); // never throws
        }
    }

    public long FreeSpace => _diskSpaceChecker(DownloadDirectory);

    /// <summary>Whether an upload of <paramref name="contentLength"/> bytes would leave the required headroom.</summary>
    public bool HasRoomFor(long contentLength)
    {
        long free = FreeSpace;
        return free >= _limits.MinFreeDiskSpaceBytes && (contentLength <= 0 || free >= contentLength + _limits.MinFreeDiskSpaceBytes);
    }

    // -- Files the PC shares --------------------------------------------------------------

    public LanDropFileItem? AddShared(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        var fileInfo = new FileInfo(filePath);
        var item = new LanDropFileItem
        {
            FileName = fileInfo.Name,
            FilePath = fileInfo.FullName,
            SizeBytes = fileInfo.Length,
            IsReceived = false,
            Timestamp = DateTime.Now,
            DeviceName = Environment.MachineName
        };
        _sharedFiles[item.Id] = item;
        return item;
    }

    public void RemoveShared(string id) => _sharedFiles.TryRemove(id, out _);

    public void ClearShared() => _sharedFiles.Clear();

    public IReadOnlyList<LanDropFileItem> GetShared() => _sharedFiles.Values.OrderByDescending(f => f.Timestamp).ToList();

    /// <summary>The shared file with this id, if it is still on disk.</summary>
    public LanDropFileItem? FindDownload(string? id) =>
        !string.IsNullOrEmpty(id) && _sharedFiles.TryGetValue(id, out var item) && File.Exists(item.FilePath) ? item : null;

    /// <summary>Copies a shared file's bytes to <paramref name="destination"/>.</summary>
    public static async Task CopyToAsync(LanDropFileItem item, Stream destination, CancellationToken ct)
    {
        await using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[65536];
        int read;
        while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destination.WriteAsync(buffer, 0, read, ct);
        }
    }

    // -- Files received from phones ------------------------------------------------------

    public IReadOnlyList<LanDropFileItem> GetReceived()
    {
        lock (_receivedFiles) return _receivedFiles.OrderByDescending(f => f.Timestamp).ToList();
    }

    public void ClearReceived()
    {
        lock (_receivedFiles) _receivedFiles.Clear();
    }

    /// <summary>
    /// Streams a multipart body into the download folder through staging files, then records
    /// every file it completed. Throws <see cref="System.Security.SecurityException"/>,
    /// <see cref="InvalidDataException"/> or <see cref="IOException"/> as the parser does.
    /// </summary>
    public async Task<IReadOnlyList<LanDropFileItem>> ReceiveMultipartAsync(Stream body, string boundary, string userAgent, CancellationToken ct)
    {
        var staged = await StreamingMultipartParser.ParseAndStageAsync(body, boundary, DownloadDirectory, _limits, _diskSpaceChecker, ct);
        return staged.Select(file => Record(file.FinalFilePath, userAgent)).ToList();
    }

    /// <summary>
    /// Streams exactly <paramref name="contentLength"/> bytes into a staging file, then moves it
    /// to a unique name in the download folder. Nothing appears there unless it all arrived.
    /// </summary>
    public async Task<LanDropFileItem> ReceiveRawAsync(Stream body, string safeFileName, long contentLength, string userAgent, CancellationToken ct)
    {
        string stagingPath = Path.Combine(DownloadDirectory, $".landrop_{Guid.NewGuid():N}.tmp");
        bool committed = false;
        long bytesWritten = 0;

        try
        {
            await using (var stagingFs = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, useAsync: true))
            {
                var buf = new byte[32768];
                long remaining = contentLength;

                while (remaining > 0)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(_limits.ReadTimeout);

                    int toRead = (int)Math.Min(buf.Length, remaining);
                    int read = await body.ReadAsync(buf.AsMemory(0, toRead), readCts.Token);
                    if (read == 0)
                    {
                        throw new IOException("Client disconnected before upload completed.");
                    }

                    await stagingFs.WriteAsync(buf.AsMemory(0, read), ct);
                    bytesWritten += read;
                    remaining -= read;

                    if (bytesWritten > _limits.MaxFileSizeBytes)
                    {
                        throw new IOException($"Upload file exceeded maximum allowed size of {_limits.MaxFileSizeBytes} bytes.");
                    }
                }
                await stagingFs.FlushAsync(ct);
            }

            if (FreeSpace < _limits.MinFreeDiskSpaceBytes)
            {
                throw new IOException("Insufficient disk space remained after writing upload.");
            }

            string finalPath = LanDropPathSanitizer.GetUniqueContainedPath(DownloadDirectory, safeFileName);
            File.Move(stagingPath, finalPath);
            committed = true;
            return Record(finalPath, userAgent);
        }
        finally
        {
            if (!committed && File.Exists(stagingPath))
            {
                try { File.Delete(stagingPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort: an orphaned staging file is removed the next time the folder is set. */ }
            }
        }
    }

    private LanDropFileItem Record(string filePath, string userAgent)
    {
        var fi = new FileInfo(filePath);
        var item = new LanDropFileItem
        {
            FileName = fi.Name,
            FilePath = fi.FullName,
            SizeBytes = fi.Length,
            IsReceived = true,
            Timestamp = DateTime.Now,
            DeviceName = LanDropDeviceNames.ForReceivedFile(userAgent)
        };

        lock (_receivedFiles)
        {
            _receivedFiles.Insert(0, item);
            if (_receivedFiles.Count > _limits.MaxSessionFilesCount) _receivedFiles.RemoveAt(_receivedFiles.Count - 1);
        }
        return item;
    }

    public static void CleanUpOrphanStagingFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in new DirectoryInfo(directory).GetFiles(".landrop_*.tmp"))
            {
                try { file.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Best-effort: still in use by a transfer; removed next time. */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best-effort housekeeping: an unreadable download folder leaves the temp files for later.
        }
    }

    private static long DefaultDiskSpaceChecker(string directory)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown free space (a network or removable folder): uploads are still capped per file
            // and per request, so the check is skipped rather than refusing every upload.
            return long.MaxValue;
        }
    }
}

/// <summary>Quick-text messages exchanged with phones, newest first, capped in number.</summary>
internal sealed class LanDropClipboard
{
    private readonly List<LanDropTextMessage> _messages = new();
    private readonly int _max;

    public LanDropClipboard(int max) => _max = max;

    public void Add(LanDropTextMessage message)
    {
        lock (_messages)
        {
            _messages.Insert(0, message);
            if (_messages.Count > _max) _messages.RemoveAt(_messages.Count - 1);
        }
    }

    public string? Latest
    {
        get { lock (_messages) return _messages.FirstOrDefault()?.Content; }
    }

    public IReadOnlyList<LanDropTextMessage> GetAll()
    {
        lock (_messages) return _messages.ToList();
    }

    public void Clear()
    {
        lock (_messages) _messages.Clear();
    }
}

/// <summary>
/// How a phone is labelled, from its User-Agent. The three places use slightly different labels
/// (they always have), so each keeps its own mapping.
/// </summary>
internal static class LanDropDeviceNames
{
    /// <summary>The connected-devices list.</summary>
    public static string ForSession(string userAgent) =>
        userAgent.Contains("iPhone") ? "Apple iPhone" :
        userAgent.Contains("iPad") ? "Apple iPad" :
        userAgent.Contains("Android") ? "Android Device" :
        userAgent.Contains("Macintosh") ? "Mac" :
        userAgent.Contains("Windows") ? "Windows PC" : "LAN Client";

    /// <summary>A received file's "from" label.</summary>
    public static string ForReceivedFile(string userAgent) =>
        userAgent.Contains("iPhone") ? "iPhone" :
        userAgent.Contains("Android") ? "Android Phone" :
        userAgent.Contains("iPad") ? "iPad" : "Mobile Client";

    /// <summary>A quick-text message's sender.</summary>
    public static string ForTextMessage(string userAgent) =>
        userAgent.Contains("iPhone") ? "iPhone" :
        userAgent.Contains("Android") ? "Android Phone" : "Mobile Client";
}
