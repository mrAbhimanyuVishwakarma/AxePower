using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.LanDrop;

/// <summary>One parsed request: what the router needs, plus the body bytes read with the head.</summary>
internal sealed class LanDropRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string QueryString { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required bool HasContentLength { get; init; }
    public required long ContentLength { get; init; }

    /// <summary>Body bytes that arrived in the same reads as the head.</summary>
    public required byte[] InitialBody { get; init; }

    private Dictionary<string, string>? _query;
    public Dictionary<string, string> Query => _query ??= LanDropHttp.ParseQueryString(QueryString);

    public string UserAgent => Headers.TryGetValue("User-Agent", out var ua) ? ua : string.Empty;
}

internal sealed class LanDropHeaderParseResult
{
    public bool IsValid { get; init; }
    public string Method { get; init; } = string.Empty;
    public string RawUrl { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string QueryString { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasContentLength { get; init; }
    public long ContentLength { get; init; } = -1;
    public bool ConflictingContentLength { get; init; }
    public bool MalformedContentLength { get; init; }
}

/// <summary>
/// HTTP/1.1 for the portal: reading a bounded request head, parsing it, and writing responses.
/// Every response closes the connection; there is no keep-alive.
/// </summary>
internal static class LanDropHttp
{
    internal const int MaxQueryParameters = 200;

    public enum ReadOutcome { Ok, Closed, HeadTooLarge, ConflictingContentLength, MalformedContentLength }

    /// <summary>
    /// Reads until the blank line that ends the head, never more than
    /// <see cref="LanDropSecurityLimits.MaxHeaderSizeBytes"/>, each read bounded by the read timeout.
    /// </summary>
    public static async Task<(ReadOutcome Outcome, LanDropRequest? Request)> ReadRequestAsync(
        Stream stream, LanDropSecurityLimits limits, CancellationToken ct)
    {
        var headerBuffer = new byte[4096];
        int totalRead = 0;
        int headerEndIndex = -1;
        using var msHeader = new MemoryStream();

        while (headerEndIndex == -1)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(limits.ReadTimeout);

            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(headerBuffer, 0, headerBuffer.Length, readCts.Token);
            }
            catch (OperationCanceledException)
            {
                return (ReadOutcome.Closed, null);
            }

            if (bytesRead == 0) return (ReadOutcome.Closed, null);

            msHeader.Write(headerBuffer, 0, bytesRead);
            totalRead += bytesRead;

            if (totalRead > limits.MaxHeaderSizeBytes) return (ReadOutcome.HeadTooLarge, null);

            headerEndIndex = FindPattern(msHeader.ToArray(), "\r\n\r\n"u8.ToArray());
        }

        byte[] allData = msHeader.ToArray();
        var parsed = ParseRequestHeaders(Encoding.UTF8.GetString(allData, 0, headerEndIndex));
        if (!parsed.IsValid) return (ReadOutcome.Closed, null);
        if (parsed.ConflictingContentLength) return (ReadOutcome.ConflictingContentLength, null);
        if (parsed.MalformedContentLength) return (ReadOutcome.MalformedContentLength, null);

        int bodyStart = headerEndIndex + 4;
        var initialBody = new byte[allData.Length - bodyStart];
        Array.Copy(allData, bodyStart, initialBody, 0, initialBody.Length);

        return (ReadOutcome.Ok, new LanDropRequest
        {
            Method = parsed.Method,
            Path = parsed.Path,
            QueryString = parsed.QueryString,
            Headers = parsed.Headers,
            HasContentLength = parsed.HasContentLength,
            ContentLength = parsed.ContentLength,
            InitialBody = initialBody
        });
    }

