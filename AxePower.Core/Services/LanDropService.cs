using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Helpers;
using AxePower.Core.Models;

using AxePower.Core.Services.Music;

namespace AxePower.Core.Services;

public class LanDropService : IDisposable
{
    private readonly LogService _logService;

    /// <summary>
    /// Null only where the portal is constructed without security wired up (older tests). The
    /// server then refuses to start rather than listening unprotected.
    /// </summary>
    private readonly PortalAccessToken? _token;

    /// <summary>Null when the music player is not available; its routes then return 404.</summary>
    private readonly MusicRemote? _music;

    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    private readonly ConcurrentDictionary<string, LanDropFileItem> _sharedFiles = new();
    private readonly List<LanDropFileItem> _receivedFiles = new();
    private readonly List<LanDropTextMessage> _clipboardMessages = new();

    public event EventHandler<LanDropFileItem>? OnFileReceived;
    public event EventHandler<LanDropTextMessage>? OnTextReceived;
    public event EventHandler<LanDropServerState>? OnStateChanged;

    public string CurrentIpAddress { get; private set; } = string.Empty;
    public int CurrentPort { get; private set; } = 5050;
    public bool IsRunning => _tcpListener != null && _cts != null && !_cts.IsCancellationRequested;
    public string ServerUrl => IsRunning && !string.IsNullOrEmpty(CurrentIpAddress)
        ? $"http://{CurrentIpAddress}:{CurrentPort}/"
        : string.Empty;

    public string DownloadDirectory { get; set; }

    /// <summary>HTTP line endings, spelled out so they survive any source-file rewriting.</summary>
    private const string Crlf = "\r\n";
    private const string Lf = "\n";

    /// <summary>
    /// The address to open on a phone, including the access token. This is what the QR code
    /// encodes: the link is the credential, so it must not be shared any further than intended.
    /// </summary>
    public string PortalUrl => IsRunning && _token is not null && !string.IsNullOrEmpty(CurrentIpAddress)
        ? $"http://{CurrentIpAddress}:{CurrentPort}/?{PortalAccessToken.QueryName}={_token.Value}"
        : string.Empty;

    // Win32 Media, Lock, and Power simulation
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public LanDropService(LogService logService, PortalAccessToken? token = null, MusicRemote? music = null)
    {
        _logService = logService;
        _token = token;
        _music = music;
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "AxePower_LanDrop");
        Directory.CreateDirectory(downloads);
        DownloadDirectory = downloads;

