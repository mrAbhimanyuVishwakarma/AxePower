using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AxePower.Core.Services.Accounts;

/// <summary>The calls <see cref="AccountService"/> makes, so the service can be tested without a network.</summary>
public interface IAccountApi
{
    /// <summary>Returns the session, or null when Supabase sent a confirmation email instead.</summary>
    Task<AuthSession?> SignUpAsync(string email, string password, IReadOnlyDictionary<string, string> metadata, CancellationToken ct);

    Task<AuthSession> SignInWithPasswordAsync(string email, string password, CancellationToken ct);
    Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>The page that starts a browser sign-in with <paramref name="provider"/>, returning to <paramref name="redirect"/>.</summary>
    Uri BuildSignInUrl(AccountProvider provider, Uri redirect, string codeChallenge);

    /// <summary>Trades the code the browser brought back, plus the PKCE verifier, for a session.</summary>
    Task<AuthSession> ExchangeCodeAsync(AccountProvider provider, string code, string codeVerifier, CancellationToken ct);
    Task RequestPasswordResetAsync(string email, CancellationToken ct);
    Task SignOutAsync(string accessToken, CancellationToken ct);

    Task<AccountProfile?> GetProfileAsync(string accessToken, string userId, CancellationToken ct);
    Task<AccountProfile> CreateProfileAsync(string accessToken, string userId, string username, string displayName, string? avatarUrl, CancellationToken ct);
    Task<AccountProfile> UpdateProfileAsync(string accessToken, string userId, string? displayName, string? username, CancellationToken ct);
    Task<bool> IsUsernameAvailableAsync(string username, CancellationToken ct);

    /// <summary>Deletes the caller's account through the <c>delete-account</c> Edge Function.</summary>
    Task DeleteAccountAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// Supabase Auth (GoTrue), PostgREST and Edge Functions over plain HTTP.
///
/// Kept deliberately small instead of taking the Supabase SDK: these are a handful of documented
/// endpoints, and owning them lets tests swap the network for a stub handler. The publishable key
/// goes only in the <c>apikey</c> header. It is not a JWT, so it must never be sent as a bearer
/// token; the bearer is always the user's own access token.
/// </summary>
public sealed partial class SupabaseAccountApi : IAccountApi, IDisposable
{
    private const string ProfileColumns = "id,username,display_name,avatar_url,created_at,username_changed_at";

    private readonly SupabaseProject _project;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;

    public SupabaseAccountApi(SupabaseProject project, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        _project = project;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            BaseAddress = project.Url,
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public void Dispose() => _http.Dispose();

    // -- Auth -----------------------------------------------------------------------------------

    public async Task<AuthSession?> SignUpAsync(
        string email, string password, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        var data = new JsonObject();
        foreach (var (key, value) in metadata) data[key] = value;

        var body = new JsonObject { ["email"] = email, ["password"] = password, ["data"] = data };
        var json = await SendAsync(HttpMethod.Post, "auth/v1/signup", body, accessToken: null, ct);

        // With email confirmation on, Supabase answers with the bare user and no tokens.
        return json?["access_token"] is null ? null : ParseSession(json);
    }

    public async Task<AuthSession> SignInWithPasswordAsync(string email, string password, CancellationToken ct)
    {
        var body = new JsonObject { ["email"] = email, ["password"] = password };
        var json = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=password", body, null, ct);
        return ParseSession(json);
    }

    public async Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var body = new JsonObject { ["refresh_token"] = refreshToken };
        try
        {
            var json = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token", body, null, ct);
            return ParseSession(json);
        }
        catch (AccountException ex) when (ex.Error is not (AccountError.Offline or AccountError.ServiceUnavailable
                                                          or AccountError.RateLimited or AccountError.SessionExpired))
        {
            // Any other refusal means this token can never work again (a damaged one comes back as
            // "validation_failed"). Calling it expired signs the user out cleanly instead of leaving
            // them "offline" forever.
            throw new AccountException(AccountError.SessionExpired, "Your session has ended. Sign in again.", ex);
        }
    }

    public Uri BuildSignInUrl(AccountProvider provider, Uri redirect, string codeChallenge)
    {
        var redirectTo = Uri.EscapeDataString(redirect.ToString());
        var challenge = Uri.EscapeDataString(codeChallenge);

        // Steam is OpenID 2.0, which Supabase Auth does not speak; our steam-login function does.
        var path = provider == AccountProvider.Steam
            ? $"functions/v1/steam-login?redirect={redirectTo}&challenge={challenge}"
            : $"auth/v1/authorize?provider={ProviderId(provider)}&redirect_to={redirectTo}" +
              $"&code_challenge={challenge}&code_challenge_method=s256";

        return new Uri(_project.Url, path);
    }

