using System;
using System.Collections.Generic;

namespace AxePower.Core.Services.Accounts;

/// <summary>
/// The Supabase project behind AxePower accounts. Both values are public by design: the
/// publishable key only reaches what Row Level Security allows, and it ships inside the apps.
/// The project's secret key must never appear anywhere in this repo.
/// </summary>
public sealed record SupabaseProject(Uri Url, string PublishableKey)
{
    public static SupabaseProject AxePower { get; } = new(
        new Uri("https://kjnqsgdlcytpooueymdt.supabase.co"),
        "sb_publishable__WFKuy8jF5iwSMH0dgTikw_Hv864_Pp");
}

/// <summary>The one-click sign-ins. Email and password is separate.</summary>
public enum AccountProvider
{
    Google,
    GitHub,
    Discord,
    Steam
}

public enum AccountStatus
{
    SignedOut,

    /// <summary>Signed in, but no @username yet (a social sign-in, or the chosen name was taken meanwhile).</summary>
    NeedsProfile,

    SignedIn
}

/// <summary>A row of <c>public.profiles</c>.</summary>
public sealed record AccountProfile(
    string UserId,
    string Username,
    string DisplayName,
    string? AvatarUrl,
    DateTime CreatedAtUtc,
    DateTime? UsernameChangedAtUtc);

/// <summary>What the UI shows. Immutable; <see cref="AccountService.StateChanged"/> hands out a new one.</summary>
public sealed record AccountState(
    AccountStatus Status,
    string? UserId,
    string? Email,
    AccountProfile? Profile,
    bool IsOffline,
    string? Provider = null)
{
    public static AccountState SignedOut { get; } = new(AccountStatus.SignedOut, null, null, null, false);

    /// <summary>Up to two letters for the avatar circle when there is no picture.</summary>
    public string Initials
    {
        get
        {
            var source = Profile?.DisplayName ?? Profile?.Username ?? Email ?? string.Empty;
            var words = source.Split(new[] { ' ', '.', '_', '-', '@' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length switch
            {
                0 => string.Empty,
                1 => words[0][..1].ToUpperInvariant(),
                _ => (words[0][..1] + words[1][..1]).ToUpperInvariant()
            };
        }
    }

    /// <summary>When the 30-day username limit lifts, or null if a change is allowed now.</summary>
    public DateTime? UsernameLockedUntilUtc(DateTime nowUtc)
    {
        var until = Profile?.UsernameChangedAtUtc?.AddDays(UsernameRules.ChangeIntervalDays);
        return until > nowUtc ? until : null;
    }
}

/// <summary>A signed-in session as Supabase Auth returns it.</summary>
public sealed record AuthSession(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAtUtc,
    string UserId,
    string? Email,
    string? Provider,
    IReadOnlyDictionary<string, string> UserMetadata);

public enum AccountError
{
    /// <summary>No connection, or Supabase did not answer in time.</summary>
    Offline,

    /// <summary>Supabase answered with a server error.</summary>
    ServiceUnavailable,

    NotSignedIn,
    InvalidCredentials,
    EmailNotConfirmed,
    EmailTaken,
    InvalidEmail,
    WeakPassword,
    RateLimited,

    /// <summary>The stored session was revoked or expired for good; the user has to sign in again.</summary>
    SessionExpired,

    UsernameInvalid,
    UsernameTaken,
    UsernameReserved,
    UsernameChangeTooSoon,
    DisplayNameInvalid,

    Unknown
}

public sealed class AccountException : Exception
{
    public AccountException(AccountError error, string message, Exception? inner = null)
        : base(message, inner) => Error = error;

    public AccountError Error { get; }
}
