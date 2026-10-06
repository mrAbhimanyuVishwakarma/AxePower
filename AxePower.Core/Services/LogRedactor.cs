using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AxePower.Core.Services;

/// <summary>
/// Strips secrets and personal details from text before it is logged. <see cref="LogService"/> runs
/// every message and exception through it, so a failure that echoes a URL, a header or a response
/// body cannot leak the LAN Drop token, a room link's viewer secret, an OAuth code, a session token,
/// an API key or an email address. The user's name and the computer's name are replaced too, also
/// inside paths (C:\Users\name\Music becomes %USERPROFILE%\Music). It errs on the side of removing:
/// a long hash may show as [redacted]. GUIDs (MSI product codes, task ids) are kept because they are
/// needed to diagnose uninstalls.
/// </summary>
public static class LogRedactor
{
    public const string Mask = "[redacted]";
    public const string UserMask = "[user]";
    public const string ComputerMask = "[computer]";

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const RegexOptions IgnoreCase = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Regex PrivateKeyBlock = new(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|$)", Options, Budget);

    // user:password@ inside a URL (proxy settings, git remotes, database strings).
    private static readonly Regex UrlCredentials = new(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@:""'<>]+:[^\s/@""'<>]+@", IgnoreCase, Budget);

    // A URL fragment carries room secrets (#v=...) and OAuth implicit-flow tokens (#access_token=...).
    private static readonly Regex UrlFragment = new(@"(?<url>\b(?:https?|wss?)://[^\s#""'<>]+)#[^\s""'<>]+", IgnoreCase, Budget);

    // Any URL's query string: tokens, codes, proofs and nonces travel there.
    private static readonly Regex UrlQuery = new(@"(?<url>\b(?:https?|wss?)://[^\s?#""'<>]+)\?[^\s#""'<>]*", IgnoreCase, Budget);

    // A query fragment without a scheme, e.g. a request line "GET /x?t=..." or "...&code=...".
    private static readonly Regex SecretParameter = new(@"(?<name>[?&](?:t|v|n|p|token|access_token|refresh_token|id_token|code|key|api_key|apikey|secret|password|sig|signature|auth|session)=)[^&\s#""'<>]+", IgnoreCase, Budget);

    // A whole Cookie / Set-Cookie header, every pair on it.
    private static readonly Regex CookieHeader = new(@"(?<name>\b(?:set-)?cookie\s*:\s*)[^\r\n]+", IgnoreCase, Budget);

    private static readonly Regex AuthorizationValue = new(@"(?<scheme>\b(?:Bearer|Basic|Token|Digest)\s+)[A-Za-z0-9._~+/=-]+", IgnoreCase, Budget);

    private static readonly Regex KeyValueSecret = new(@"(?<name>\b(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|cookie|axe_session|private[_-]?key)""?\s*[:=]\s*""?)[^\s"",;&]+", IgnoreCase, Budget);

    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", Options, Budget);

