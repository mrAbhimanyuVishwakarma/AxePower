using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.Music;

/// <summary>The user cancelled a sign-in, or Spotify refused it.</summary>
public sealed class SpotifyAuthException : Exception
{
    public SpotifyAuthException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Signs into Spotify with the Authorization Code + PKCE flow, which is the only flow a desktop
/// app may use: there is no client secret to leak, because there is none. The browser sends the
/// code back to a loopback listener that is only open while a sign-in is in progress.
///
/// The user supplies their own Client ID (Settings → Music → Sources → Spotify) and registers
/// <see cref="RedirectUri"/> in their Spotify dashboard, so no AxePower-owned key is shipped.
/// </summary>
public sealed class SpotifyAuth
{
    /// <summary>Fixed, because Spotify matches redirect URIs exactly and will not accept a range.</summary>
    public const int CallbackPort = 8898;

    public const string RedirectUri = "http://127.0.0.1:8898/callback";

    private const string Scopes = "playlist-read-private playlist-read-collaborative user-library-read";
    private const string RefreshTokenKey = "spotify.refresh_token";

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);

    private readonly ISecretStore _secrets;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _accessToken;
    private DateTime _accessTokenExpiresUtc;

    public SpotifyAuth(ISecretStore secrets, SettingsService settings, LogService log, HttpClient? http = null)
    {
        _secrets = secrets;
        _settings = settings;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public event EventHandler? Changed;

    public string? ClientId => _settings.GetSettings().Music.Integrations?.SpotifyClientId?.Trim();

    public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

    public bool IsSignedIn => _secrets.Get(RefreshTokenKey) is not null;

    public void SignOut()
    {
        _secrets.Set(RefreshTokenKey, null);
        _accessToken = null;
        _accessTokenExpiresUtc = DateTime.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the browser, waits for Spotify to redirect back, and stores the refresh token.
    /// Returns false when the user closed the page or denied access.
    /// </summary>
    public async Task<bool> SignInAsync(CancellationToken ct)
    {
        string clientId = ClientId
            ?? throw new SpotifyAuthException("Add your Spotify Client ID first.");

        string verifier = CreateVerifier();
        string challenge = Challenge(verifier);
        string state = CreateVerifier()[..16];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{CallbackPort}/callback/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new SpotifyAuthException(
                $"Port {CallbackPort} could not be opened for the Spotify sign-in ({ex.Message}).", ex);
        }

        try
        {
            var query = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = challenge,
                ["state"] = state,
                ["scope"] = Scopes
            };

            OpenBrowser("https://accounts.spotify.com/authorize?" + Encode(query));

            var context = await WaitForCallbackAsync(listener, timeout.Token);
            string? code = context.Request.QueryString["code"];
            string? returnedState = context.Request.QueryString["state"];
            string? error = context.Request.QueryString["error"];

            // A mismatched state means the response did not come from the request we made,
            // so it is rejected without ever being exchanged for a token.
            bool ok = error is null && code is not null && returnedState == state;
            await RespondAsync(context, ok, error);

            if (error is not null)
            {
                _log.LogWarning($"[Music] Spotify sign-in was refused: {error}");
                return false;
            }

            if (code is null) return false;
            if (returnedState != state) throw new SpotifyAuthException("The Spotify sign-in response did not match the request.");

            await ExchangeAsync(clientId, code, verifier, timeout.Token);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpotifyAuthException("The Spotify sign-in timed out.");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    /// <summary>A valid access token, refreshing it when it is about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            // A minute of headroom, so a token cannot expire between this check and the request.
            if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresUtc - TimeSpan.FromMinutes(1))
                return _accessToken;

            string clientId = ClientId ?? throw new SpotifyAuthException("Add your Spotify Client ID first.");
            string refresh = _secrets.Get(RefreshTokenKey) ?? throw new SpotifyAuthException("Sign in to Spotify first.");

            var response = await PostTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = clientId
            }, ct);

            Store(response);
            return _accessToken!;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task ExchangeAsync(string clientId, string code, string verifier, CancellationToken ct)
    {
        var response = await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        }, ct);

        Store(response);
    }

    private async Task<JsonElement> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync("https://accounts.spotify.com/api/token", content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // A refresh token that Spotify has revoked can never work again, so it is dropped
            // rather than retried on every request.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                _secrets.Set(RefreshTokenKey, null);

            throw new SpotifyAuthException($"Spotify refused the sign-in ({(int)response.StatusCode}).");
        }

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private void Store(JsonElement token)
    {
        _accessToken = token.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (_accessToken is null) throw new SpotifyAuthException("Spotify did not return an access token.");

        int seconds = token.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
        _accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(seconds);

        // Spotify rotates refresh tokens: a response that carries a new one replaces the old,
        // and a response without one means the stored token is still current.
        if (token.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } value)
            _secrets.Set(RefreshTokenKey, value);
    }

    private static async Task<HttpListenerContext> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
    {
        var contextTask = listener.GetContextAsync();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (ct.Register(() => cancelled.TrySetResult(true)))
        {
            var finished = await Task.WhenAny(contextTask, cancelled.Task);
            if (finished != contextTask) throw new OperationCanceledException(ct);
        }

        return await contextTask;
    }

    private static async Task RespondAsync(HttpListenerContext context, bool ok, string? error)
    {
        string title = ok ? "AxePower is connected" : "Sign-in was not completed";
        string detail = ok
            ? "You can close this tab and go back to AxePower."
            : WebUtility.HtmlEncode(error ?? "The response from Spotify was not what AxePower asked for.");

        // Plain, self-contained HTML: the page is served once, from the loopback address, and
        // must render with no network of its own.
        string html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>AxePower</title></head>" +
            "<body style=\"font-family:Segoe UI,system-ui,sans-serif;background:#111;color:#eee;" +
            "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            $"<div style=\"text-align:center\"><h1 style=\"font-weight:600\">{title}</h1>" +
            $"<p style=\"color:#aaa\">{detail}</p></div></body></html>";

        byte[] bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = ok ? 200 : 400;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            throw new SpotifyAuthException($"The browser could not be opened: {ex.Message}", ex);
        }
    }

    private static string Encode(Dictionary<string, string> values)
    {
        var parts = new List<string>(values.Count);
        foreach (var pair in values)
            parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        return string.Join("&", parts);
    }

    /// <summary>A high-entropy PKCE verifier, base64url with no padding.</summary>
    private static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    private static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
