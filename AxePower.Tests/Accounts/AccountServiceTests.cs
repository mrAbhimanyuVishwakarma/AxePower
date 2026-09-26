using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Accounts;

/// <summary>
/// Sign-in state against a fake Supabase. Proves the logic (offline keeps you signed in, a revoked
/// session signs you out, the profile is created from sign-up metadata); the real service is
/// exercised live in A3.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerAccountTests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccountApi _api = new();
    private readonly AccountSessionStore _store;
    private readonly AccountService _service;

    public AccountServiceTests()
    {
        _store = new AccountSessionStore(new LogService(), _dir);
        _service = new AccountService(_api, _store, new FixedClock(), new LogService());
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AccountProfile Profile(string username = "mrabhimanyu") =>
        new("u-1", username, "Abhimanyu", null, Now.AddDays(-1), null);

    private static AuthSession Session(string token = "acc-2", Dictionary<string, string>? metadata = null) =>
        new(token, "ref-" + token, Now.AddHours(1), "u-1", "me@example.com", "email", metadata ?? new Dictionary<string, string>());

    private void SaveSession(AccountProfile? profile, DateTime? expires = null) => _store.Save(new StoredAccountSession
    {
        AccessToken = "acc-1",
        RefreshToken = "ref-1",
        ExpiresAtUtc = expires ?? Now.AddHours(1),
        UserId = "u-1",
        Email = "me@example.com",
        Profile = profile
    });

    // -- Start-up --------------------------------------------------------------------------------

    [Fact]
    public async Task Initialize_WithNothingSaved_IsSignedOut_WithoutTouchingTheNetwork()
    {
        await _service.InitializeAsync();

        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Initialize_RefreshesTheSavedSession_AndSavesTheNewTokens()
    {
        SaveSession(Profile());
        _api.Refresh = _ => Session("acc-2");
        _api.GetProfile = (_, _) => Profile();

        await _service.InitializeAsync();

        Assert.Equal(AccountStatus.SignedIn, _service.State.Status);
        Assert.Equal("mrabhimanyu", _service.State.Profile!.Username);
        Assert.False(_service.State.IsOffline);
        Assert.Equal("ref-acc-2", _store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Initialize_Offline_KeepsYouSignedIn_WithTheSavedProfile()
    {
        SaveSession(Profile());
        _api.Refresh = _ => throw new AccountException(AccountError.Offline, "offline");

        await _service.InitializeAsync();

        Assert.Equal(AccountStatus.SignedIn, _service.State.Status);
        Assert.True(_service.State.IsOffline);
        Assert.Equal("mrabhimanyu", _service.State.Profile!.Username);
        Assert.NotNull(_store.Load());
    }

    [Fact]
    public async Task Initialize_WithARevokedSession_SignsOut_AndForgetsIt()
    {
        SaveSession(Profile());
        _api.Refresh = _ => throw new AccountException(AccountError.SessionExpired, "gone");

        await _service.InitializeAsync();

        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.Null(_store.Load());
    }

    // -- Sign-up and sign-in ---------------------------------------------------------------------

    [Fact]
    public async Task SignUp_RejectsABadUsername_BeforeAnyNetworkCall()
    {
        var ex = await Assert.ThrowsAsync<AccountException>(() => _service.SignUpAsync("me@example.com", "pw123456", "a..b", "Me"));

        Assert.Equal(AccountError.UsernameInvalid, ex.Error);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task SignUp_WithATakenUsername_Fails()
    {
        _api.Available = _ => false;

        var ex = await Assert.ThrowsAsync<AccountException>(() => _service.SignUpAsync("me@example.com", "pw123456", "mrabhimanyu", "Me"));

        Assert.Equal(AccountError.UsernameTaken, ex.Error);
        Assert.DoesNotContain("SignUp", _api.Calls);
    }

    [Fact]
    public async Task SignUp_SendsANormalisedName_AndWaitsForTheEmail()
    {
        IReadOnlyDictionary<string, string>? sent = null;
        _api.SignUp = metadata => { sent = metadata; return null; };

        var outcome = await _service.SignUpAsync(" me@example.com ", "pw123456", " @MrAbhimanyu ", " Abhimanyu ");

        Assert.Equal(SignUpOutcome.ConfirmationEmailSent, outcome);
        Assert.Equal("mrabhimanyu", sent!["username"]);
        Assert.Equal("Abhimanyu", sent["display_name"]);
        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task FirstSignIn_AfterConfirming_CreatesTheProfileFromTheSignUpDetails()
    {
        var metadata = new Dictionary<string, string> { ["username"] = "mrabhimanyu", ["display_name"] = "Abhimanyu" };
        _api.SignIn = () => Session(metadata: metadata);
        _api.GetProfile = (_, _) => null;
        (string Username, string Display)? created = null;
        _api.CreateProfile = (username, display, _) => { created = (username, display); return Profile(username); };

        await _service.SignInWithPasswordAsync("me@example.com", "pw123456");

        Assert.Equal(("mrabhimanyu", "Abhimanyu"), created);
        Assert.Equal(AccountStatus.SignedIn, _service.State.Status);
        Assert.Equal("mrabhimanyu", _store.Load()!.Profile!.Username);
    }

    [Fact]
    public async Task FirstSignIn_WhenTheNameWasTakenMeanwhile_AsksForAnother()
    {
        var metadata = new Dictionary<string, string> { ["username"] = "mrabhimanyu" };
        _api.SignIn = () => Session(metadata: metadata);
        _api.GetProfile = (_, _) => null;
        _api.CreateProfile = (username, _, _) => username == "mrabhimanyu"
            ? throw new AccountException(AccountError.UsernameTaken, "taken")
            : Profile(username);

        await _service.SignInWithPasswordAsync("me@example.com", "pw123456");
        Assert.Equal(AccountStatus.NeedsProfile, _service.State.Status);

        await _service.CreateProfileAsync("mr.abhi", "Abhimanyu");
        Assert.Equal(AccountStatus.SignedIn, _service.State.Status);
        Assert.Equal("mr.abhi", _service.State.Profile!.Username);
    }

    [Fact]
    public async Task SignIn_UsesTheProviderPicture_ForANewProfile()
    {
        var metadata = new Dictionary<string, string> { ["username"] = "mrabhimanyu", ["picture"] = "https://img/me.png" };
        _api.SignIn = () => Session(metadata: metadata);
        _api.GetProfile = (_, _) => null;
        string? avatar = null;
        _api.CreateProfile = (username, _, url) => { avatar = url; return Profile(username); };

        await _service.SignInWithPasswordAsync("me@example.com", "pw123456");

        Assert.Equal("https://img/me.png", avatar);
    }

    [Fact]
    public async Task SignIn_WithWrongPassword_StaysSignedOut()
    {
        _api.SignIn = () => throw new AccountException(AccountError.InvalidCredentials, "Wrong email or password.");

        await Assert.ThrowsAsync<AccountException>(() => _service.SignInWithPasswordAsync("me@example.com", "nope"));

        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.Null(_store.Load());
    }

    // -- Tokens ----------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpiringToken_IsRefreshedBeforeTheCall()
    {
        // Started offline, so the saved token (30 s from expiry) is still the one in memory.
        SaveSession(Profile(), expires: Now.AddSeconds(30));
        _api.Refresh = _ => throw new AccountException(AccountError.Offline, "offline");
        await _service.InitializeAsync();

        string? usedToken = null;
        _api.Refresh = _ => Session("acc-10");
        _api.UpdateProfile = (token, _, _) => { usedToken = token; return Profile() with { DisplayName = "New" }; };
        await _service.UpdateProfileAsync(displayName: "New");

        Assert.Equal("acc-10", usedToken);
        Assert.False(_service.State.IsOffline);
    }

    [Fact]
    public async Task ATokenTheServerCallsExpired_IsRefreshedAndTheCallRetriedOnce()
    {
        SaveSession(Profile());
        _api.Refresh = _ => Session("acc-2");
        _api.GetProfile = (_, _) => Profile();
        await _service.InitializeAsync();

        var tokens = new List<string>();
        _api.Refresh = _ => Session("acc-3");
        _api.UpdateProfile = (token, _, _) =>
        {
            tokens.Add(token);
            if (tokens.Count == 1) throw new AccountException(AccountError.SessionExpired, "jwt expired");
            return Profile() with { DisplayName = "New" };
        };

        await _service.UpdateProfileAsync(displayName: "New");

        Assert.Equal(new[] { "acc-2", "acc-3" }, tokens);
        Assert.Equal("New", _service.State.Profile!.DisplayName);
    }

    // -- Profile ---------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateProfile_WithTheSameUsername_DoesNotCallTheServer()
    {
        SaveSession(Profile());
        _api.Refresh = _ => Session();
        _api.GetProfile = (_, _) => Profile();
        await _service.InitializeAsync();
        _api.Calls.Clear();

        await _service.UpdateProfileAsync(username: "@MrAbhimanyu");

        Assert.Empty(_api.Calls);
    }

    [Fact]
    public void UsernameLock_IsReportedFor30DaysAfterAChange()
    {
        var state = new AccountState(AccountStatus.SignedIn, "u-1", null, Profile() with { UsernameChangedAtUtc = Now.AddDays(-10) }, false);

        Assert.Equal(Now.AddDays(20), state.UsernameLockedUntilUtc(Now));
        Assert.Null(state.UsernameLockedUntilUtc(Now.AddDays(21)));
    }

    // -- Sign-out and deletion -------------------------------------------------------------------

    [Fact]
    public async Task SignOut_Offline_StillSignsOutLocally()
    {
        SaveSession(Profile());
        _api.Refresh = _ => Session();
        _api.GetProfile = (_, _) => Profile();
        await _service.InitializeAsync();
        _api.SignOut = () => throw new AccountException(AccountError.Offline, "offline");

        await _service.SignOutAsync();

        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task DeleteAccount_SignsOut_OnlyAfterTheServerConfirms()
    {
        SaveSession(Profile());
        _api.Refresh = _ => Session();
        _api.GetProfile = (_, _) => Profile();
        await _service.InitializeAsync();

        _api.Delete = () => throw new AccountException(AccountError.Offline, "offline");
        await Assert.ThrowsAsync<AccountException>(() => _service.DeleteAccountAsync());
        Assert.Equal(AccountStatus.SignedIn, _service.State.Status);

        _api.Delete = () => { };
        await _service.DeleteAccountAsync();
        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task StateChanged_FiresOnSignInAndSignOut()
    {
        var seen = new List<AccountStatus>();
        _service.StateChanged += (_, s) => seen.Add(s.Status);
        _api.SignIn = () => Session();
        _api.GetProfile = (_, _) => Profile();

        await _service.SignInWithPasswordAsync("me@example.com", "pw123456");
        await _service.SignOutAsync();

        Assert.Equal(new[] { AccountStatus.SignedIn, AccountStatus.SignedOut }, seen);
    }

    // -- Fakes -----------------------------------------------------------------------------------

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
