using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.ViewModels;
using AxePower.App.ViewModels.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Friends;
using AxePower.Core.Services.Music;
using AxePower.Tests.Accounts;

namespace AxePower.Tests.Friends;

/// <summary>
/// Suggestions while typing in "Add a friend", and "Let people find me by name". The matching
/// rules themselves (prefix, '_' literal, hidden profiles, blocks) are checked on the database in
/// supabase/tests.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class FriendSearchTests : IDisposable
{
    private static readonly DateTime Now = FakeFriendsApi.Now;

    private readonly WpfApplicationFixture _wpf;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerFriendSearch", Guid.NewGuid().ToString("N"));
    private readonly FakeAccountApi _accountApi = new();
    private readonly FakeFriendsApi _api = new();
    private readonly AccountService _account;
    private readonly FriendsService _friends;
    private readonly FriendsHubViewModel _hub;

    public FriendSearchTests(WpfApplicationFixture wpf)
    {
        _wpf = wpf;
        Directory.CreateDirectory(_dir);
        _account = new AccountService(_accountApi, new AccountSessionStore(new LogService(), Path.Combine(_dir, "a")), new Clock(), new LogService());
        _friends = new FriendsService(_account, _api, () => new FakeFeed(), new Clock(), new LogService(),
            new SettingsService(new LogService(), Path.Combine(_dir, "s")), idleTime: () => TimeSpan.Zero);
        _hub = new FriendsHubViewModel(_friends, new AccountViewModel(_account, new Clock()), new LogService())
        {
            SearchDelay = TimeSpan.FromMilliseconds(40)
        };
        _friends.Attach();
    }

    public void Dispose()
    {
        _friends.Dispose();
        _account.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task SignInAsync()
    {
        _accountApi.SignIn = () => new AuthSession("tok", "ref", Now.AddHours(1), FakeFriendsApi.Me, "me@example.com", "google", new Dictionary<string, string>());
        _accountApi.GetProfile = (_, _) => FakeAccountApi.Profile();
        await _account.SignInWithPasswordAsync("me@example.com", "pw123456");
        await _friends.ReloadAsync();
        Flush();
    }

    private void Flush() => _wpf.Invoke(() => { });

    private async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200; i++)
        {
            Flush();
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    private static ProfileSuggestion Person(string id, string name, SuggestionState state = SuggestionState.None) =>
        new(id, name, name, null, state);

    [Fact]
    public async Task OneCharacter_SearchesNothing()
    {
        await SignInAsync();
        _hub.AddUsername = "a";
        await Task.Delay(120);

        Assert.Empty(_api.Searches);
        Assert.False(_hub.HasSuggestions);
    }

    [Fact]
    public async Task TypingFast_SearchesOnlyTheLastText_AndShowsEachPersonsAction()
    {
        await SignInAsync();
        _api.SearchResults.AddRange(new[]
        {
            Person("u-2", "abhi_v"),
            Person("u-3", "abhinav", SuggestionState.Outgoing),
            Person("u-4", "abhijit", SuggestionState.Friend),
            Person("u-5", "abhay", SuggestionState.Incoming),
            Person("u-6", "abhi_troll", SuggestionState.Blocked),
        });

        _hub.AddUsername = "ab";
        _hub.AddUsername = "abh";
        _hub.AddUsername = "@abhi";
        await Until(() => _hub.HasSuggestions, "suggestions");

        Assert.Equal(new[] { "abhi" }, _api.Searches);
        Assert.Equal(new[] { "Add", "Requested", "Friends", "Accept", "Blocked" }, _hub.Suggestions.Select(s => s.ActionText));
        Assert.Equal(new[] { true, false, false, true, false }, _hub.Suggestions.Select(s => s.CanAct));
    }

    [Fact]
    public async Task Add_SendsTheRequest_AndTheRowBecomesRequested()
    {
        await SignInAsync();
        _api.SearchResults.Add(Person("u-2", "abhi_v"));
        _hub.AddUsername = "abhi";
        await Until(() => _hub.HasSuggestions, "suggestions");

        await _hub.ActOnSuggestionCommand.ExecuteAsync(_hub.Suggestions[0]);

        Assert.Equal("abhi_v", _api.LastRequestedUsername);
        Assert.Equal("Requested", _hub.Suggestions[0].ActionText);
        Assert.Equal("Request sent to @abhi_v.", _hub.AddStatus);
    }

    [Fact]
    public async Task Accept_AcceptsTheirRequest()
    {
        _api.FriendsList = new[] { new Friend("f-5", "u-5", "abhay", "abhay", null, FriendState.Incoming, Now, FriendPresence.Offline) };
        await SignInAsync();
        _api.SearchResults.Add(Person("u-5", "abhay", SuggestionState.Incoming));
        _hub.AddUsername = "abha";
        await Until(() => _hub.HasSuggestions, "suggestions");

        await _hub.ActOnSuggestionCommand.ExecuteAsync(_hub.Suggestions[0]);

        Assert.Contains("Respond:f-5:True", _api.Calls);
        Assert.Equal("Friends", _hub.Suggestions[0].ActionText);
    }

    [Fact]
    public async Task NoMatches_SaysSo()
    {
        await SignInAsync();
        _hub.AddUsername = "zzz";
        await Until(() => _hub.ShowsNoMatches, "no-matches hint");

        Assert.False(_hub.HasSuggestions);
    }

    [Fact]
    public async Task FindableByName_UpdatesTheProfile()
    {
        await SignInAsync();

        await _friends.SetDiscoverableAsync(false);

        Assert.False(_friends.Discoverable);
        Assert.Contains("SetDiscoverable", _accountApi.Calls);
    }

    [Fact]
    public async Task TheApi_SearchesByRpc_AndReadsTheStates()
    {
        var handler = new Handler("""
            [{"user_id":"u-2","username":"abhi_v","display_name":"Abhi","avatar_url":null,"state":"none"},
             {"user_id":"u-3","username":"abhi_x","display_name":"X","avatar_url":null,"state":"blocked"}]
            """);
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://p.supabase.co"), "k"), handler);

        var found = await api.SearchProfilesAsync("tok", "abhi", CancellationToken.None);

        Assert.Equal("https://p.supabase.co/rest/v1/rpc/search_profiles", handler.Uri);
        Assert.Equal("""{"query":"abhi"}""", handler.Body);
        Assert.Equal(new[] { SuggestionState.None, SuggestionState.Blocked }, found.Select(f => f.State));
    }

    [Fact]
    public async Task TheApi_ReadsAndWritesDiscoverable()
    {
        var handler = new Handler("""[{"id":"u-1","username":"me","display_name":"Me","discoverable":false,"created_at":"2026-09-26T00:00:00Z"}]""");
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://p.supabase.co"), "k"), handler);

        var profile = await api.SetDiscoverableAsync("tok", "u-1", false, CancellationToken.None);

        Assert.False(profile.Discoverable);
        Assert.Equal("""{"discoverable":false}""", handler.Body);
        Assert.True(SupabaseAccountApi.ParseProfile(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"x","username":"y"}""")!).Discoverable); // absent = findable
    }

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class Handler(string reply) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri!.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) };
        }
    }
}