    /// <summary>
    /// Reads a small body (text, remote action) into memory: the bytes that came with the head,
    /// then up to <paramref name="contentLength"/> more, each read bounded by the read timeout.
    /// </summary>
    public static async Task<byte[]> ReadSmallBodyAsync(
        Stream stream, byte[] initialBody, long contentLength, int bufferSize, TimeSpan readTimeout, CancellationToken ct)
    {
        using var bodyStream = new MemoryStream();
        bodyStream.Write(initialBody, 0, initialBody.Length);

        long bytesRemaining = contentLength > 0 ? (contentLength - initialBody.Length) : 0;
        var buf = new byte[bufferSize];
        while (bytesRemaining > 0)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(readTimeout);

            int toRead = (int)Math.Min(buf.Length, bytesRemaining);
            int read = await stream.ReadAsync(buf, 0, toRead, readCts.Token);
            if (read == 0) break;
            bodyStream.Write(buf, 0, read);
            bytesRemaining -= read;
        }
        return bodyStream.ToArray();
    }

    public static async Task SendAsync(
        Stream stream,
        int statusCode,
        string statusDescription,
        string contentType,
        byte[] body,
        int? retryAfterSeconds = null,
        string? extraHeaders = null)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {statusDescription}\r\n");
        // No Access-Control-Allow-Origin: the portal page is served from this same origin,
        // so it needs no grant, and granting one let any site the user visits reach in.
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
        {
            sb.Append(extraHeaders).Append("\r\n");
        }
        if (retryAfterSeconds.HasValue)
        {
            sb.Append($"Retry-After: {retryAfterSeconds.Value}\r\n");
        }
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, 0, body.Length);
        }
        await stream.FlushAsync();
    }

    public static Task SendTextAsync(Stream stream, int status, string description, string text) =>
        SendAsync(stream, status, description, "text/plain", Encoding.UTF8.GetBytes(text));

    public static Task SendJsonAsync(Stream stream, int status, string description, string json, string contentType = "application/json") =>
        SendAsync(stream, status, description, contentType, Encoding.UTF8.GetBytes(json));

    /// <summary>The head of a file download; the caller streams <paramref name="length"/> bytes after it.</summary>
    public static async Task WriteAttachmentHeadAsync(Stream stream, string fileName, long length, CancellationToken ct)
    {
        var headerSb = new StringBuilder();
        headerSb.Append("HTTP/1.1 200 OK\r\n");
        headerSb.Append("X-Content-Type-Options: nosniff\r\n");
        headerSb.Append("Content-Type: application/octet-stream\r\n");
        headerSb.Append($"Content-Length: {length}\r\n");
        headerSb.Append($"Content-Disposition: attachment; filename=\"{WebUtility.UrlEncode(fileName)}\"\r\n");
        headerSb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(headerSb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
    }

    /// <summary>The head of a Server-Sent Events stream, which stays open.</summary>
    public static async Task WriteEventStreamHeadAsync(Stream stream, CancellationToken ct)
    {
        const string head =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream; charset=utf-8\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: keep-alive\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
        await stream.FlushAsync(ct);
    }

    internal static int FindPattern(byte[] buffer, byte[] pattern, int start = 0)
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

    internal static Dictionary<string, string> ParseQueryString(string query, int maxParameters = MaxQueryParameters)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;

        var pairs = query.Split('&');
        foreach (var pair in pairs)
        {
            if (dict.Count >= maxParameters) break;
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

    internal static bool TryGetCookie(Dictionary<string, string> headers, string name, out string value)
    {
        value = string.Empty;
        if (!headers.TryGetValue("Cookie", out var cookieHeader)) return false;
        foreach (var part in cookieHeader.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (part.AsSpan(0, eq).Trim().SequenceEqual(name))
            {
                value = part[(eq + 1)..].Trim();
                return value.Length > 0;
            }
        }
        return false;
    }

    internal static LanDropHeaderParseResult ParseRequestHeaders(string headerString)
    {
        var lines = headerString.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new LanDropHeaderParseResult { IsValid = false };

        var reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return new LanDropHeaderParseResult { IsValid = false };

        string method = reqLine[0].ToUpperInvariant();
        string rawUrl = reqLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool hasContentLength = false;
        long contentLength = -1;
        bool conflictingContentLength = false;
        bool malformedContentLength = false;

        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                string key = lines[i].Substring(0, colon).Trim();
                string val = lines[i].Substring(colon + 1).Trim();

                if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasContentLength)
                    {
                        conflictingContentLength = true;
                    }
                    else
                    {
                        hasContentLength = true;
                        if (val.Contains(','))
                        {
                            conflictingContentLength = true;
                        }
                        else if (!long.TryParse(val, out var parsed) || parsed < 0)
                        {
                            malformedContentLength = true;
                        }
                        else
                        {
                            contentLength = parsed;
                        }
                    }
                }

                headers[key] = val;
            }
        }

        string path = rawUrl;
        string queryString = "";
        int qIndex = rawUrl.IndexOf('?');
        if (qIndex != -1)
        {
            path = rawUrl.Substring(0, qIndex);
            queryString = rawUrl.Substring(qIndex + 1);
        }

        return new LanDropHeaderParseResult
        {
            IsValid = true,
            Method = method,
            RawUrl = rawUrl,
            Path = path,
            QueryString = queryString,
            Headers = headers,
            HasContentLength = hasContentLength,
            ContentLength = contentLength,
            ConflictingContentLength = conflictingContentLength,
            MalformedContentLength = malformedContentLength
        };
    }
}
