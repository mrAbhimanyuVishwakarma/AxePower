using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services.Accounts;

public enum SignUpOutcome
{
    /// <summary>The usual case: Supabase emailed a confirmation link; the user signs in after opening it.</summary>
    ConfirmationEmailSent,

    /// <summary>Email confirmation is off in the project, so the account is already signed in.</summary>
    SignedIn
}

/// <summary>
/// The optional AxePower account: who is signed in on this PC, and their profile.
///
/// Signed out is a first-class state; nothing in AxePower may require an account. Being offline
/// never signs anyone out: the last profile seen stays on screen and the session is retried on the
/// next start. Only Supabase saying the session is gone ends it.
///
/// Operations run one at a time, because a refresh token is single-use: two refreshes racing each
/// other would end the session.
/// </summary>
public sealed class AccountService : IDisposable
{
    /// <summary>Refresh this long before the access token actually expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly IAccountApi _api;
    private readonly AccountSessionStore _store;
    private readonly IClock _clock;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long to wait for the browser before giving up on a sign-in.</summary>
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<IOAuthLoopback> _startLoopback;

    private StoredAccountSession? _session;
    private IReadOnlyDictionary<string, string> _metadata = new Dictionary<string, string>();

    public AccountService(
        IAccountApi api, AccountSessionStore store, IClock clock, LogService log, Func<IOAuthLoopback>? startLoopback = null)
    {
        _api = api;
        _store = store;
        _clock = clock;
        _log = log;
        _startLoopback = startLoopback ?? OAuthLoopback.Start;
    }

    public AccountState State { get; private set; } = AccountState.SignedOut;

    public event EventHandler<AccountState>? StateChanged;

    /// <summary>
    /// Restores the saved session at start-up: shows the cached profile at once, then refreshes it.
    /// Never throws; a failure leaves the cached state (offline) or signs out (session gone).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _session = _store.Load();
            if (_session is null)
            {
                Publish(AccountState.SignedOut);
                return;
            }

            Publish(StateFromSession(isOffline: false));

