using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Helpers;
using AxePower.Core.Models;

namespace AxePower.Core.Services.LanDrop;

/// <summary>What the endpoints need from the running server, so they can be tested without one.</summary>
internal interface ILanDropHost
{
    string CurrentIpAddress { get; }
    int CurrentPort { get; }
    string PortalUrl { get; }
    void FileReceived(LanDropFileItem item);
    void TextReceived(LanDropTextMessage message);
}

/// <summary>
/// The portal's file, text and remote routes. Each reads what it needs from an authenticated
/// request and writes one response; the router decides which one runs.
/// </summary>
internal sealed class LanDropEndpoints
{
    private const string Success = "{\"success\":true}";

    private readonly ILanDropHost _host;
    private readonly LanDropStorage _storage;
    private readonly LanDropClipboard _clipboard;
    private readonly IRemoteMediaActions _remoteActions;
    private readonly LanDropAuthenticator _auth;
    private readonly LanDropSecurityLimits _limits;
    private readonly LogService _log;

    public LanDropEndpoints(ILanDropHost host, LanDropStorage storage, LanDropClipboard clipboard,
        IRemoteMediaActions remoteActions, LanDropAuthenticator auth, LanDropSecurityLimits limits, LogService log)
    {
        _host = host;
        _storage = storage;
        _clipboard = clipboard;
        _remoteActions = remoteActions;
        _auth = auth;
        _limits = limits;
        _log = log;
    }

