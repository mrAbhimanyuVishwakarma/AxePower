using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// One connection, start to finish: the lockout check, the bounded request read, size rules,
/// authentication, then exactly one route. Every response closes the connection.
/// </summary>
internal sealed class LanDropRequestHandler
{
    private readonly LanDropSecurityLimits _limits;
    private readonly LanDropAuthenticator _auth;
    private readonly LanDropClientRegistry _clients;
    private readonly LanDropEndpoints _endpoints;
    private readonly LanDropMusicEndpoints _music;
    private readonly Action _clientAdded;
    private readonly LogService _log;

    public LanDropRequestHandler(LanDropSecurityLimits limits, LanDropAuthenticator auth, LanDropClientRegistry clients,
        LanDropEndpoints endpoints, LanDropMusicEndpoints music, Action clientAdded, LogService log)
    {
        _limits = limits;
        _auth = auth;
        _clients = clients;
        _endpoints = endpoints;
        _music = music;
        _clientAdded = clientAdded;
        _log = log;
    }

    public async Task HandleAsync(Stream stream, IPAddress clientIp, CancellationToken ct)
    {
        // Early rejection if client IP is locked out due to repeated authentication failures
        if (_auth.IsLockedOut(clientIp, out var retryAfter))
        {
            int retrySec = Math.Max(1, (int)retryAfter.TotalSeconds);
            byte[] retryBody = Encoding.UTF8.GetBytes($"{{\"error\":\"Too Many Requests. Locked out due to failed authentication.\",\"retryAfterSeconds\":{retrySec}}}");
            await LanDropHttp.SendAsync(stream, 429, "Too Many Requests", "application/json", retryBody, retrySec);
            await Task.Delay(50, ct);
            return;
        }

        try
        {
            var (outcome, request) = await LanDropHttp.ReadRequestAsync(stream, _limits, ct);
            switch (outcome)
            {
                case LanDropHttp.ReadOutcome.Closed:
                    return;
                case LanDropHttp.ReadOutcome.HeadTooLarge:
                    await LanDropHttp.SendTextAsync(stream, 431, "Request Header Fields Too Large", "Request Header Fields Too Large");
                    return;
                case LanDropHttp.ReadOutcome.ConflictingContentLength:
                    await LanDropHttp.SendTextAsync(stream, 400, "Bad Request", "Conflicting Content-Length headers");
                    return;
                case LanDropHttp.ReadOutcome.MalformedContentLength:
                    await LanDropHttp.SendTextAsync(stream, 400, "Bad Request", "Malformed or negative Content-Length header");
                    return;
            }

            if (request!.Method == "POST" && await RejectBodyAsync(stream, request)) return;

            if (request.Method == "OPTIONS")
            {
                await LanDropHttp.SendAsync(stream, 204, "No Content", "text/plain", Array.Empty<byte>());
                return;
            }

            if (_auth.IsBarred(clientIp))
            {
                // Refused even with valid credentials: the user removed this device.
                await LanDropHttp.SendTextAsync(stream, 403, "Forbidden", "This device was disconnected. Scan the new QR code after the PC rotates access.");
                return;
            }

            if (!_auth.Authorise(request, clientIp))
            {
                // The same answer for a missing token and a wrong one, with nothing about
                // what this service is, so scanning the network learns nothing from it.
                await LanDropHttp.SendTextAsync(stream, 401, "Unauthorized", "Unauthorized");
                await Task.Delay(50, ct);
                return;
            }

            var session = _clients.Track(clientIp.ToString(), request.UserAgent, out var added);
            if (added) _clientAdded();
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.SessionCts.Token);
            await RouteAsync(stream, request, sessionCts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[LanDrop] Client handler exception: {ex.Message}");
        }
    }

    /// <summary>The POST size rules, before any body is read. True when a refusal was sent.</summary>
    private async Task<bool> RejectBodyAsync(Stream stream, LanDropRequest request)
    {
        if (!request.HasContentLength)
        {
            await LanDropHttp.SendTextAsync(stream, 411, "Length Required", "Length Required");
            return true;
        }

        long length = request.ContentLength;
        bool tooLarge = length > _limits.MaxTotalRequestSizeBytes
            || (request.Path == "/api/text" && length > _limits.MaxTextMessageSizeBytes)
            || (request.Path is "/api/remote" or "/api/music/command" && length > 1024);
        if (!tooLarge) return false;

        await LanDropHttp.SendTextAsync(stream, 413, "Payload Too Large", "Payload Too Large");
        return true;
    }

    private Task RouteAsync(Stream stream, LanDropRequest request, CancellationToken ct)
    {
        var path = request.Path;
        return request.Method switch
        {
            "GET" when path is "/" or "/index.html" => _endpoints.PortalPageAsync(stream),
            "GET" when path == "/api/status" => _endpoints.StatusAsync(stream),
            "GET" when path.StartsWith("/api/download") => _endpoints.DownloadAsync(stream, request, ct),
            "GET" when path == "/api/music/state" => _music.StateAsync(stream),
            "GET" when path == "/api/music/events" => _music.EventsAsync(stream, ct),
            "GET" when path == "/qr" => _endpoints.QrAsync(stream),
            "GET" => NotFoundAsync(stream),
            "POST" when path == "/api/upload" => _endpoints.UploadAsync(stream, request, ct),
            "POST" when path == "/api/text" => _endpoints.TextAsync(stream, request, ct),
            "POST" when path == "/api/remote" => _endpoints.RemoteAsync(stream, request, ct),
            "POST" when path == "/api/music/command" => _music.CommandAsync(stream, request, ct),
            "POST" => NotFoundAsync(stream),
            // Any other method gets no response, as it always has.
            _ => Task.CompletedTask
        };
    }

    private static Task NotFoundAsync(Stream stream) => LanDropHttp.SendTextAsync(stream, 404, "Not Found", "Not Found");
}
