using System;
using System.Text.RegularExpressions;

namespace AxePower.Core.Services;

/// <summary>
/// Strips secrets from text before it is logged. <see cref="LogService"/> runs every message and
/// exception through it, so a failure that echoes a URL, a header or a response body cannot leak
/// the LAN Drop token, a room link's viewer secret, an OAuth code, a session token or an email
/// address into AxePower.log. It errs on the side of removing: a long hash may show as [redacted].
/// GUIDs (MSI product codes, task ids) are kept because they are needed to diagnose uninstalls.
/// </summary>
public static class LogRedactor
{
    public const string Mask = "[redacted]";

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

    // A URL fragment carries room secrets (#v=...) and OAuth implicit-flow tokens (#access_token=...).
    private static readonly Regex UrlFragment = new(@"(?<url>\b(?:https?|wss?)://[^\s#""'<>]+)#[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    // Any URL's query string: tokens, codes, proofs and nonces travel there.
    private static readonly Regex UrlQuery = new(@"(?<url>\b(?:https?|wss?)://[^\s?#""'<>]+)\?[^\s#""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    // A query fragment without a scheme, e.g. a request line "GET /x?t=..." or "...&code=...".
    private static readonly Regex SecretParameter = new(@"(?<name>[?&](?:t|v|n|p|token|access_token|refresh_token|id_token|code|key|api_key|apikey|secret|password|sig|signature|auth|session)=)[^&\s#""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    private static readonly Regex AuthorizationValue = new(@"(?<scheme>\b(?:Bearer|Basic|Token)\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    private static readonly Regex KeyValueSecret = new(@"(?<name>\b(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|cookie|set-cookie|axe_session)""?\s*[:=]\s*""?)[^\s"",;&]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Budget);

    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant, Budget);

    private static readonly Regex Email = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, Budget);

    private static readonly Regex Guid = new(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$", RegexOptions.CultureInvariant, Budget);

    // Long opaque tokens: hex or base64/base64url runs of 24+ characters mixing letters and digits.
    private static readonly Regex OpaqueToken = new(@"(?<![A-Za-z0-9_/\\.+-])[A-Za-z0-9_+/-]{24,}={0,2}(?![A-Za-z0-9_/\\.+-])", RegexOptions.CultureInvariant, Budget);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            var s = UrlFragment.Replace(text, m => m.Groups["url"].Value + "#" + Mask);
            s = UrlQuery.Replace(s, m => m.Groups["url"].Value + "?" + Mask);
            s = SecretParameter.Replace(s, m => m.Groups["name"].Value + Mask);
            s = AuthorizationValue.Replace(s, m => m.Groups["scheme"].Value + Mask);
            s = KeyValueSecret.Replace(s, m => m.Groups["name"].Value + Mask);
            s = Jwt.Replace(s, Mask);
            s = Email.Replace(s, "[email]");
            s = OpaqueToken.Replace(s, m => LooksLikeSecret(m.Value) ? Mask : m.Value);
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological message: logging nothing of it is safer than logging it unredacted.
            return "[message withheld: too long to redact]";
        }
    }

    /// <summary>A short, redacted description of an exception: type and message, no stack.</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {Redact(ex.Message)}";

    private static bool LooksLikeSecret(string value)
    {
        if (Guid.IsMatch(value)) return false;
        bool letter = false, digit = false;
        foreach (var c in value)
        {
            if (char.IsDigit(c)) digit = true;
            else if (char.IsLetter(c)) letter = true;
        }
        // Words like "SystemTelemetryServiceFailure" have no digits; paths are split by separators.
        return letter && digit;
    }
}