        var ips = GetLocalIpAddresses();
        CurrentIpAddress = ips.FirstOrDefault() ?? "127.0.0.1";
    }

    public static List<string> GetLocalIpAddresses()
    {
        var result = new List<string>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ThenByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

            foreach (var ni in interfaces)
            {
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        var ipStr = addr.Address.ToString();
                        if (!result.Contains(ipStr))
                        {
                            result.Add(ipStr);
                        }
                    }
                }
            }
        }
        catch { }

        if (result.Count == 0)
        {
            result.Add("127.0.0.1");
        }

        return result;
    }

    public Task<bool> StartServerAsync(string? ipAddress = null, int port = 5050)
    {
        try
        {
            if (IsRunning)
            {
                StopServer();
            }

            if (!string.IsNullOrEmpty(ipAddress))
            {
                CurrentIpAddress = ipAddress;
            }
            if (_token is null)
            {
                // Listening on every interface with no way to tell an intended request from
                // any other is not something to do quietly, so it is refused outright.
                _logService.LogError("[LanDrop] Refusing to start: the portal has no access token configured.");
                return Task.FromResult(false);
            }

            CurrentPort = port;

            _cts = new CancellationTokenSource();
            _tcpListener = new TcpListener(IPAddress.Any, CurrentPort);
            _tcpListener.Start();

            _listenTask = Task.Run(() => AcceptConnectionsAsync(_tcpListener, _cts.Token));
            _logService.LogInfo($"[LanDrop] Local HTTP server started on {ServerUrl} (listening on all interfaces port {CurrentPort})");

            NotifyStateChanged();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logService.LogError($"[LanDrop] Failed to start TcpListener on port {port}", ex);
            StopServer();
            return Task.FromResult(false);
        }
    }

    public void StopServer()
    {
        try
        {
            _cts?.Cancel();
            _tcpListener?.Stop();
            _tcpListener = null;
            _logService.LogInfo("[LanDrop] Server stopped.");
        }
        catch (Exception ex)
        {
            _logService.LogError("[LanDrop] Error stopping server", ex);
        }
        finally
        {
            NotifyStateChanged();
        }
    }

    public void AddSharedFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

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
        _logService.LogInfo($"[LanDrop] Added file for sharing: {item.FileName} ({item.FormattedSize})");
    }

    public void RemoveSharedFile(string id)
    {
        _sharedFiles.TryRemove(id, out _);
    }

    public void ClearSharedFiles()
    {
        _sharedFiles.Clear();
    }

    public IReadOnlyList<LanDropFileItem> GetSharedFiles()
    {
        return _sharedFiles.Values.OrderByDescending(f => f.Timestamp).ToList();
    }

    public IReadOnlyList<LanDropFileItem> GetReceivedFiles()
    {
        lock (_receivedFiles)
        {
            return _receivedFiles.OrderByDescending(f => f.Timestamp).ToList();
        }
    }

    public void ClearReceivedFiles()
    {
        lock (_receivedFiles)
        {
            _receivedFiles.Clear();
        }
    }

    public IReadOnlyList<LanDropTextMessage> GetClipboardMessages()
    {
        lock (_clipboardMessages)
        {
            return _clipboardMessages.OrderByDescending(m => m.Timestamp).ToList();
        }
    }

    public void PostTextMessageFromPc(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var msg = new LanDropTextMessage
        {
            Content = content.Trim(),
            Timestamp = DateTime.Now,
            SenderDevice = Environment.MachineName,
            IsFromPc = true
        };

        lock (_clipboardMessages)
        {
            _clipboardMessages.Insert(0, msg);
            if (_clipboardMessages.Count > 50) _clipboardMessages.RemoveAt(_clipboardMessages.Count - 1);
        }

        OnTextReceived?.Invoke(this, msg);
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logService.LogError("[LanDrop] Socket accept error", ex);
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var networkStream = client.GetStream())
        {
            try
            {
                var headerBuffer = new byte[8192];
                int totalRead = 0;
                int headerEndIndex = -1;

                using var msHeader = new MemoryStream();

                while (headerEndIndex == -1)
                {
                    int bytesRead = await networkStream.ReadAsync(headerBuffer, 0, headerBuffer.Length, ct);
                    if (bytesRead == 0) return;

                    msHeader.Write(headerBuffer, 0, bytesRead);
                    totalRead += bytesRead;

                    byte[] currentData = msHeader.ToArray();
                    headerEndIndex = FindPattern(currentData, "\r\n\r\n"u8.ToArray());

                    if (totalRead > 65536) break;
                }

                if (headerEndIndex == -1) return;

                byte[] allData = msHeader.ToArray();
                string headerString = Encoding.UTF8.GetString(allData, 0, headerEndIndex);
                var lines = headerString.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                var reqLine = lines[0].Split(' ');
                if (reqLine.Length < 2) return;

                string method = reqLine[0].ToUpperInvariant();
                string rawUrl = reqLine[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        string key = lines[i].Substring(0, colon).Trim();
                        string val = lines[i].Substring(colon + 1).Trim();
                        headers[key] = val;
                    }
                }

                int bodyStartIndex = headerEndIndex + 4;
                int initialBodyBytesCount = allData.Length - bodyStartIndex;
                byte[] initialBodyBytes = new byte[initialBodyBytesCount];
                if (initialBodyBytesCount > 0)
                {
                    Array.Copy(allData, bodyStartIndex, initialBodyBytes, 0, initialBodyBytesCount);
                }

                string path = rawUrl;
                string queryString = "";
                int qIndex = rawUrl.IndexOf('?');
                if (qIndex != -1)
                {
                    path = rawUrl.Substring(0, qIndex);
                    queryString = rawUrl.Substring(qIndex + 1);
                }

                if (method == "OPTIONS")
                {
                    await SendResponseAsync(networkStream, 204, "No Content", "text/plain", Array.Empty<byte>());
                    return;
                }

                var query = ParseQueryString(queryString);
                if (!IsAuthorised(headers, query))
                {
                    // The same answer for a missing token and a wrong one, with nothing about
                    // what this service is, so scanning the network learns nothing from it.
                    await SendResponseAsync(networkStream, 401, "Unauthorized", "text/plain", "Unauthorized"u8.ToArray());
                    return;
                }

                if (method == "GET")
                {
                    if (path == "/" || path == "/index.html")
                    {
                        string html = GetEmbeddedWebPortalHtml();
                        byte[] body = Encoding.UTF8.GetBytes(html);
                        await SendResponseAsync(networkStream, 200, "OK", "text/html; charset=utf-8", body);
                    }
                    else if (path == "/api/status")
                    {
                        await HandleStatusRequestAsync(networkStream);
                    }
                    else if (path.StartsWith("/api/download"))
                    {
                        var queryDict = ParseQueryString(queryString);
                        string? fileId = queryDict.TryGetValue("id", out var fid) ? fid : null;
                        await HandleDownloadRequestAsync(networkStream, fileId, ct);
                    }
                    else if (path == "/api/music/state")
                    {
                        if (_music is null)
                        {
                            await SendResponseAsync(networkStream, 404, "Not Found", "text/plain", "No player"u8.ToArray());
                        }
                        else
                        {
                            await SendResponseAsync(networkStream, 200, "OK", "application/json",
                                Encoding.UTF8.GetBytes(_music.StateJson()));
                        }
                    }
                    else if (path == "/api/music/events")
                    {
                        await StreamMusicEventsAsync(networkStream, ct);
                    }
                    else if (path == "/qr")
                    {
                        string svg = QrCodeGenerator.GenerateSvg(PortalUrl, 6, 2, "#00F0FF", "#0F172A");
                        byte[] body = Encoding.UTF8.GetBytes(svg);
                        await SendResponseAsync(networkStream, 200, "OK", "image/svg+xml", body);
                    }
                    else
                    {
                        await SendResponseAsync(networkStream, 404, "Not Found", "text/plain", "Not Found"u8.ToArray());
                    }
                }
                else if (method == "POST")
                {
                    if (path == "/api/upload")
                    {
                        await HandleUploadAsync(networkStream, headers, initialBodyBytes, ct);
                    }
                    else if (path == "/api/text")
                    {
                        await HandleTextAsync(networkStream, headers, initialBodyBytes, ct);
                    }
                    else if (path == "/api/remote")
                    {
                        await HandleRemoteActionAsync(networkStream, headers, initialBodyBytes, ct);
                    }
                    else if (path == "/api/music/command")
                    {
                        await HandleMusicCommandAsync(networkStream, query, ct);
                    }
                    else
                    {
                        await SendResponseAsync(networkStream, 404, "Not Found", "text/plain", "Not Found"u8.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning($"[LanDrop] Client handler exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Whether a request carries the portal's access token, in the header its own scripts send
    /// or in the query string the QR link uses.
    /// </summary>
    private bool IsAuthorised(Dictionary<string, string> headers, Dictionary<string, string> query)
    {
        if (_token is null) return false;

        if (headers.TryGetValue(PortalAccessToken.HeaderName, out var fromHeader) && _token.IsValid(fromHeader))
            return true;

        return query.TryGetValue(PortalAccessToken.QueryName, out var fromQuery) && _token.IsValid(fromQuery);
    }

    private async Task HandleMusicCommandAsync(NetworkStream stream, Dictionary<string, string> query, CancellationToken ct)
    {
        if (_music is null)
        {
            await SendResponseAsync(stream, 404, "Not Found", "text/plain", "No player"u8.ToArray());
            return;
        }

        query.TryGetValue("action", out var action);
        query.TryGetValue("value", out var value);

        bool ok = await _music.CommandAsync(action ?? string.Empty, value, ct);
        if (!ok)
        {
            // An unknown action or an out-of-range value is the caller's mistake, and saying so
            // is more useful than a success the player quietly ignored.
            await SendResponseAsync(stream, 400, "Bad Request", "application/json",
                Encoding.UTF8.GetBytes("{\"success\":false}"));
            return;
        }

        await SendResponseAsync(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(_music.StateJson()));
    }

    /// <summary>
    /// Pushes the player's state to the phone as Server-Sent Events, so several devices stay
    /// in step with the desktop and with each other without any of them polling.
    /// </summary>
    private async Task StreamMusicEventsAsync(NetworkStream stream, CancellationToken ct)
    {
        if (_music is null)
        {
            await SendResponseAsync(stream, 404, "Not Found", "text/plain", "No player"u8.ToArray());
            return;
        }

        var header = new StringBuilder();
        header.Append("HTTP/1.1 200 OK" + Crlf);
        header.Append("Content-Type: text/event-stream; charset=utf-8" + Crlf);
        header.Append("Cache-Control: no-cache, no-store" + Crlf);
        header.Append("X-Content-Type-Options: nosniff" + Crlf);
        header.Append("Connection: keep-alive" + Crlf + Crlf);

        byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
        await stream.FlushAsync(ct);

        // A signal rather than a queue of states: the phone only ever wants the latest one, so
        // a burst of changes collapses into a single send. It starts released on purpose, so a
        // client that has just connected is sent the current state instead of waiting for a
        // change that may be minutes away.
        using var pending = new SemaphoreSlim(1, 1);
        void OnChanged(object? sender, EventArgs e)
        {
            try { if (pending.CurrentCount == 0) pending.Release(); } catch (SemaphoreFullException) { }
        }

        _music.Changed += OnChanged;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The wait doubles as a heartbeat: with nothing happening, a comment line every
                // 20 seconds keeps the connection open through anything that drops idle ones.
                bool changed = await pending.WaitAsync(TimeSpan.FromSeconds(20), ct);

                string payload = changed
                    ? "event: state" + Lf + "data: " + _music.StateJson() + Lf + Lf
                    : ": ping" + Lf + Lf;

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                await stream.WriteAsync(bytes, 0, bytes.Length, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The phone closed the tab or left the network; nothing to report.
        }
        finally
        {
            _music.Changed -= OnChanged;
        }
    }

    private async Task HandleStatusRequestAsync(NetworkStream stream)
    {
        var filesList = _sharedFiles.Values.Select(f => new
        {
            id = f.Id,
            name = f.FileName,
            size = f.SizeBytes,
            formattedSize = f.FormattedSize,
            time = f.Timestamp.ToString("HH:mm")
        }).ToList();

        string? latestText = null;
        lock (_clipboardMessages)
        {
            latestText = _clipboardMessages.FirstOrDefault()?.Content;
        }

        // Live battery telemetry
        int batteryPercent = 100;
        bool isCharging = true;
        try
        {
            if (GetSystemPowerStatus(out var sps))
            {
                if (sps.BatteryLifePercent != 255)
                {
                    batteryPercent = sps.BatteryLifePercent;
                }
                isCharging = sps.ACLineStatus == 1;
            }
        }
        catch { }

        var statusObj = new
        {
            hostName = Environment.MachineName,
            ip = CurrentIpAddress,
            port = CurrentPort,
            sharedFiles = filesList,
            clipboard = latestText ?? string.Empty,
            battery = new
            {
                percent = batteryPercent,
                charging = isCharging
            }
        };

        string json = JsonSerializer.Serialize(statusObj);
        byte[] body = Encoding.UTF8.GetBytes(json);
        await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", body);
    }

    private async Task HandleRemoteActionAsync(NetworkStream stream, Dictionary<string, string> headers, byte[] initialBody, CancellationToken ct)
    {
        headers.TryGetValue("Content-Length", out var clStr);
        long.TryParse(clStr, out long contentLength);

        using var bodyStream = new MemoryStream();
        bodyStream.Write(initialBody, 0, initialBody.Length);

        long bytesRemaining = contentLength > 0 ? (contentLength - initialBody.Length) : 0;
        var buf = new byte[1024];
        while (bytesRemaining > 0)
        {
            int toRead = (int)Math.Min(buf.Length, bytesRemaining);
            int read = await stream.ReadAsync(buf, 0, toRead, ct);
            if (read == 0) break;
            bodyStream.Write(buf, 0, read);
            bytesRemaining -= read;
        }

        string action = Encoding.UTF8.GetString(bodyStream.ToArray()).Trim().ToLowerInvariant();

        switch (action)
        {
            case "play-pause":
                SimulateKey(VK_MEDIA_PLAY_PAUSE);
                break;
            case "next":
                SimulateKey(VK_MEDIA_NEXT_TRACK);
                break;
            case "prev":
                SimulateKey(VK_MEDIA_PREV_TRACK);
                break;
            case "vol-up":
                SimulateKey(VK_VOLUME_UP);
                break;
            case "vol-down":
                SimulateKey(VK_VOLUME_DOWN);
                break;
            case "mute":
                SimulateKey(VK_VOLUME_MUTE);
                break;
            case "lock":
                LockWorkStation();
                break;
        }

        byte[] ok = Encoding.UTF8.GetBytes("{\"success\":true}");
        await SendResponseAsync(stream, 200, "OK", "application/json", ok);
    }

    private static void SimulateKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { }
    }

    private async Task HandleDownloadRequestAsync(NetworkStream stream, string? fileId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileId) || !_sharedFiles.TryGetValue(fileId, out var item) || !File.Exists(item.FilePath))
        {
            await SendResponseAsync(stream, 404, "Not Found", "text/plain", "File Not Found"u8.ToArray());
            return;
        }

        var fi = new FileInfo(item.FilePath);
        long length = fi.Length;
        string safeFileName = WebUtility.UrlEncode(item.FileName);

        var headerSb = new StringBuilder();
        headerSb.Append("HTTP/1.1 200 OK\r\n");
        // No Access-Control-Allow-Origin: the portal page is served from this same origin,
        // so it needs no grant, and granting one let any site the user visits reach in.
        headerSb.Append("X-Content-Type-Options: nosniff\r\n");
        headerSb.Append("Content-Type: application/octet-stream\r\n");
        headerSb.Append($"Content-Length: {length}\r\n");
        headerSb.Append($"Content-Disposition: attachment; filename=\"{safeFileName}\"\r\n");
        headerSb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(headerSb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);

        await using var fs = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[65536];
        int read;
        while ((read = await fs.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await stream.WriteAsync(buffer, 0, read, ct);
        }

        _logService.LogInfo($"[LanDrop] Completed download stream for: {item.FileName}");
    }

    private async Task HandleUploadAsync(NetworkStream stream, Dictionary<string, string> headers, byte[] initialBody, CancellationToken ct)
    {
        headers.TryGetValue("Content-Length", out var clStr);
        long.TryParse(clStr, out long contentLength);

        headers.TryGetValue("Content-Type", out var contentType);
        contentType ??= "";

        string userAgent = headers.TryGetValue("User-Agent", out var ua) ? ua : "Mobile Device";

        using var bodyStream = new MemoryStream();
        bodyStream.Write(initialBody, 0, initialBody.Length);

        long bytesRemaining = contentLength > 0 ? (contentLength - initialBody.Length) : 0;
        var buf = new byte[32768];

        while (bytesRemaining > 0)
        {
            int toRead = (int)Math.Min(buf.Length, bytesRemaining);
            int read = await stream.ReadAsync(buf, 0, toRead, ct);
            if (read == 0) break;
            bodyStream.Write(buf, 0, read);
            bytesRemaining -= read;
        }

        byte[] fullBody = bodyStream.ToArray();

        if (contentType.Contains("multipart/form-data"))
        {
            int bIdx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
            if (bIdx != -1)
            {
                string boundary = contentType.Substring(bIdx + 9).Trim('"', ' ');
                ParseMultipartDataAndSave(fullBody, boundary, userAgent);
            }
        }
        else
        {
            string filename = headers.TryGetValue("X-File-Name", out var xfn) ? WebUtility.UrlDecode(xfn) : $"drop_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            filename = Path.GetFileName(filename);
            string safePath = GetUniqueFilePath(DownloadDirectory, filename);
            await File.WriteAllBytesAsync(safePath, fullBody, ct);

            var item = RecordReceivedFile(safePath, userAgent);
            OnFileReceived?.Invoke(this, item);
        }

        byte[] ok = Encoding.UTF8.GetBytes("{\"success\":true}");
        await SendResponseAsync(stream, 200, "OK", "application/json", ok);
    }

    private void ParseMultipartDataAndSave(byte[] bodyBytes, string boundary, string userAgent)
    {
        byte[] boundaryBytes = Encoding.UTF8.GetBytes("--" + boundary);
        byte[] endBoundaryBytes = Encoding.UTF8.GetBytes("--" + boundary + "--");

        int pos = 0;
        while (pos < bodyBytes.Length)
        {
            int nextBoundary = FindPattern(bodyBytes, boundaryBytes, pos);
            if (nextBoundary == -1) break;

            int headerStart = nextBoundary + boundaryBytes.Length + 2;
            if (headerStart >= bodyBytes.Length) break;

            int headerEnd = FindPattern(bodyBytes, "\r\n\r\n"u8.ToArray(), headerStart);
            if (headerEnd == -1) break;

            string headerText = Encoding.UTF8.GetString(bodyBytes, headerStart, headerEnd - headerStart);
            int dataStart = headerEnd + 4;

            int dataEnd = FindPattern(bodyBytes, boundaryBytes, dataStart);
            if (dataEnd == -1) dataEnd = FindPattern(bodyBytes, endBoundaryBytes, dataStart);
            if (dataEnd == -1) break;

            string filename = "";
            int fnIdx = headerText.IndexOf("filename=\"", StringComparison.OrdinalIgnoreCase);
            if (fnIdx != -1)
            {
                int fnEnd = headerText.IndexOf("\"", fnIdx + 10, StringComparison.OrdinalIgnoreCase);
                if (fnEnd != -1)
                {
                    filename = headerText.Substring(fnIdx + 10, fnEnd - (fnIdx + 10));
                }
            }

            if (!string.IsNullOrEmpty(filename))
            {
                filename = Path.GetFileName(filename);
                string safePath = GetUniqueFilePath(DownloadDirectory, filename);
                int dataLen = dataEnd - dataStart - 2;

                if (dataLen > 0)
                {
                    using (var fs = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(bodyBytes, dataStart, dataLen);
                    }

                    var item = RecordReceivedFile(safePath, userAgent);
                    OnFileReceived?.Invoke(this, item);
                    _logService.LogInfo($"[LanDrop] Received uploaded file: {item.FileName} ({item.FormattedSize})");
                }
            }

            pos = dataEnd;
        }
    }

    private async Task HandleTextAsync(NetworkStream stream, Dictionary<string, string> headers, byte[] initialBody, CancellationToken ct)
    {
        headers.TryGetValue("Content-Length", out var clStr);
        long.TryParse(clStr, out long contentLength);
        string userAgent = headers.TryGetValue("User-Agent", out var ua) ? ua : "Mobile Device";

        using var bodyStream = new MemoryStream();
        bodyStream.Write(initialBody, 0, initialBody.Length);

        long bytesRemaining = contentLength > 0 ? (contentLength - initialBody.Length) : 0;
        var buf = new byte[4096];
        while (bytesRemaining > 0)
        {
            int toRead = (int)Math.Min(buf.Length, bytesRemaining);
            int read = await stream.ReadAsync(buf, 0, toRead, ct);
            if (read == 0) break;
            bodyStream.Write(buf, 0, read);
            bytesRemaining -= read;
        }

        string text = Encoding.UTF8.GetString(bodyStream.ToArray()).Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var msg = new LanDropTextMessage
            {
                Content = text,
                Timestamp = DateTime.Now,
                SenderDevice = userAgent.Contains("iPhone") ? "iPhone" :
                               userAgent.Contains("Android") ? "Android Phone" : "Mobile Client",
                IsFromPc = false
            };

            lock (_clipboardMessages)
            {
                _clipboardMessages.Insert(0, msg);
                if (_clipboardMessages.Count > 50) _clipboardMessages.RemoveAt(_clipboardMessages.Count - 1);
            }

            OnTextReceived?.Invoke(this, msg);
            _logService.LogInfo($"[LanDrop] Quick Text received: '{msg.Content}'");
        }

        byte[] ok = Encoding.UTF8.GetBytes("{\"success\":true}");
        await SendResponseAsync(stream, 200, "OK", "application/json", ok);
    }

    private static async Task SendResponseAsync(NetworkStream stream, int statusCode, string statusDescription, string contentType, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {statusDescription}\r\n");
        // No Access-Control-Allow-Origin: the portal page is served from this same origin,
        // so it needs no grant, and granting one let any site the user visits reach in.
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, 0, body.Length);
        }
    }

    private static int FindPattern(byte[] buffer, byte[] pattern, int start = 0)
    {
        for (int i = start; i <= buffer.Length - pattern.Length; i++)
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

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;

        var pairs = query.Split('&');
        foreach (var pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
            {
                string key = WebUtility.UrlDecode(pair.Substring(0, eq));
                string val = WebUtility.UrlDecode(pair.Substring(eq + 1));
                dict[key] = val;
            }
        }
        return dict;
    }

    private LanDropFileItem RecordReceivedFile(string filePath, string device)
    {
        var fi = new FileInfo(filePath);
        var item = new LanDropFileItem
        {
            FileName = fi.Name,
            FilePath = fi.FullName,
            SizeBytes = fi.Length,
            IsReceived = true,
            Timestamp = DateTime.Now,
            DeviceName = device.Contains("iPhone") ? "iPhone" :
                         device.Contains("Android") ? "Android Phone" :
                         device.Contains("iPad") ? "iPad" : "Mobile Client"
        };

        lock (_receivedFiles)
        {
            _receivedFiles.Insert(0, item);
            if (_receivedFiles.Count > 100) _receivedFiles.RemoveAt(_receivedFiles.Count - 1);
        }

        return item;
    }

    private static string GetUniqueFilePath(string folder, string filename)
    {
        string baseName = Path.GetFileNameWithoutExtension(filename);
        string ext = Path.GetExtension(filename);
        string fullPath = Path.Combine(folder, filename);
        int counter = 1;

        while (File.Exists(fullPath))
        {
            fullPath = Path.Combine(folder, $"{baseName} ({counter}){ext}");
            counter++;
        }

        return fullPath;
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke(this, new LanDropServerState
        {
            IsRunning = IsRunning,
            SelectedIp = CurrentIpAddress,
            Port = CurrentPort,
            PortalUrl = PortalUrl
        });
    }

    private static string GetEmbeddedWebPortalHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
    <title>AxePower Mobile Hub</title>
    <style>
        :root {
            --bg: #090D16;
            --surface: #111827;
            --surface-elevated: #1F2937;
            --card-border: #374151;
            --accent: #00F0FF;
            --accent-glow: rgba(0, 240, 255, 0.2);
            --text-primary: #F9FAFB;
            --text-secondary: #9CA3AF;
            --success: #10B981;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }
        body { background-color: var(--bg); color: var(--text-primary); padding: 16px; min-height: 100vh; display: flex; flex-direction: column; align-items: center; }
        .container { width: 100%; max-width: 540px; display: flex; flex-direction: column; gap: 18px; }
        .header { display: flex; align-items: center; justify-content: space-between; padding: 14px 18px; background: var(--surface); border: 1px solid var(--card-border); border-radius: 16px; box-shadow: 0 4px 20px rgba(0,0,0,0.4); }
        .logo-group { display: flex; align-items: center; gap: 12px; }
        .logo-badge { width: 36px; height: 36px; border-radius: 10px; background: linear-gradient(135deg, #00F0FF, #3B82F6); display: flex; align-items: center; justify-content: center; font-weight: bold; color: #090D16; font-size: 18px; }
        .title { font-size: 18px; font-weight: 700; }
        .status-pill { font-size: 12px; font-weight: 600; padding: 4px 10px; border-radius: 20px; background: rgba(16, 185, 129, 0.15); color: var(--success); border: 1px solid rgba(16, 185, 129, 0.3); display: flex; align-items: center; gap: 6px; }
        .status-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--success); }
        
        .tab-bar { display: flex; gap: 8px; background: var(--surface); padding: 6px; border-radius: 12px; border: 1px solid var(--card-border); }
        .tab-btn { flex: 1; padding: 10px; font-size: 13px; font-weight: 600; text-align: center; border-radius: 8px; background: transparent; border: none; color: var(--text-secondary); cursor: pointer; }
        .tab-btn.active { background: var(--accent); color: #090D16; }

        .card { background: var(--surface); border: 1px solid var(--card-border); border-radius: 16px; padding: 20px; box-shadow: 0 4px 20px rgba(0,0,0,0.3); }
        .card-title { font-size: 15px; font-weight: 600; margin-bottom: 14px; display: flex; align-items: center; gap: 8px; color: var(--text-primary); }
        .dropzone { border: 2px dashed var(--card-border); border-radius: 14px; padding: 28px 16px; text-align: center; background: rgba(31, 41, 55, 0.4); cursor: pointer; transition: all 0.2s ease; display: flex; flex-direction: column; align-items: center; gap: 10px; }
        .dropzone.dragover { border-color: var(--accent); background: var(--accent-glow); }
        .dropzone-icon { font-size: 32px; }
        .dropzone-text { font-size: 14px; color: var(--text-primary); font-weight: 500; }
        .dropzone-sub { font-size: 12px; color: var(--text-secondary); }
        .btn-upload { margin-top: 10px; background: var(--accent); color: #090D16; font-weight: 600; border: none; border-radius: 10px; padding: 10px 20px; font-size: 14px; cursor: pointer; }
        .progress-bar { width: 100%; height: 8px; background: #374151; border-radius: 4px; overflow: hidden; margin-top: 14px; display: none; }
        .progress-fill { height: 100%; background: var(--accent); width: 0%; transition: width 0.15s ease; }
        .file-list { display: flex; flex-direction: column; gap: 10px; margin-top: 8px; }
        .file-item { display: flex; align-items: center; justify-content: space-between; padding: 12px 14px; background: var(--surface-elevated); border: 1px solid var(--card-border); border-radius: 12px; }
        .file-info { display: flex; flex-direction: column; gap: 3px; max-width: 75%; }
        .file-name { font-size: 13px; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .file-meta { font-size: 11px; color: var(--text-secondary); }
        .btn-download { background: rgba(0, 240, 255, 0.12); color: var(--accent); border: 1px solid rgba(0, 240, 255, 0.3); border-radius: 8px; padding: 8px 14px; font-size: 12px; font-weight: 600; text-decoration: none; display: inline-flex; align-items: center; gap: 4px; }
        .empty-msg { font-size: 13px; color: var(--text-secondary); text-align: center; padding: 16px; font-style: italic; }
        .text-input-group { display: flex; gap: 10px; margin-top: 8px; }
        .text-input { flex: 1; background: var(--surface-elevated); border: 1px solid var(--card-border); border-radius: 10px; padding: 10px 14px; color: var(--text-primary); font-size: 13px; outline: none; }
        .btn-send { background: var(--accent); color: #090D16; border: none; border-radius: 10px; padding: 10px 16px; font-weight: 600; font-size: 13px; cursor: pointer; }
        .clipboard-box { margin-top: 10px; background: rgba(31, 41, 55, 0.6); border: 1px solid var(--card-border); border-radius: 10px; padding: 12px 14px; font-size: 13px; display: flex; align-items: center; justify-content: space-between; }
        .btn-copy { background: transparent; border: 1px solid var(--card-border); color: var(--accent); border-radius: 6px; padding: 4px 10px; font-size: 11px; cursor: pointer; }

        /* Remote Controls */
        .battery-card { display: flex; align-items: center; justify-content: space-between; padding: 16px; background: #0E1626; border: 1px solid var(--card-border); border-radius: 14px; margin-bottom: 14px; }
        .battery-val { font-size: 24px; font-weight: bold; color: var(--accent); }
        .remote-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; }
        .btn-remote { background: var(--surface-elevated); border: 1px solid var(--card-border); color: var(--text-primary); border-radius: 12px; padding: 16px 8px; font-size: 13px; font-weight: 600; cursor: pointer; display: flex; flex-direction: column; align-items: center; gap: 6px; }
        .btn-remote:active { background: var(--accent-glow); border-color: var(--accent); }
        .toast { position: fixed; bottom: 20px; left: 50%; transform: translateX(-50%); background: #1F2937; border: 1px solid var(--accent); color: #fff; padding: 10px 20px; border-radius: 20px; font-size: 13px; box-shadow: 0 4px 20px rgba(0,0,0,0.5); opacity: 0; pointer-events: none; transition: opacity 0.2s; }
        .toast.show { opacity: 1; }
    </style>
</head>
<body>
    <div class=""container"">
        <header class=""header"">
            <div class=""logo-group"">
                <div class=""logo-badge"">⚡</div>
                <div>
                    <div class=""title"">AxePower Mobile Hub</div>
                    <div style=""font-size: 11px; color: var(--text-secondary);"" id=""hostSub"">AxePower PC</div>
                </div>
            </div>
            <div class=""status-pill"">
                <div class=""status-dot""></div>
                <span>Connected</span>
            </div>
        </header>

        <div class=""tab-bar"">
            <button class=""tab-btn active"" id=""tabDropBtn"" onclick=""switchTab('drop')"">📂 File Drop</button>
            <button class=""tab-btn"" id=""tabRemoteBtn"" onclick=""switchTab('remote')"">⚡ Remote PC</button>
        </div>

        <!-- Section 1: File Drop -->
        <div id=""secDrop"" style=""display: flex; flex-direction: column; gap: 16px;"">
            <section class=""card"">
                <div class=""card-title"">📤 Send Files to PC</div>
                <div class=""dropzone"" id=""dropzone"">
                    <div class=""dropzone-icon"">📁</div>
                    <div class=""dropzone-text"">Tap or Drop files here</div>
                    <div class=""dropzone-sub"">Instant transfer over local Wi-Fi</div>
                    <input type=""file"" id=""fileInput"" multiple style=""display:none;"">
                    <button class=""btn-upload"" onclick=""document.getElementById('fileInput').click()"">Choose Files</button>
                </div>
                <div class=""progress-bar"" id=""progressBar"">
                    <div class=""progress-fill"" id=""progressFill""></div>
                </div>
            </section>

            <section class=""card"">
                <div class=""card-title"">📥 Available on PC for Download</div>
                <div class=""file-list"" id=""sharedFileList"">
                    <div class=""empty-msg"">No files currently shared from PC</div>
                </div>
            </section>

            <section class=""card"">
                <div class=""card-title"">📋 Quick Text &amp; Links</div>
                <div class=""text-input-group"">
                    <input type=""text"" id=""quickTextInput"" class=""text-input"" placeholder=""Type a link, note or OTP to send to PC..."">
                    <button class=""btn-send"" onclick=""sendQuickText()"">Send</button>
                </div>
                <div id=""clipboardContainer"" style=""display:none;"">
                    <div style=""font-size: 11px; color: var(--text-secondary); margin-top: 14px;"">Latest from PC:</div>
                    <div class=""clipboard-box"">
                        <span id=""clipboardText"" style=""overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:80%;""></span>
                        <button class=""btn-copy"" onclick=""copyClipboard()"">Copy</button>
                    </div>
                </div>
            </section>
        </div>

        <!-- Section 2: Remote PC Controls -->
        <div id=""secRemote"" style=""display: none; flex-direction: column; gap: 16px;"">
            <section class=""card"">
                <div class=""card-title"">🔋 Live PC Telemetry</div>
                <div class=""battery-card"">
                    <div>
                        <div style=""font-size: 12px; color: var(--text-secondary);"">PC Battery State</div>
                        <div class=""battery-val"" id=""battPercent"">--%</div>
                    </div>
                    <div id=""battStatus"" style=""font-size: 13px; font-weight: 600; color: var(--success);"">AC POWER</div>
                </div>

                <div class=""card-title"" style=""margin-top: 10px;"">🎵 Media &amp; Audio Controls</div>
                <div class=""remote-grid"">
                    <button class=""btn-remote"" onclick=""sendAction('prev')""><span>⏮</span><span>Previous</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('play-pause')""><span>⏯</span><span>Play / Pause</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('next')""><span>⏭</span><span>Next</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('vol-down')""><span>🔉</span><span>Vol Down</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('mute')""><span>🔇</span><span>Mute</span></button>
                    <button class=""btn-remote"" onclick=""sendAction('vol-up')""><span>🔊</span><span>Vol Up</span></button>
                </div>

                <div class=""card-title"" style=""margin-top: 20px;"">🔒 Power &amp; Security</div>
                <button class=""btn-remote"" style=""width: 100%; padding: 14px; background: #2A171D; border-color: #EF4444; color: #EF4444;"" onclick=""sendAction('lock')"">
                    <span>🔒</span><span>Lock PC Workstation</span>
                </button>
            </section>
        </div>
    </div>

    <div class=""toast"" id=""toast""></div>

    <script>
        // The link AxePower handed out carries the access token. It is kept in memory and
        // attached to every request; it is never stored, so closing the tab ends the session.
        const AXE_TOKEN = new URLSearchParams(location.search).get('t') || '';
        const axeUrl = (path) => path + (path.includes('?') ? '&' : '?') + 't=' + encodeURIComponent(AXE_TOKEN);
        const axeFetch = (path, init) => fetch(axeUrl(path), init);

        function showToast(msg) {
            const toast = document.getElementById('toast');
            toast.textContent = msg;
            toast.classList.add('show');
            setTimeout(() => toast.classList.remove('show'), 3000);
        }

        function switchTab(t) {
            if (t === 'drop') {
                document.getElementById('secDrop').style.display = 'flex';
                document.getElementById('secRemote').style.display = 'none';
                document.getElementById('tabDropBtn').classList.add('active');
                document.getElementById('tabRemoteBtn').classList.remove('active');
            } else {
                document.getElementById('secDrop').style.display = 'none';
                document.getElementById('secRemote').style.display = 'flex';
                document.getElementById('tabDropBtn').classList.remove('active');
                document.getElementById('tabRemoteBtn').classList.add('active');
            }
        }

        const dropzone = document.getElementById('dropzone');
        const fileInput = document.getElementById('fileInput');
        const progressBar = document.getElementById('progressBar');
        const progressFill = document.getElementById('progressFill');

        ['dragenter', 'dragover'].forEach(name => {
            dropzone.addEventListener(name, (e) => { e.preventDefault(); dropzone.classList.add('dragover'); });
        });
        ['dragleave', 'drop'].forEach(name => {
            dropzone.addEventListener(name, (e) => { e.preventDefault(); dropzone.classList.remove('dragover'); });
        });

        dropzone.addEventListener('drop', (e) => {
            if (e.dataTransfer.files.length > 0) uploadFiles(e.dataTransfer.files);
        });

        fileInput.addEventListener('change', () => {
            if (fileInput.files.length > 0) uploadFiles(fileInput.files);
        });

        async function uploadFiles(files) {
            progressBar.style.display = 'block';
            for (let i = 0; i < files.length; i++) {
                const file = files[i];
                const formData = new FormData();
                formData.append('file', file);

                try {
                    progressFill.style.width = '20%';
                    const xhr = new XMLHttpRequest();
                    xhr.open('POST', axeUrl('/api/upload'), true);
                    xhr.upload.onprogress = (e) => {
                        if (e.lengthComputable) {
                            progressFill.style.width = Math.round((e.loaded / e.total) * 100) + '%';
                        }
                    };
                    xhr.onload = () => {
                        if (xhr.status === 200) showToast('✅ Sent ' + file.name + ' to PC!');
                        else showToast('❌ Failed to upload ' + file.name);
                    };
                    xhr.onerror = () => showToast('❌ Network error uploading ' + file.name);
                    xhr.send(formData);
                } catch (err) {
                    showToast('❌ Error: ' + err.message);
                }
            }
            setTimeout(() => {
                progressBar.style.display = 'none';
                progressFill.style.width = '0%';
            }, 1000);
        }

        async function sendQuickText() {
            const input = document.getElementById('quickTextInput');
            const val = input.value.trim();
            if (!val) return;

            try {
                const res = await axeFetch('/api/text', { method: 'POST', body: val });
                if (res.ok) {
                    showToast('🚀 Text sent to PC!');
                    input.value = '';
                }
            } catch (err) {
                showToast('❌ Error sending text');
            }
        }

        async function sendAction(act) {
            try {
                const res = await axeFetch('/api/remote', { method: 'POST', body: act });
                if (res.ok) showToast('⚡ Action sent: ' + act);
            } catch (e) {
                showToast('❌ Action error');
            }
        }

        let currentClipboard = '';
        function copyClipboard() {
            if (currentClipboard) {
                navigator.clipboard.writeText(currentClipboard).then(() => showToast('📋 Copied to clipboard!'));
            }
        }

        async function refreshStatus() {
            try {
                const res = await axeFetch('/api/status');
                if (!res.ok) return;
                const data = await res.json();

                if (data.hostName) document.getElementById('hostSub').textContent = data.hostName + ' (' + data.ip + ')';

                if (data.battery) {
                    document.getElementById('battPercent').textContent = data.battery.percent + '%';
                    document.getElementById('battStatus').textContent = data.battery.charging ? '⚡ CHARGING / AC' : '🔋 ON BATTERY';
                    document.getElementById('battStatus').style.color = data.battery.charging ? 'var(--accent)' : 'var(--success)';
                }

                const listContainer = document.getElementById('sharedFileList');
                if (data.sharedFiles && data.sharedFiles.length > 0) {
                    listContainer.innerHTML = '';
                    data.sharedFiles.forEach(f => {
                        const item = document.createElement('div');
                        item.className = 'file-item';
                        item.innerHTML = `
                            <div class=""file-info"">
                                <div class=""file-name"" title=""${f.name}"">${f.name}</div>
                                <div class=""file-meta"">${f.formattedSize} • ${f.time}</div>
                            </div>
                            <a href=""${axeUrl(`/api/download?id=${f.id}`)}"" class=""btn-download"">⬇ Download</a>
                        `;
                        listContainer.appendChild(item);
                    });
                } else {
                    listContainer.innerHTML = '<div class=""empty-msg"">No files currently shared from PC</div>';
                }

                if (data.clipboard) {
                    currentClipboard = data.clipboard;
                    document.getElementById('clipboardText').textContent = data.clipboard;
                    document.getElementById('clipboardContainer').style.display = 'block';
                }
            } catch (e) {}
        }

        refreshStatus();
        setInterval(refreshStatus, 3000);
    </script>
</body>
</html>";
    }

    public void Dispose()
    {
        StopServer();
        _cts?.Dispose();
    }
}
