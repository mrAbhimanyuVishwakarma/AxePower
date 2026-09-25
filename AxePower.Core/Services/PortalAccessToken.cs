using System;
using System.Security.Cryptography;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services;

/// <summary>
/// The secret that proves a request to the LAN portal came from the link AxePower handed out.
///
/// The portal listens on every interface, so without this anything on the same network — and,
/// because a browser will happily post to a private address from a public page, any website
/// open on this PC — could upload files, read what is shared, drive playback or lock the
/// screen. The token travels in the QR link, so the phone never has to be told about it.
/// </summary>
public sealed class PortalAccessToken
{
    private const string SecretKey = "portal.access_token";

    /// <summary>The query-string name the portal link uses.</summary>
    public const string QueryName = "t";

    /// <summary>The header the portal's own scripts send, so the token stays out of the URL.</summary>
    public const string HeaderName = "X-AxePower-Token";

    private readonly ISecretStore _secrets;
    private readonly object _gate = new();
    private string? _cached;

    public PortalAccessToken(ISecretStore secrets)
    {
        _secrets = secrets;
    }

    /// <summary>The current token, creating one the first time it is needed.</summary>
    public string Value
    {
        get
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_cached)) return _cached!;

                string? stored = _secrets.Get(SecretKey);
                if (!string.IsNullOrWhiteSpace(stored) && stored.Length >= 22)
                {
                    _cached = stored;
                    return _cached;
                }

                _cached = Generate();
                _secrets.Set(SecretKey, _cached);
                return _cached;
            }
        }
    }

    /// <summary>Replaces the token, so every link and every phone handed one is cut off.</summary>
    public string Rotate()
    {
        lock (_gate)
        {
            _cached = Generate();
            _secrets.Set(SecretKey, _cached);
            return _cached;
        }
    }

    /// <summary>
    /// Whether a supplied token is the right one. The comparison takes the same time whatever
    /// the input, so an attacker cannot learn the token one character at a time.
    /// </summary>
    public bool IsValid(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;

        string expected = Value;
        if (supplied.Length != expected.Length) return false;

        int difference = 0;
        for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ supplied[i];
        return difference == 0;
    }

    /// <summary>A URL-safe 128-bit token.</summary>
    private static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
