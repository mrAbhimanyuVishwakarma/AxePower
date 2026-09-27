using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Accounts;

namespace AxePower.Tests.Accounts;

/// <summary>A Supabase stand-in: each call runs the delegate a test sets, and is recorded by name.</summary>
internal sealed class FakeAccountApi : IAccountApi
{
    public List<string> Calls { get; } = new();

    public Func<IReadOnlyDictionary<string, string>, AuthSession?> SignUp { get; set; } = _ => null;
    public Func<AuthSession> SignIn { get; set; } = () => throw new InvalidOperationException("SignIn not set");
    public Func<string, AuthSession> Refresh { get; set; } = _ => throw new InvalidOperationException("Refresh not set");
    public Action SignOut { get; set; } = () => { };
    public Func<string, string, AccountProfile?> GetProfile { get; set; } = (_, _) => null;
    public Func<string, string, string?, AccountProfile> CreateProfile { get; set; } = (u, _, _) => Profile(u);
    public Func<string, string?, string?, AccountProfile> UpdateProfile { get; set; } = (_, _, _) => Profile();
    public Func<string, bool> Available { get; set; } = _ => true;
    public Func<bool, AccountProfile> SetDiscoverable { get; set; } = d => Profile() with { Discoverable = d };

    public Task<AccountProfile> SetDiscoverableAsync(string accessToken, string userId, bool discoverable, CancellationToken ct)
    { Calls.Add("SetDiscoverable"); return Task.FromResult(SetDiscoverable(discoverable)); }
    public Action Delete { get; set; } = () => { };
    public Func<AccountProvider, string, string, AuthSession> Exchange { get; set; } = (_, _, _) => throw new InvalidOperationException("Exchange not set");

    public Uri BuildSignInUrl(AccountProvider provider, Uri redirect, string codeChallenge)
    { Calls.Add("BuildSignInUrl"); return new Uri($"https://auth.example/{provider}?redirect={Uri.EscapeDataString(redirect.ToString())}&challenge={codeChallenge}"); }

    public Task<AuthSession> ExchangeCodeAsync(AccountProvider provider, string code, string codeVerifier, CancellationToken ct)
    { Calls.Add("Exchange"); return Task.FromResult(Exchange(provider, code, codeVerifier)); }

    public Task<AuthSession?> SignUpAsync(string email, string password, IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    { Calls.Add("SignUp"); return Task.FromResult(SignUp(metadata)); }

    public Task<AuthSession> SignInWithPasswordAsync(string email, string password, CancellationToken ct)
    { Calls.Add("SignIn"); return Task.FromResult(SignIn()); }

    public Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken ct)
    { Calls.Add("Refresh"); return Task.FromResult(Refresh(refreshToken)); }

    public Task RequestPasswordResetAsync(string email, CancellationToken ct)
    { Calls.Add("Reset"); return Task.CompletedTask; }

    public Task SignOutAsync(string accessToken, CancellationToken ct)
    { Calls.Add("SignOut"); SignOut(); return Task.CompletedTask; }

    public Task<AccountProfile?> GetProfileAsync(string accessToken, string userId, CancellationToken ct)
    { Calls.Add("GetProfile"); return Task.FromResult(GetProfile(accessToken, userId)); }

    public Task<AccountProfile> CreateProfileAsync(string accessToken, string userId, string username, string displayName, string? avatarUrl, CancellationToken ct)
    { Calls.Add("CreateProfile"); return Task.FromResult(CreateProfile(username, displayName, avatarUrl)); }

    public Task<AccountProfile> UpdateProfileAsync(string accessToken, string userId, string? displayName, string? username, CancellationToken ct)
    { Calls.Add("UpdateProfile"); return Task.FromResult(UpdateProfile(accessToken, displayName, username)); }

    public Task<bool> IsUsernameAvailableAsync(string username, CancellationToken ct)
    { Calls.Add("Available"); return Task.FromResult(Available(username)); }

    public Task DeleteAccountAsync(string accessToken, CancellationToken ct)
    { Calls.Add("Delete"); Delete(); return Task.CompletedTask; }

    public static AccountProfile Profile(string username = "mrabhimanyu") =>
        new("u-1", username, "Abhimanyu", null, new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc), null);
}
