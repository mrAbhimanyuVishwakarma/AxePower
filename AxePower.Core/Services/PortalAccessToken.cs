using System;
using System.Security.Cryptography;
using System.Text;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services;

/// <summary>
/// Session-scoped, high-entropy access credential proving an HTTP request originated from
/// the link or QR code issued by AxePower for the current server session.
///
/// Guarded against replay across sessions: each server session receives a fresh 256-bit token
/// that is never persisted to disk, expires on timeout, and is invalidated on server stop,
/// restart, network profile switch, or explicit user disconnection.
/// </summary>
public sealed class PortalAccessToken
{
    /// <summary>The query-string parameter name the portal link uses.</summary>
    public const string QueryName = "t";

    /// <summary>The HTTP header the portal's own scripts send, keeping the token out of referers and URLs.</summary>
    public const string HeaderName = "X-AxePower-Token";

    private readonly object _gate = new();
    private string? _currentToken;
    private DateTime _createdAt;
    private readonly TimeSpan _sessionLifetime;

    public PortalAccessToken(ISecretStore? secrets = null, TimeSpan? sessionLifetime = null)
    {
        // secrets parameter preserved for backward-compatible DI/tests, but credentials are now
        // strictly ephemeral per session to prevent cross-session token reuse.
        _sessionLifetime = sessionLifetime ?? TimeSpan.FromHours(4);
    }

    /// <summary>
    /// The active session token, generating a fresh high-entropy 256-bit value if none exists.
    /// </summary>
    public string Value
    {
        get
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_currentToken) && !IsExpiredInternal())
                {
                    return _currentToken!;
                }

                _currentToken = GenerateHighEntropyToken();
                _createdAt = DateTime.UtcNow;
                return _currentToken;
            }
        }
    }

    /// <summary>
    /// Whether the current token has exceeded its maximum session lifetime.
    /// </summary>
    public bool IsExpired
    {
        get
        {
            lock (_gate)
            {
                return IsExpiredInternal();
            }
        }
    }

    private bool IsExpiredInternal() =>
        string.IsNullOrEmpty(_currentToken) || (DateTime.UtcNow - _createdAt > _sessionLifetime);

    /// <summary>
    /// Starts a fresh server session with a newly generated high-entropy token.
    /// Immediately cuts off access from any previous session URLs.
    /// </summary>
    public string StartNewSession() => Rotate();

    /// <summary>
    /// Rotates the session token immediately, invalidating all existing links and client sessions.
    /// </summary>
    public string Rotate()
    {
        lock (_gate)
        {
            _currentToken = GenerateHighEntropyToken();
            _createdAt = DateTime.UtcNow;
            return _currentToken;
        }
    }

    /// <summary>
    /// Invalidates the active token, rendering any incoming requests unauthorized.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _currentToken = null;
        }
    }

    /// <summary>
    /// Constant-time token verification using framework cryptographic APIs (CryptographicOperations.FixedTimeEquals).
    /// Compares SHA-256 digests of the supplied and expected tokens to guarantee constant comparison
    /// duration regardless of token length or character match positions.
    /// </summary>
    public bool IsValid(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;

        string? expected;
        lock (_gate)
        {
            if (IsExpiredInternal()) return false;
            expected = _currentToken;
        }

        if (string.IsNullOrEmpty(expected)) return false;

        // Hash both strings to 32-byte fixed-length digests before constant-time comparison
        byte[] suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        byte[] expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));

        return CryptographicOperations.FixedTimeEquals(suppliedDigest, expectedDigest);
    }

    /// <summary>
    /// Generates a cryptographically strong 256-bit URL-safe token.
    /// </summary>
    private static string GenerateHighEntropyToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
