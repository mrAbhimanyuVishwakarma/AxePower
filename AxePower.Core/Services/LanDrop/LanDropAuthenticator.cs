using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Who may use the portal: the session token (header, session cookie or QR-link query), the
/// per-address lockout after failed guesses, and the bar on devices the user disconnected.
/// </summary>
internal sealed class LanDropAuthenticator
{
    /// <summary>The session cookie the portal page authenticates with once the QR link has been opened.</summary>
    public const string SessionCookieName = "axe_session";

    private readonly PortalAccessToken? _token;

    /// <summary>
    /// Addresses the user disconnected one by one. They still hold this session's cookie (and may
    /// have the link in their history), so they stay barred until access rotates or the server
    /// restarts, at which point the old credentials are dead anyway.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _barred = new();

    public LanDropAuthenticator(PortalAccessToken? token, LanDropSecurityLimits limits)
    {
        _token = token;
        RateLimiter = new AuthenticationRateLimiter(limits.MaxFailedAuthAttempts, limits.FailedAuthWindow, limits.FailedAuthLockoutDuration);
    }

    public AuthenticationRateLimiter RateLimiter { get; }

    public bool IsLockedOut(IPAddress address, out TimeSpan retryAfter) => RateLimiter.IsLockedOut(address, out retryAfter);

    public bool IsBarred(IPAddress address) => _barred.ContainsKey(address.ToString());

    public void Bar(string address) => _barred[address] = 0;

    /// <summary>A new session: no bars and no recorded failures.</summary>
    public void Reset()
    {
        _barred.Clear();
        RateLimiter.Reset();
    }

    public void ClearBars() => _barred.Clear();

    /// <summary>Checks the request's credentials and records the outcome against its address.</summary>
    public bool Authorise(LanDropRequest request, IPAddress address)
    {
        if (!IsAuthorised(request.Headers, request.Query))
        {
            RateLimiter.RecordFailedAttempt(address);
            return false;
        }
        RateLimiter.RecordSuccessfulAuth(address);
        return true;
    }

    /// <summary>
    /// Whether a request carries the portal's access token, in the header its own scripts send,
    /// in the session cookie, or in the query string the QR link uses.
    /// </summary>
    public bool IsAuthorised(Dictionary<string, string> headers, Dictionary<string, string> query)
    {
        if (_token is null) return false;

        if (headers.TryGetValue(PortalAccessToken.HeaderName, out var fromHeader) && _token.IsValid(fromHeader))
            return true;

        if (LanDropHttp.TryGetCookie(headers, SessionCookieName, out var fromCookie) && _token.IsValid(fromCookie))
            return true;

        return query.TryGetValue(PortalAccessToken.QueryName, out var fromQuery) && _token.IsValid(fromQuery);
    }

    /// <summary>
    /// The cookie that replaces the token in the URL once the QR link has been opened. HttpOnly
    /// keeps it from page scripts, SameSite=Strict keeps other sites from sending it, and with
    /// no Expires it ends with the browser session. Rotating the token makes it worthless.
    /// </summary>
    public string SessionCookieHeader() =>
        $"Set-Cookie: {SessionCookieName}={_token!.Value}; Path=/; HttpOnly; SameSite=Strict";
}
