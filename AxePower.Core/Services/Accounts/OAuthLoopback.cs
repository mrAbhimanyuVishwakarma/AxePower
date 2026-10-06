using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.Accounts;

/// <summary>PKCE (RFC 7636): the verifier stays in AxePower; only its SHA-256 goes to the browser.</summary>
public static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Where a browser sign-in comes back to. Swappable so tests need no socket or browser.</summary>
public interface IOAuthLoopback : IDisposable
{
    Uri RedirectUri { get; }

    /// <summary>Waits for the browser to return, and gives the query string it brought.</summary>
    Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct);
}

/// <summary>
/// A one-shot HTTP listener on 127.0.0.1 that the browser returns to after signing in.
///
/// The port is fixed because Supabase only redirects to addresses on its allow-list, and this exact
/// URL is on it. It binds loopback only, so nothing on the network can reach it, and it closes as
/// soon as one callback has arrived. Raw sockets rather than HttpListener, which needs admin rights
/// or a URL reservation to listen (the same reason as ShareViewerHost).
/// </summary>
public sealed class OAuthLoopback : IOAuthLoopback
{
    public const int Port = 53682;
    public const string CallbackPath = "/auth/callback";

    private readonly TcpListener _listener;

    private OAuthLoopback(TcpListener listener) => _listener = listener;

    public Uri RedirectUri { get; } = new($"http://127.0.0.1:{Port}{CallbackPath}");

    public static OAuthLoopback Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new AccountException(AccountError.Unknown,
                $"Another app is using port {Port}, which sign-in needs. Close it and try again.", ex);
        }

        return new OAuthLoopback(listener);
    }

    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();

            var target = await ReadRequestTargetAsync(stream, ct);
            if (target is null || !target.StartsWith(CallbackPath, StringComparison.Ordinal))
            {
                // Browsers also ask for /favicon.ico and the like; answer and keep waiting.
                await WriteAsync(stream, "404 Not Found", "Not found.", ct);
                continue;
            }

            var query = ParseQuery(target);
            bool failed = query.ContainsKey("error") || query.ContainsKey("error_description");
            await WriteAsync(stream, "200 OK", Page(failed), ct);
            return query;
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); }
        catch (ObjectDisposedException) { /* Already stopped by an earlier Dispose. */ }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int used = 0;
        while (used < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(used), ct);
            if (read == 0) break;
            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            int end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var parts = text[..end].Split(' ');
            return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
        }
        return null;
    }

    internal static Dictionary<string, string> ParseQuery(string target, int maxParameters = 200)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int q = target.IndexOf('?');
        if (q < 0) return result;

        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= maxParameters) break;

            int eq = pair.IndexOf('=');
            string rawKey = eq < 0 ? pair : pair[..eq];
            string rawValue = eq < 0 ? string.Empty : pair[(eq + 1)..];

            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                key = WebUtility.UrlDecode(rawKey.Replace('+', ' '));
            }

            try
            {
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                value = WebUtility.UrlDecode(rawValue.Replace('+', ' '));
            }

            result[key] = value;
        }
        return result;
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(bytes, ct);
    }

    private static string Page(bool failed)
    {
        var (title, line) = failed
            ? ("Sign-in didn't finish", "Go back to AxePower to see why, then try again.")
            : ("You're signed in", "You can close this tab and return to AxePower.");

        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>AxePower</title>
            <style>
              body{margin:0;height:100vh;display:grid;place-items:center;background:#0b0d12;color:#e8eaf0;
                   font-family:Segoe UI,system-ui,sans-serif}
              .card{padding:40px 48px;border:1px solid #2a2e37;border-radius:18px;background:#14171e;text-align:center}
              h1{font-size:22px;margin:0 0 8px} p{margin:0;color:#9ba3af}
            </style></head>
            <body><div class="card"><h1>{{title}}</h1><p>{{line}}</p></div></body></html>
            """;
    }
}