            try
            {
                Apply(await _api.RefreshAsync(_session.RefreshToken, ct));
                await LoadOrCreateProfileAsync(ct);
            }
            catch (AccountException ex) when (IsSessionGone(ex))
            {
                _log.LogInfo("[Account] The saved session has ended; signed out.");
                EndSession();
            }
            catch (AccountException ex)
            {
                _log.LogWarning($"[Account] Could not refresh the session ({ex.Error}); showing the saved profile.");
                Publish(StateFromSession(isOffline: true));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> IsUsernameAvailableAsync(string username, CancellationToken ct = default)
    {
        var name = UsernameRules.Normalize(username);
        return UsernameRules.IsValid(name) ? _api.IsUsernameAvailableAsync(name, ct) : Task.FromResult(false);
    }

    /// <summary>
    /// Creates an email account. The username and display name travel in the sign-up metadata and
    /// become the profile at the first sign-in, because until the email is confirmed there is no
    /// session to write the profile with.
    /// </summary>
    public async Task<SignUpOutcome> SignUpAsync(
        string email, string password, string username, string displayName, CancellationToken ct = default)
    {
        var name = UsernameRules.Normalize(username);
        var display = UsernameRules.NormalizeDisplayName(displayName);
        ValidateProfileInput(name, display);

        await _gate.WaitAsync(ct);
        try
        {
            if (!await _api.IsUsernameAvailableAsync(name, ct))
                throw new AccountException(AccountError.UsernameTaken, "That username is taken.");

            var metadata = new Dictionary<string, string> { ["username"] = name, ["display_name"] = display };
            var session = await _api.SignUpAsync(email.Trim(), password, metadata, ct);
            if (session is null)
            {
                _log.LogInfo("[Account] Sign-up started; waiting for the email to be confirmed.");
                return SignUpOutcome.ConfirmationEmailSent;
            }

            await FinishSignInAsync(session, ct);
            return SignUpOutcome.SignedIn;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignInWithPasswordAsync(string email, string password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await FinishSignInAsync(await _api.SignInWithPasswordAsync(email.Trim(), password, ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One-click sign-in: opens the provider's page in the browser, waits for the browser to come
    /// back to AxePower, then trades the returned code for a session. With PKCE, the code is useless
    /// to anyone without the verifier, which never leaves this process.
    /// </summary>
    /// <param name="openBrowser">Opens a URL in the user's browser.</param>
    public async Task SignInWithProviderAsync(AccountProvider provider, Action<Uri> openBrowser, CancellationToken ct = default)
    {
        using var loopback = _startLoopback();
        var verifier = Pkce.CreateVerifier();
        openBrowser(_api.BuildSignInUrl(provider, loopback.RedirectUri, Pkce.Challenge(verifier)));

        IReadOnlyDictionary<string, string> reply;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(BrowserTimeout);
            try
            {
                reply = await loopback.WaitForCallbackAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AccountException(AccountError.Unknown, "Sign-in timed out. Try again when you're ready.");
            }
        }

        if (reply.TryGetValue("error_description", out var why) || reply.TryGetValue("error", out why))
        {
            _log.LogInfo($"[Account] {provider} sign-in was not completed ({(reply.TryGetValue("error", out var e) ? e : "error")}).");
            throw new AccountException(AccountError.Unknown,
                string.IsNullOrWhiteSpace(why) ? $"{provider} sign-in didn't finish." : $"{provider} sign-in didn't finish: {why}");
        }

        if (!reply.TryGetValue("code", out var code) || code.Length == 0)
            throw new AccountException(AccountError.Unknown, $"{provider} sign-in came back without a code. Try again.");

        await _gate.WaitAsync(ct);
        try
        {
            await FinishSignInAsync(await _api.ExchangeCodeAsync(provider, code, verifier, ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A username to offer after a one-click sign-in, made from the provider's name. May be empty.</summary>
    public string SuggestedUsername
    {
        get
        {
            foreach (var key in new[] { "preferred_username", "user_name", "name", "full_name" })
            {
                if (_metadata.TryGetValue(key, out var value) && UsernameRules.Suggest(value) is { Length: > 0 } suggestion)
                    return suggestion;
            }
            return string.Empty;
        }
    }

    /// <summary>A display name to offer after a one-click sign-in. May be empty.</summary>
    public string SuggestedDisplayName
    {
        get
        {
            foreach (var key in new[] { "full_name", "name", "preferred_username", "user_name" })
            {
                if (_metadata.TryGetValue(key, out var value)
                    && UsernameRules.NormalizeDisplayName(UsernameRules.StripHidden(value)) is { Length: > 0 } name)
                    return name.Length <= UsernameRules.DisplayNameMaxLength ? name : name[..UsernameRules.DisplayNameMaxLength].Trim();
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// A valid access token for other services that call Supabase as this user (Friends). Refreshes
    /// first when it is about to expire, or when <paramref name="forceRefresh"/> says the server
    /// rejected it. Throws <see cref="AccountException"/> (NotSignedIn / SessionExpired / Offline).
    /// </summary>
    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = RequireSession();
            if (forceRefresh || session.ExpiresAtUtc - _clock.UtcNow < RefreshMargin) await RefreshOrEndAsync(ct);
            return RequireSession().AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RequestPasswordResetAsync(string email, CancellationToken ct = default) =>
        _api.RequestPasswordResetAsync(email.Trim(), ct);

    /// <summary>For <see cref="AccountStatus.NeedsProfile"/>: pick the @username after a social sign-in.</summary>
    public async Task CreateProfileAsync(string username, string displayName, CancellationToken ct = default)
    {
        var name = UsernameRules.Normalize(username);
        var display = UsernameRules.NormalizeDisplayName(displayName);
        ValidateProfileInput(name, display);

        await _gate.WaitAsync(ct);
        try
        {
            var session = RequireSession();
            var profile = await WithTokenAsync(
                token => _api.CreateProfileAsync(token, session.UserId, name, display, ProviderAvatar(), ct), ct);
            SetProfile(profile);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Changes the display name and/or the @username (the latter once every 30 days).</summary>
    public async Task UpdateProfileAsync(string? displayName = null, string? username = null, CancellationToken ct = default)
    {
        string? name = username is null ? null : UsernameRules.Normalize(username);
        string? display = displayName is null ? null : UsernameRules.NormalizeDisplayName(displayName);
        if (name is not null && !UsernameRules.IsValid(name))
            throw new AccountException(AccountError.UsernameInvalid, "Usernames are 3-20 characters: a-z, 0-9, '_' and '.'.");
        if (display is not null && !UsernameRules.IsValidDisplayName(display))
            throw new AccountException(AccountError.DisplayNameInvalid, $"Display names are 1-{UsernameRules.DisplayNameMaxLength} characters.");

        await _gate.WaitAsync(ct);
        try
        {
            var session = RequireSession();
            if (name == State.Profile?.Username) name = null;
            if (name is null && display is null) return;

            var profile = await WithTokenAsync(
                token => _api.UpdateProfileAsync(token, session.UserId, display, name, ct), ct);
            SetProfile(profile);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Signs this PC out. Always succeeds locally, even offline.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_session is { } session)
            {
                try { await _api.SignOutAsync(session.AccessToken, ct); }
                catch (AccountException ex) { _log.LogInfo($"[Account] Server sign-out skipped ({ex.Error}); signed out locally."); }
            }

            EndSession();
            _log.LogInfo("[Account] Signed out.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the account and everything tied to it on the server, then signs out.</summary>
    public async Task DeleteAccountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RequireSession();
            await WithTokenAsync(async token => { await _api.DeleteAccountAsync(token, ct); return true; }, ct);
            EndSession();
            _log.LogInfo("[Account] Account deleted.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    // -- Internals -------------------------------------------------------------------------------

    private static void ValidateProfileInput(string username, string displayName)
    {
        if (!UsernameRules.IsValid(username))
            throw new AccountException(AccountError.UsernameInvalid, "Usernames are 3-20 characters: a-z, 0-9, '_' and '.'.");
        if (!UsernameRules.IsValidDisplayName(displayName))
            throw new AccountException(AccountError.DisplayNameInvalid, $"Display names are 1-{UsernameRules.DisplayNameMaxLength} characters.");
    }

    private StoredAccountSession RequireSession() =>
        _session ?? throw new AccountException(AccountError.NotSignedIn, "Sign in first.");

    private static bool IsSessionGone(AccountException ex) =>
        ex.Error is AccountError.SessionExpired or AccountError.InvalidCredentials;

    /// <summary>
    /// Runs a call with a fresh access token. Refreshes first when the token is about to expire, and
    /// once more if the server still calls it expired. If the session is gone for good, signs out.
    /// </summary>
    private async Task<T> WithTokenAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        var session = RequireSession();
        if (session.ExpiresAtUtc - _clock.UtcNow < RefreshMargin) await RefreshOrEndAsync(ct);

        try
        {
            return await call(RequireSession().AccessToken);
        }
        catch (AccountException ex) when (ex.Error == AccountError.SessionExpired)
        {
            await RefreshOrEndAsync(ct);
            return await call(RequireSession().AccessToken);
        }
    }

    private async Task RefreshOrEndAsync(CancellationToken ct)
    {
        try
        {
            Apply(await _api.RefreshAsync(RequireSession().RefreshToken, ct));
        }
        catch (AccountException ex) when (IsSessionGone(ex))
        {
            EndSession();
            throw new AccountException(AccountError.SessionExpired, "Your session has ended. Sign in again.", ex);
        }
    }

    /// <summary>
    /// Keeps a new session and loads its profile. If only the profile load fails (say the connection
    /// dropped in between), the user is still signed in and sees that the profile is not loaded yet.
    /// </summary>
    private async Task FinishSignInAsync(AuthSession auth, CancellationToken ct)
    {
        Apply(auth);
        try
        {
            await LoadOrCreateProfileAsync(ct);
        }
        catch (AccountException ex) when (!IsSessionGone(ex))
        {
            Publish(StateFromSession(isOffline: true));
            throw;
        }
    }

    /// <summary>Loads the profile; at the first sign-in after an email sign-up, creates it from the sign-up metadata.</summary>
    private async Task LoadOrCreateProfileAsync(CancellationToken ct)
    {
        var session = RequireSession();
        var profile = await WithTokenAsync(token => _api.GetProfileAsync(token, session.UserId, ct), ct);

        if (profile is null
            && _metadata.TryGetValue("username", out var wanted) && UsernameRules.IsValid(wanted))
        {
            var display = _metadata.TryGetValue("display_name", out var d) && UsernameRules.IsValidDisplayName(d) ? d : wanted;
            try
            {
                profile = await WithTokenAsync(
                    token => _api.CreateProfileAsync(token, session.UserId, wanted, display, ProviderAvatar(), ct), ct);
            }
            catch (AccountException ex) when (ex.Error is AccountError.UsernameTaken or AccountError.UsernameReserved)
            {
                // Someone claimed the name between sign-up and confirmation; the user picks another.
                _log.LogInfo("[Account] The username chosen at sign-up is no longer free; asking for another.");
            }
        }

        SetProfile(profile);
    }

    /// <summary>The provider's picture (Google, Discord, Steam) when it is an https URL; otherwise initials are shown.</summary>
    private string? ProviderAvatar()
    {
        foreach (var key in new[] { "avatar_url", "picture" })
        {
            if (_metadata.TryGetValue(key, out var url) && UsernameRules.IsAllowedAvatarUrl(url)) return url;
        }

        return null;
    }

    private void Apply(AuthSession auth)
    {
        _metadata = auth.UserMetadata;
        _session = new StoredAccountSession
        {
            AccessToken = auth.AccessToken,
            RefreshToken = auth.RefreshToken,
            ExpiresAtUtc = auth.ExpiresAtUtc,
            UserId = auth.UserId,
            Email = auth.Email,
            Provider = auth.Provider,
            Profile = _session?.UserId == auth.UserId ? _session.Profile : null
        };
        _store.Save(_session);
    }

    private void SetProfile(AccountProfile? profile)
    {
        var session = RequireSession();
        session.Profile = profile;
        _store.Save(session);
        Publish(StateFromSession(isOffline: false));

        if (profile is not null) _log.LogInfo($"[Account] Signed in as @{profile.Username}.");
    }

    private AccountState StateFromSession(bool isOffline)
    {
        var session = RequireSession();
        var status = session.Profile is null ? AccountStatus.NeedsProfile : AccountStatus.SignedIn;
        return new AccountState(status, session.UserId, session.Email, session.Profile, isOffline, session.Provider);
    }

    private void EndSession()
    {
        _session = null;
        _metadata = new Dictionary<string, string>();
        _store.Clear();
        Publish(AccountState.SignedOut);
    }

    private void Publish(AccountState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