    // Key formats with a recognisable prefix, redacted even when short or all letters.
    private static readonly Regex KnownKeyFormats = new(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}" +            // GitHub tokens
        @"|github_pat_[A-Za-z0-9_]{20,}" +              // GitHub fine-grained tokens
        @"|sb_(?:secret|publishable)_[A-Za-z0-9_-]{8,}" + // Supabase keys
        @"|sk-(?:ant-|proj-)?[A-Za-z0-9_-]{16,}" +      // OpenAI / Anthropic style keys
        @"|(?:sk|rk|pk)_(?:live|test)_[A-Za-z0-9]{10,}" + // Stripe keys
        @"|xox[abprs]-[A-Za-z0-9-]{10,}" +              // Slack tokens
        @"|AKIA[0-9A-Z]{16}" +                          // AWS access key IDs
        @"|AIza[0-9A-Za-z_-]{30,}" +                    // Google API keys
        @"|ya29\.[A-Za-z0-9_-]{10,}" +                  // Google OAuth access tokens
        @"|[MN][A-Za-z0-9]{23,27}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,})", // Discord bot tokens
        Options, Budget);

    private static readonly Regex Email = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", Options, Budget);

    // Another user's profile folder (C:\Users\someone\...), in either slash style.
    private static readonly Regex OtherUserProfile = new(@"(?<root>\b[A-Za-z]:[\\/]+Users[\\/]+)(?!Public\b|Default\b|All Users\b)[^\\/\s""'<>:*?|]+", IgnoreCase, Budget);

    private static readonly Regex Guid = new(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$", Options, Budget);

    // Long opaque tokens: hex or base64/base64url runs of 24+ characters mixing letters and digits.
    private static readonly Regex OpaqueToken = new(@"(?<![A-Za-z0-9_/\\.+-])[A-Za-z0-9_+/-]{24,}={0,2}(?![A-Za-z0-9_/\\.+-])", Options, Budget);

    // Short or common account names would mangle ordinary words, so they are only replaced inside paths.
    private static readonly HashSet<string> CommonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "user", "users", "owner", "guest", "test", "home", "default", "public", "system", "pc", "desktop", "laptop",
    };

    private static readonly Identity Machine = Identity.Current();

    public static string Redact(string? text) => Redact(text, Machine);

    /// <summary>A short, redacted description of an exception: type and message, no stack.</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {Redact(ex.Message)}";

    /// <summary>For tests: redacts as if running as <paramref name="userProfile"/> on <paramref name="computerName"/>.</summary>
    internal static string RedactFor(string? text, string userProfile, string userName, string computerName) =>
        Redact(text, new Identity(userProfile, userName, computerName));

    private static string Redact(string? text, Identity identity)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        try
        {
            var s = PrivateKeyBlock.Replace(text, Mask);
            s = UrlCredentials.Replace(s, m => m.Groups["scheme"].Value + Mask + "@");
            s = UrlFragment.Replace(s, m => m.Groups["url"].Value + "#" + Mask);
            s = UrlQuery.Replace(s, m => m.Groups["url"].Value + "?" + Mask);
            s = SecretParameter.Replace(s, m => m.Groups["name"].Value + Mask);
            s = CookieHeader.Replace(s, m => m.Groups["name"].Value + Mask);
            s = AuthorizationValue.Replace(s, m => m.Groups["scheme"].Value + Mask);
            s = KeyValueSecret.Replace(s, m => m.Groups["name"].Value + Mask);
            s = Jwt.Replace(s, Mask);
            s = KnownKeyFormats.Replace(s, Mask);
            s = Email.Replace(s, "[email]");
            s = identity.Replace(s);
            s = OtherUserProfile.Replace(s, m => m.Groups["root"].Value + UserMask);
            s = OpaqueToken.Replace(s, m => LooksLikeSecret(m.Value) ? Mask : m.Value);
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological message: logging nothing of it is safer than logging it unredacted.
            return "[message withheld: too long to redact]";
        }
    }

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

    /// <summary>This machine's user profile, user name and computer name, and how to hide them.</summary>
    private sealed class Identity
    {
        private readonly Regex? _profile;
        private readonly Regex? _userName;
        private readonly Regex? _computerName;

        public Identity(string userProfile, string userName, string computerName)
        {
            var profile = userProfile.TrimEnd('\\', '/');
            if (profile.Length > 3)
            {
                // Match either slash style: C:\Users\name and C:/Users/name.
                var pattern = string.Join(@"[\\/]+", profile.Split('\\', '/').Select(Regex.Escape));
                _profile = new Regex($@"{pattern}(?![^\\/\s""'<>:*?|])", IgnoreCase, Budget);
            }
            if (userName.Length >= 3 && !CommonNames.Contains(userName))
                _userName = new Regex($@"(?<![A-Za-z0-9_.-]){Regex.Escape(userName)}(?![A-Za-z0-9_-])", IgnoreCase, Budget);
            if (computerName.Length >= 3 && !CommonNames.Contains(computerName))
                _computerName = new Regex($@"(?<![A-Za-z0-9_.-]){Regex.Escape(computerName)}(?![A-Za-z0-9_-])", IgnoreCase, Budget);
        }

        public static Identity Current()
        {
            try
            {
                return new Identity(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.UserName,
                    Environment.MachineName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
            {
                // No identity to hide is better than no logging.
                return new Identity(string.Empty, string.Empty, string.Empty);
            }
        }

        public string Replace(string s)
        {
            if (_profile is not null) s = _profile.Replace(s, "%USERPROFILE%");
            if (_computerName is not null) s = _computerName.Replace(s, ComputerMask);
            if (_userName is not null) s = _userName.Replace(s, UserMask);
            return s;
        }
    }
}
