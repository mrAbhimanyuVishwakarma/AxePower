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

    public static string NormalizeDisplayName(string? input) => (input ?? string.Empty).Trim();

    public static bool IsValidDisplayName(string? displayName) =>
        displayName is not null
        && displayName.Length is >= 1 and <= DisplayNameMaxLength
        && displayName == displayName.Trim();
}
