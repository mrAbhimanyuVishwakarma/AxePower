using System.Text.RegularExpressions;

namespace AxePower.Core.Services.Accounts;

/// <summary>
/// The client-side copy of the rules in the A1 migration, so the form can say "not allowed" before a
/// round trip. The database stays the authority: it also holds the reserved names and uniqueness.
/// </summary>
public static class UsernameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 20;
    public const int DisplayNameMaxLength = 32;
    public const int ChangeIntervalDays = 30;

    private static readonly Regex Pattern = new(
        "^[a-z0-9_][a-z0-9_.]{1,18}[a-z0-9_]$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>What the user typed, as the database will store it: trimmed, no leading '@', lowercase.</summary>
    public static string Normalize(string? input) =>
        (input ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    public static bool IsValid(string? username) =>
        username is not null && Pattern.IsMatch(username) && !username.Contains("..");

    /// <summary>
    /// A valid username made from a provider's name ("Abhi Manyu" → "abhi_manyu"), or empty when
    /// nothing usable is left. Only a suggestion: availability is still checked as usual.
    /// </summary>
    public static string Suggest(string? name)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var c in Normalize(name))
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.') chars.Append(c);
            else if (c is ' ' or '-') chars.Append('_');
        }

        var candidate = chars.ToString();
        while (candidate.Contains("..")) candidate = candidate.Replace("..", ".");
        candidate = candidate.Trim('.');
        if (candidate.Length > MaxLength) candidate = candidate[..MaxLength].TrimEnd('.');
        return IsValid(candidate) ? candidate : string.Empty;
    }

    public static string NormalizeDisplayName(string? input) => (input ?? string.Empty).Trim();

    public static bool IsValidDisplayName(string? displayName) =>
        displayName is not null
        && displayName.Length is >= 1 and <= DisplayNameMaxLength
        && displayName == displayName.Trim()
        && !HiddenCharacters.IsMatch(displayName);

    /// <summary>Control and invisible/bidirectional formatting characters, which can make one name look like another.</summary>
    private static readonly Regex HiddenCharacters = new(
        @"[\u0001-\u001F\u007F-\u009F\u200B-\u200F\u202A-\u202E\u2060-\u2069\uFEFF]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Removes the characters <see cref="IsValidDisplayName"/> rejects (for names taken from a provider).</summary>
    public static string StripHidden(string? value) => HiddenCharacters.Replace(value ?? string.Empty, string.Empty);

    /// <summary>
    /// Avatars may only come from the sign-in providers' picture servers; the database enforces the
    /// same list (profiles_avatar_url_provider), because friends' apps load each other's avatars.
    /// </summary>
    public static bool IsAllowedAvatarUrl(string? url) =>
        url is not null && url.Length <= 1024 && AvatarHosts.IsMatch(url);

    private static readonly Regex AvatarHosts = new(
        @"^https://(([a-z0-9-]+\.)*(googleusercontent\.com|githubusercontent\.com|discordapp\.com|steamstatic\.com)|steamcdn-a\.akamaihd\.net)/",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
