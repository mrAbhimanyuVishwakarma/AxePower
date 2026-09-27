using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Accounts;

/// <summary>
/// One-click sign-in (Google, GitHub, Discord, Steam) up to the point where a real browser and a
/// real provider would take over. The browser round trip itself is only proven by signing in live.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ProviderSignInTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerAccountTests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccountApi _api = new();
    private readonly FakeLoopback _loopback = new();
    private readonly AccountService _service;

    public ProviderSignInTests()
    {
        _service = new AccountService(_api, new AccountSessionStore(new LogService(), _dir), new SystemClock(), new LogService(), () => _loopback);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AuthSession Session(Dictionary<string, string> metadata, string provider = "google") =>
        new("acc", "ref", DateTime.UtcNow.AddHours(1), "u-1", "me@gmail.com", provider, metadata);

    // -- PKCE ----------------------------------------------------------------------------------------

    [Fact]
    public void Pkce_ChallengeIsTheBase64UrlSha256OfTheVerifier()
    {
        // Expected value computed independently with Python's hashlib.
        Assert.Equal("ZkOPH8cU0cTC3M30mdONAkIr-gVbpfePAqsrQSXUS8Q", Pkce.Challenge("dBjftJeZ4CVP-mJ92K9HUwMfgSK2yq5xQ4gJ3Q-HN8M"));
    }

    [Fact]
    public void Pkce_VerifiersAreRandomAndUrlSafe()
    {
        var a = Pkce.CreateVerifier();
        var b = Pkce.CreateVerifier();
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", Pkce.Challenge(a)); // the shape steam-login accepts
    }

    // -- URLs ----------------------------------------------------------------------------------------

    [Fact]
    public void SignInUrl_ForGoogle_UsesSupabasePkce()
    {
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://p.supabase.co"), "k"));
        var url = api.BuildSignInUrl(AccountProvider.Google, new Uri("http://127.0.0.1:53682/auth/callback"), "CHAL").ToString();

        Assert.StartsWith("https://p.supabase.co/auth/v1/authorize?provider=google&", url);
        Assert.Contains("redirect_to=http%3A%2F%2F127.0.0.1%3A53682%2Fauth%2Fcallback", url);
        Assert.Contains("code_challenge=CHAL&code_challenge_method=s256", url);
    }

    [Fact]
    public void SignInUrl_ForSteam_GoesThroughOurFunction()
    {
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://p.supabase.co"), "k"));
        var url = api.BuildSignInUrl(AccountProvider.Steam, new Uri("http://127.0.0.1:53682/auth/callback"), "CHAL").ToString();

        Assert.StartsWith("https://p.supabase.co/functions/v1/steam-login?redirect=", url);
        Assert.EndsWith("&challenge=CHAL", url);
    }

    // -- The service ---------------------------------------------------------------------------------

    [Fact]
    public async Task SignIn_OpensTheBrowser_WaitsForTheCode_AndExchangesItWithTheVerifier()
    {
        Uri? opened = null;
        string? usedVerifier = null;
        _loopback.Reply = new() { ["code"] = "the-code" };
        _api.Exchange = (provider, code, verifier) =>
        {
            Assert.Equal(AccountProvider.GitHub, provider);
            Assert.Equal("the-code", code);
            usedVerifier = verifier;
            return Session(new() { ["user_name"] = "MrAbhimanyu", ["name"] = "Abhimanyu V", ["avatar_url"] = "https://avatars/me.png" }, "github");
        };
        _api.GetProfile = (_, _) => null;

        await _service.SignInWithProviderAsync(AccountProvider.GitHub, url => opened = url);

        Assert.NotNull(opened);
        Assert.Contains(Pkce.Challenge(usedVerifier!), opened!.ToString()); // challenge out, verifier kept
        Assert.Equal(AccountStatus.NeedsProfile, _service.State.Status);
        Assert.Equal("github", _service.State.Provider);
        Assert.Equal("mrabhimanyu", _service.SuggestedUsername);
        Assert.Equal("Abhimanyu V", _service.SuggestedDisplayName);
        Assert.True(_loopback.Disposed);
    }

    [Fact]
    public async Task SignIn_WhenTheProviderSaysNo_ReportsWhy_AndStaysSignedOut()
    {
        _loopback.Reply = new() { ["error"] = "access_denied", ["error_description"] = "The user denied access" };

        var ex = await Assert.ThrowsAsync<AccountException>(() => _service.SignInWithProviderAsync(AccountProvider.Discord, _ => { }));

        Assert.Contains("The user denied access", ex.Message);
        Assert.Equal(AccountStatus.SignedOut, _service.State.Status);
        Assert.DoesNotContain("Exchange", _api.Calls);
    }

    [Fact]
    public async Task SignIn_Cancelled_StopsWaiting()
    {
        _loopback.WaitForever = true;
        using var cts = new CancellationTokenSource();
        var signIn = _service.SignInWithProviderAsync(AccountProvider.Steam, _ => cts.Cancel(), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
        Assert.True(_loopback.Disposed);
    }

    [Fact]
    public async Task TheDialog_AfterAOneClickSignIn_AsksForAUsername_PrefilledFromTheProvider()
    {
        _loopback.Reply = new() { ["code"] = "c" };
        _api.Exchange = (_, _, _) => Session(new() { ["full_name"] = "Abhimanyu V", ["name"] = "abhi.v" });
        _api.GetProfile = (_, _) => null;
        var vm = new AccountViewModel(_service, new SystemClock()) { OpenBrowser = _ => { } };

        vm.OpenSignIn();
        await vm.SignInWithCommand.ExecuteAsync("Google");

        Assert.Equal(AccountDialogMode.ChooseUsername, vm.Mode);
        Assert.Equal("abhi.v", vm.Username);
        Assert.Equal("Abhimanyu V", vm.NewDisplayName);
        Assert.False(vm.IsWaitingForBrowser);
        Assert.Equal("Signed in with Google", vm.SignedInWith);
    }

    [Fact]
    public async Task ASteamAccount_HidesItsPlaceholderEmail()
    {
        _loopback.Reply = new() { ["code"] = "c" };
        _api.Exchange = (_, _, _) => new AuthSession("acc", "ref", DateTime.UtcNow.AddHours(1), "u-1",
            "steam-76561198000000000@steam.axepower.invalid", "steam", new Dictionary<string, string>());
        _api.GetProfile = (_, _) => FakeAccountApi.Profile();
        var vm = new AccountViewModel(_service, new SystemClock());

        await _service.SignInWithProviderAsync(AccountProvider.Steam, _ => { });

        Assert.True(vm.IsSteamAccount);
        Assert.False(vm.HasEmail);
        Assert.Equal(string.Empty, vm.AccountEmail);
        Assert.Equal("Signed in with Steam", vm.SignedInWith);
    }

    // -- The real loopback, on its real port ---------------------------------------------------------

    [Fact]
    public async Task Loopback_IgnoresOtherRequests_AndReturnsTheCallbackQuery()
    {
        using var loopback = OAuthLoopback.Start();
        var wait = loopback.WaitForCallbackAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        using var http = new HttpClient();
        var miss = await http.GetAsync("http://127.0.0.1:53682/favicon.ico");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, miss.StatusCode);

        var page = await http.GetStringAsync("http://127.0.0.1:53682/auth/callback?code=abc%2B1&state=x+y");
        Assert.Contains("You're signed in", page);

        var query = await wait;
        Assert.Equal("abc+1", query["code"]);
        Assert.Equal("x y", query["state"]);
    }

    [Fact]
    public void Loopback_ListensOnLocalhostOnly()
    {
        using var loopback = OAuthLoopback.Start();
        Assert.Equal("http://127.0.0.1:53682/auth/callback", loopback.RedirectUri.ToString());

        // A second sign-in while one is waiting cannot take the port: it gets a clear message.
        var ex = Assert.Throws<AccountException>(() => OAuthLoopback.Start());
        Assert.Contains("53682", ex.Message);
    }

    // -- Username suggestions ------------------------------------------------------------------------

    [Theory]
    [InlineData("Abhimanyu Vishwakarma", "abhimanyu_vishwakarm")] // cut to 20
    [InlineData("MrAbhimanyu", "mrabhimanyu")]
    [InlineData("..x..y..", "x.y")]
    [InlineData("Ab", "")]            // too short to be valid
    [InlineData("日本語", "")]         // nothing usable left
    [InlineData("a-very-long-name-that-goes-on-and-on", "a_very_long_name_tha")]
    public void Suggest_MakesAValidUsernameOrNothing(string name, string expected) =>
        Assert.Equal(expected, UsernameRules.Suggest(name));

    private sealed class FakeLoopback : IOAuthLoopback
    {
        public Dictionary<string, string> Reply { get; set; } = new();
        public bool WaitForever { get; set; }
        public bool Disposed { get; private set; }

        public Uri RedirectUri { get; } = new("http://127.0.0.1:53682/auth/callback");

        public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
        {
            if (WaitForever) await Task.Delay(Timeout.Infinite, ct);
            return Reply;
        }

        public void Dispose() => Disposed = true;
    }
}