    public async Task<AuthSession> ExchangeCodeAsync(AccountProvider provider, string code, string codeVerifier, CancellationToken ct)
    {
        var json = provider == AccountProvider.Steam
            ? await SendAsync(HttpMethod.Post, "functions/v1/steam-login",
                new JsonObject { ["action"] = "exchange", ["code"] = code, ["verifier"] = codeVerifier }, null, ct)
            : await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=pkce",
                new JsonObject { ["auth_code"] = code, ["code_verifier"] = codeVerifier }, null, ct);
        return ParseSession(json);
    }

    internal static string ProviderId(AccountProvider provider) => provider switch
    {
        AccountProvider.Google => "google",
        AccountProvider.GitHub => "github",
        AccountProvider.Discord => "discord",
        AccountProvider.Steam => "steam",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public Task RequestPasswordResetAsync(string email, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "auth/v1/recover", new JsonObject { ["email"] = email }, null, ct);

    // "local" ends only this PC's session; being signed in on the phone is untouched, as with Steam.
    public Task SignOutAsync(string accessToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "auth/v1/logout?scope=local", null, accessToken, ct);

    // -- Profiles -------------------------------------------------------------------------------

    public async Task<AccountProfile?> GetProfileAsync(string accessToken, string userId, CancellationToken ct)
    {
        var json = await SendAsync(
            HttpMethod.Get, $"rest/v1/profiles?id=eq.{Uri.EscapeDataString(userId)}&select={ProfileColumns}", null, accessToken, ct);
        return json is JsonArray { Count: > 0 } rows ? ParseProfile(rows[0]!) : null;
    }

    public async Task<AccountProfile> CreateProfileAsync(
        string accessToken, string userId, string username, string displayName, string? avatarUrl, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["id"] = userId,
            ["username"] = username,
            ["display_name"] = displayName,
            ["avatar_url"] = avatarUrl
        };
        var json = await SendAsync(HttpMethod.Post, $"rest/v1/profiles?select={ProfileColumns}", body, accessToken, ct, returnRepresentation: true);
        return SingleProfile(json);
    }

    public async Task<AccountProfile> UpdateProfileAsync(
        string accessToken, string userId, string? displayName, string? username, CancellationToken ct)
    {
        var body = new JsonObject();
        if (displayName is not null) body["display_name"] = displayName;
        if (username is not null) body["username"] = username;

        var json = await SendAsync(
            HttpMethod.Patch, $"rest/v1/profiles?id=eq.{Uri.EscapeDataString(userId)}&select={ProfileColumns}",
            body, accessToken, ct, returnRepresentation: true);
        return SingleProfile(json);
    }