    /// <summary>GET / — the portal page, with the session cookie that replaces the link's token.</summary>
    public Task PortalPageAsync(Stream stream) =>
        LanDropHttp.SendAsync(stream, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LanDropPortalPage.Html),
            extraHeaders: _auth.SessionCookieHeader());

    /// <summary>GET /qr — the portal link as an SVG QR code.</summary>
    public Task QrAsync(Stream stream) =>
        LanDropHttp.SendAsync(stream, 200, "OK", "image/svg+xml",
            Encoding.UTF8.GetBytes(QrCodeGenerator.GenerateSvg(_host.PortalUrl, 6, 4, "#0F172A", "#FFFFFF")));

    /// <summary>GET /api/status — host, shared files, latest clipboard text and battery.</summary>
    public Task StatusAsync(Stream stream)
    {
        var (batteryPercent, isCharging) = HostPowerStatus.Read();
        var status = new
        {
            hostName = Environment.MachineName,
            ip = _host.CurrentIpAddress,
            port = _host.CurrentPort,
            sharedFiles = _storage.GetShared().Select(f => new
            {
                id = f.Id,
                name = f.FileName,
                size = f.SizeBytes,
                formattedSize = f.FormattedSize,
                time = f.Timestamp.ToString("HH:mm")
            }).ToList(),
            clipboard = _clipboard.Latest ?? string.Empty,
            battery = new { percent = batteryPercent, charging = isCharging }
        };
        return LanDropHttp.SendJsonAsync(stream, 200, "OK", JsonSerializer.Serialize(status), "application/json; charset=utf-8");
    }

    /// <summary>GET /api/download?id= — streams one shared file.</summary>
    public async Task DownloadAsync(Stream stream, LanDropRequest request, CancellationToken ct)
    {
        string? fileId = request.Query.TryGetValue("id", out var fid) ? fid : null;
        if (_storage.FindDownload(fileId) is not { } item)
        {
            await LanDropHttp.SendTextAsync(stream, 404, "Not Found", "File Not Found");
            return;
        }

        await LanDropHttp.WriteAttachmentHeadAsync(stream, item.FileName, new FileInfo(item.FilePath).Length, ct);
        await LanDropStorage.CopyToAsync(item, stream, ct);
        _log.LogInfo($"[LanDrop] Completed download stream for: {item.FileName}");
    }

    /// <summary>POST /api/remote — a media key or lock, from a short text body.</summary>
    public async Task RemoteAsync(Stream stream, LanDropRequest request, CancellationToken ct)
    {
        if (request.ContentLength > 1024)
        {
            await LanDropHttp.SendTextAsync(stream, 413, "Payload Too Large", "Payload Too Large");
            return;
        }

        var body = await LanDropHttp.ReadSmallBodyAsync(stream, request.InitialBody, request.ContentLength, 1024, _limits.ReadTimeout, ct);
        // An unknown action is accepted and ignored, as the portal has always done.
        if (RemoteMediaActionParser.Parse(Encoding.UTF8.GetString(body)) is { } action)
        {
            _remoteActions.Perform(action);
        }
        await LanDropHttp.SendJsonAsync(stream, 200, "OK", Success);
    }

    /// <summary>POST /api/text — a quick-text message from the phone.</summary>
    public async Task TextAsync(Stream stream, LanDropRequest request, CancellationToken ct)
    {
        if (request.ContentLength > _limits.MaxTextMessageSizeBytes)
        {
            await LanDropHttp.SendTextAsync(stream, 413, "Payload Too Large", "Payload Too Large");
            return;
        }

        string userAgent = request.Headers.TryGetValue("User-Agent", out var ua) ? ua : "Mobile Device";
        var body = await LanDropHttp.ReadSmallBodyAsync(stream, request.InitialBody, request.ContentLength, 4096, _limits.ReadTimeout, ct);
        string text = Encoding.UTF8.GetString(body).Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var msg = new LanDropTextMessage
            {
                Content = text,
                Timestamp = DateTime.Now,
                SenderDevice = LanDropDeviceNames.ForTextMessage(userAgent),
                IsFromPc = false
            };
            _clipboard.Add(msg);
            _host.TextReceived(msg);
            // Personal content: only its size is logged.
            _log.LogInfo($"[LanDrop] Quick Text received ({msg.Content.Length} characters).");
        }
        await LanDropHttp.SendJsonAsync(stream, 200, "OK", Success);
    }

    /// <summary>POST /api/upload — a multipart form, or a raw body named by X-File-Name.</summary>
    public async Task UploadAsync(Stream stream, LanDropRequest request, CancellationToken ct)
    {
        string contentType = request.Headers.TryGetValue("Content-Type", out var type) ? type : "";
        string userAgent = request.Headers.TryGetValue("User-Agent", out var ua) ? ua : "Mobile Device";

        if (!_storage.HasRoomFor(request.ContentLength))
        {
            await LanDropHttp.SendJsonAsync(stream, 507, "Insufficient Storage", "{\"success\":false,\"error\":\"Insufficient disk space on server.\"}");
            return;
        }

        await using var body = new PrefixStream(request.InitialBody, stream);
        bool multipart = contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (multipart)
            {
                int bIdx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
                if (bIdx == -1)
                {
                    await LanDropHttp.SendJsonAsync(stream, 400, "Bad Request", "{\"success\":false,\"error\":\"Missing boundary in multipart request.\"}");
                    return;
                }

                string boundary = contentType.Substring(bIdx + 9).Trim('\"', ' ');
                foreach (var item in await _storage.ReceiveMultipartAsync(body, boundary, userAgent, ct))
                {
                    _host.FileReceived(item);
                    _log.LogInfo($"[LanDrop] Received uploaded file: {item.FileName} ({item.FormattedSize})");
                }
            }
            else
            {
                string rawFilename = request.Headers.TryGetValue("X-File-Name", out var xfn)
                    ? WebUtility.UrlDecode(xfn)
                    : $"drop_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin";

                if (LanDropPathSanitizer.IsPathTraversalAttempt(rawFilename))
                {
                    await SendUnsafeNameAsync(stream);
                    return;
                }

                var item = await _storage.ReceiveRawAsync(body, LanDropPathSanitizer.NormalizeFileName(rawFilename), request.ContentLength, userAgent, ct);
                _host.FileReceived(item);
                _log.LogInfo($"[LanDrop] Received uploaded binary: {item.FileName} ({item.FormattedSize})");
            }
        }
        catch (SecurityException sex)
        {
            _log.LogWarning(multipart
                ? $"[LanDrop] Security violation during upload: {sex.Message}"
                : $"[LanDrop] Traversal containment violation: {sex.Message}");
            await SendUnsafeNameAsync(stream);
            return;
        }
        catch (InvalidDataException idEx) when (multipart)
        {
            _log.LogWarning($"[LanDrop] Malformed multipart upload: {idEx.Message}");
            await LanDropHttp.SendJsonAsync(stream, 400, "Bad Request", $"{{\"success\":false,\"error\":\"{idEx.Message}\"}}");
            return;
        }
        catch (IOException ioEx)
        {
            _log.LogWarning(multipart ? $"[LanDrop] IO error during upload: {ioEx.Message}" : $"[LanDrop] Upload IO error: {ioEx.Message}");
            bool full = ioEx.Message.Contains("disk space", StringComparison.OrdinalIgnoreCase);
            await LanDropHttp.SendJsonAsync(stream, full ? 507 : 400, full ? "Insufficient Storage" : "Bad Request",
                $"{{\"success\":false,\"error\":\"{ioEx.Message}\"}}");
            return;
        }

        await LanDropHttp.SendJsonAsync(stream, 200, "OK", Success);
    }

    private static Task SendUnsafeNameAsync(Stream stream) =>
        LanDropHttp.SendJsonAsync(stream, 400, "Bad Request", "{\"success\":false,\"error\":\"Invalid or unsafe filename.\"}");
}