    public async Task<bool> IsUsernameAvailableAsync(string username, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "rest/v1/rpc/username_available", new JsonObject { ["name"] = username }, null, ct);
        return json?.GetValueKind() == JsonValueKind.True;
    }

    public Task DeleteAccountAsync(string accessToken, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "functions/v1/delete-account", new JsonObject(), accessToken, ct);

    // -- Plumbing -------------------------------------------------------------------------------

    private async Task<JsonNode?> SendAsync(
        HttpMethod method, string path, JsonNode? body, string? accessToken, CancellationToken ct,
        bool returnRepresentation = false, string? prefer = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", _project.PublishableKey);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var preferences = string.Join(",", new[] { returnRepresentation ? "return=representation" : null, prefer }.Where(p => p is not null));
        if (preferences.Length > 0) request.Headers.Add("Prefer", preferences);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AccountException(AccountError.Offline, "AxePower accounts did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new AccountException(AccountError.Offline, "Can't reach AxePower accounts. Check your connection.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw MapError(response.StatusCode, text);
            if (string.IsNullOrWhiteSpace(text)) return null;

            try { return JsonNode.Parse(text); }
            catch (JsonException ex) { throw new AccountException(AccountError.Unknown, "AxePower accounts sent an unreadable reply.", ex); }
        }
    }

    /// <summary>
    /// Turns an error reply into something the UI can act on. Auth errors carry <c>error_code</c>
    /// (older ones <c>error</c>); database errors carry a Postgres <c>code</c> and our own exception text.
    /// </summary>
    internal static AccountException MapError(HttpStatusCode status, string body)
    {
        string code = string.Empty, message = string.Empty;
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
            {
                code = Str(o, "error_code") ?? Str(o, "code") ?? Str(o, "error") ?? string.Empty;
                message = Str(o, "msg") ?? Str(o, "message") ?? Str(o, "error_description") ?? string.Empty;
            }
        }
        catch (JsonException) { }

        AccountException Fail(AccountError error, string text) => new(error, text);

        if ((int)status == 429 || code.StartsWith("over_", StringComparison.Ordinal))
            return Fail(AccountError.RateLimited, "Too many attempts. Wait a minute and try again.");

        switch (code)
        {
            case "invalid_credentials":
            case "invalid_grant" when message.Contains("credentials", StringComparison.OrdinalIgnoreCase):
                return Fail(AccountError.InvalidCredentials, "Wrong email or password.");
            case "email_not_confirmed":
                return Fail(AccountError.EmailNotConfirmed, "Confirm your email first: open the link we sent you.");
            case "user_already_exists":
            case "email_exists":
                return Fail(AccountError.EmailTaken, "An account with this email already exists.");
            case "email_address_invalid":
            case "email_address_not_authorized":
                return Fail(AccountError.InvalidEmail, "That email address can't be used.");
            case "weak_password":
                return Fail(AccountError.WeakPassword, string.IsNullOrEmpty(message) ? "Choose a stronger password." : message);
            case "refresh_token_not_found":
            case "refresh_token_already_used":
            case "session_not_found":
            case "session_expired":
            case "bad_jwt":
            case "user_not_found":
            case "PGRST301":
            case "PGRST303":
                return Fail(AccountError.SessionExpired, "Your session has ended. Sign in again.");
            case "23505":
                return Fail(AccountError.UsernameTaken, "That username is taken.");
            case "P0001" when message == "USER_NOT_FOUND":
                return Fail(AccountError.FriendNotFound, "No one has that username.");
            case "P0001" when message == "CANNOT_ADD_SELF":
                return Fail(AccountError.CannotAddSelf, "That's you.");
            case "P0001" when message == "YOU_BLOCKED_THEM":
                return Fail(AccountError.YouBlockedThem, "You blocked this person. Unblock them first.");
            case "P0001" when message == "TOO_MANY_REQUESTS":
                return Fail(AccountError.RateLimited, "You have too many requests waiting. Cancel some first.");
            case "P0001" when message == "TOO_MANY_INVITES":
                return Fail(AccountError.RateLimited, "Too many invites in the last hour. Try again later.");
            case "P0001" when message == "REQUEST_NOT_FOUND":
                return Fail(AccountError.RequestNotFound, "That request is no longer there.");
            case "42501" when message.Contains("friend_invites", StringComparison.Ordinal):
                return Fail(AccountError.NotFriends, "You can only invite friends.");
            case "P0001" when message == "USERNAME_RESERVED":
                return Fail(AccountError.UsernameReserved, "That username is reserved.");
            case "P0001" when message == "USERNAME_CHANGE_TOO_SOON":
                return Fail(AccountError.UsernameChangeTooSoon, $"You can change your username once every {UsernameRules.ChangeIntervalDays} days.");
            case "23514" when message.Contains("profiles_username_format", StringComparison.Ordinal):
                return Fail(AccountError.UsernameInvalid, "Usernames are 3-20 characters: a-z, 0-9, '_' and '.'.");
            case "23514" when message.Contains("profiles_display_name_length", StringComparison.Ordinal):
                return Fail(AccountError.DisplayNameInvalid, $"Display names are 1-{UsernameRules.DisplayNameMaxLength} characters.");
        }

        if (status == HttpStatusCode.Unauthorized)
            return Fail(AccountError.SessionExpired, "Your session has ended. Sign in again.");
        if ((int)status >= 500)
            return Fail(AccountError.ServiceUnavailable, "AxePower accounts are having trouble. Try again shortly.");

        return Fail(AccountError.Unknown, string.IsNullOrEmpty(message) ? $"Request failed ({(int)status})." : message);
    }

    private AuthSession ParseSession(JsonNode? json)
    {
        if (json is not JsonObject o || Str(o, "access_token") is not { } access || Str(o, "refresh_token") is not { } refresh
            || o["user"] is not JsonObject user || Str(user, "id") is not { } userId)
        {
            throw new AccountException(AccountError.Unknown, "AxePower accounts sent an incomplete session.");
        }

        DateTime expires = o["expires_at"] is JsonValue at && at.TryGetValue(out long unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : _utcNow().AddSeconds(o["expires_in"] is JsonValue inV && inV.TryGetValue(out int seconds) ? seconds : 3600);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (user["user_metadata"] is JsonObject meta)
        {
            foreach (var (key, value) in meta)
            {
                if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String) metadata[key] = v.GetValue<string>();
            }
        }

        var provider = user["app_metadata"] is JsonObject app ? Str(app, "provider") : null;
        return new AuthSession(access, refresh, expires, userId, Str(user, "email"), provider, metadata);
    }

    private static AccountProfile SingleProfile(JsonNode? json) =>
        json is JsonArray { Count: > 0 } rows
            ? ParseProfile(rows[0]!)
            : throw new AccountException(AccountError.Unknown, "Your profile could not be saved.");

    internal static AccountProfile ParseProfile(JsonNode row) => new(
        UserId: Str(row, "id") ?? string.Empty,
        Username: Str(row, "username") ?? string.Empty,
        DisplayName: Str(row, "display_name") ?? string.Empty,
        AvatarUrl: Str(row, "avatar_url"),
        CreatedAtUtc: Date(row, "created_at") ?? DateTime.MinValue,
        UsernameChangedAtUtc: Date(row, "username_changed_at"));

    private static string? Str(JsonNode node, string name) =>
        node[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static DateTime? Date(JsonNode node, string name) =>
        Str(node, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime
            : null;
}
