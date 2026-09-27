using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Friends;
using AxePower.Core.Services.Music;
using AxePower.Tests.Accounts;

namespace AxePower.Tests.Friends;

/// <summary>
/// Friends on the account (F2), against a fake Supabase and a fake Realtime feed. Settings live in
/// a temp folder. What only two real accounts can prove (Realtime delivery, RLS as seen from the
/// app) is left for the live test in F4.
/// </summary>
public sealed class FriendsServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
    private const string Me = "u-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerFriendsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccountApi _accountApi = new();
    private readonly FakeFriendsApi _api = new();
    private readonly FakeFeed _feed = new();
    private readonly AccountService _account;
    private readonly SettingsService _settings;
    private string? _game;
    private TimeSpan _idle = TimeSpan.Zero;
    private readonly FriendsService _friends;

    public FriendsServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _account = new AccountService(_accountApi, new AccountSessionStore(new LogService(), Path.Combine(_dir, "account")), new Clock(), new LogService());
        _settings = new SettingsService(new LogService(), Path.Combine(_dir, "settings"));
        _friends = new FriendsService(_account, _api, () => _feed, new Clock(), new LogService(), _settings,
            runningGame: () => Task.FromResult(_game), idleTime: () => _idle);
    }

    public void Dispose()
    {
        _friends.Dispose();
        _account.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task SignInAsync()
    {
        _accountApi.SignIn = () => new AuthSession("tok", "ref", Now.AddHours(1), Me, "me@example.com", "google", new Dictionary<string, string>());
        _accountApi.GetProfile = (_, _) => FakeAccountApi.Profile();
        await _account.SignInWithPasswordAsync("me@example.com", "pw123456");
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    private static Friend Friend(string id, string username, FriendState state = FriendState.Friend) =>
        new("f-" + id, id, username, username, null, state, Now, FriendPresence.Offline);

    // -- Lifetime ---------------------------------------------------------------------------------

    [Fact]
    public void SignedOut_ItDoesNothing()
    {
        _friends.Attach();

        Assert.False(_friends.IsActive);
        Assert.Empty(_api.Calls);
        Assert.False(_feed.Started);
    }

    [Fact]
    public async Task SigningIn_StartsIt_LoadsFriends_AndPublishesPresence()
    {
        _api.FriendsList = new[] { Friend("u-2", "player_two"), Friend("u-3", "someone", FriendState.Incoming) };
        _api.Presence["u-2"] = new FriendPresence(PresenceStatus.Online, ActivityKind.Game, "Valorant", Now.AddSeconds(-20));
        _friends.Attach();

        await SignInAsync();
        await Until(() => _api.Published.Count > 0 && _friends.Friends.Count == 2, "load and publish");

        Assert.True(_friends.IsActive);
        Assert.Equal(Me, _feed.UserId);
        Assert.Equal("Valorant", _friends.Friends.Single(f => f.Username == "player_two").Presence.Activity);
        Assert.Equal(new MyPresence(PresenceStatus.Online, null, null), _api.Published.Last());
        Assert.Equal(new[] { "u-2" }, _api.PresenceAskedFor); // presence only for accepted friends
    }

    [Fact]
    public async Task SigningOut_StopsIt_AndForgetsEverything()
    {
        _api.FriendsList = new[] { Friend("u-2", "player_two") };
        _friends.Attach();
        await SignInAsync();
        await Until(() => _friends.Friends.Count == 1, "load");

        await _account.SignOutAsync();

        Assert.False(_friends.IsActive);
        Assert.Empty(_friends.Friends);
        Assert.True(_feed.Disposed);
    }

    // -- Presence and privacy ----------------------------------------------------------------------

    [Fact]
    public async Task Presence_ShowsTheGame_WithHiddenCharactersRemoved_AndCutTo64()
    {
        _game = "Val‮orant " + new string('x', 80);
        await SignInAsync();
        _friends.Attach();

        var presence = await _friends.ComputePresenceAsync();

        Assert.Equal(ActivityKind.Game, presence.Kind);
        Assert.StartsWith("Valorant", presence.Activity);
        Assert.Equal(64, presence.Activity!.Length);
    }

    [Fact]
    public async Task Presence_ShowsCallOrSharing_WhenNoGame()
    {
        await SignInAsync();
        _friends.SetSessionActivity(ActivityKind.Sharing);

        Assert.Equal(new MyPresence(PresenceStatus.Online, ActivityKind.Sharing, "Sharing screen"), await _friends.ComputePresenceAsync());
    }

    [Fact]
    public async Task Presence_IsAway_AfterTenIdleMinutes()
    {
        _idle = TimeSpan.FromMinutes(11);
        Assert.Equal(PresenceStatus.Away, (await _friends.ComputePresenceAsync()).Status);
    }

    [Fact]
    public async Task Privacy_HideActivity_KeepsTheStatusOnly()
    {
        _game = "Valorant";
        _friends.ShareActivity = false;

        Assert.Equal(new MyPresence(PresenceStatus.Online, null, null), await _friends.ComputePresenceAsync());
        var reread = new SettingsService(new LogService(), Path.Combine(_dir, "settings"));
        reread.LoadSettings();
        Assert.False(reread.GetSettings().ShareScreen.ShareActivityWithFriends); // saved, not just in memory
    }

    [Fact]
    public async Task Privacy_AppearOffline_PublishesOffline_WithNothingElse()
    {
        _game = "Valorant";
        _friends.Attach();
        await SignInAsync();
        await Until(() => _api.Published.Count > 0, "first publish");

        _friends.AppearOffline = true;
        await Until(() => _api.Published.Last().Status == PresenceStatus.Offline, "offline publish");

        Assert.Equal(new MyPresence(PresenceStatus.Offline, null, null), _api.Published.Last());
    }

    [Fact]
    public void AFriendWhoseHeartbeatStopped_ReadsAsOffline()
    {
        var old = new FriendPresence(PresenceStatus.Online, ActivityKind.Game, "Valorant", Now.AddMinutes(-3));
        var fresh = old with { UpdatedAtUtc = Now.AddSeconds(-30) };

        Assert.Equal(PresenceStatus.Offline, old.Effective(Now).Status);
        Assert.Null(old.Effective(Now).Activity);
        Assert.Equal(PresenceStatus.Online, fresh.Effective(Now).Status);
    }

    // -- Live changes -------------------------------------------------------------------------------

    [Fact]
    public async Task LivePresence_UpdatesAFriend_AndIgnoresStrangers()
    {
        _api.FriendsList = new[] { Friend("u-2", "player_two") };
        _friends.Attach();
        await SignInAsync();
        await Until(() => _friends.Friends.Count == 1, "load");

        _feed.Push(new RealtimeChange("presence", "UPDATE", Row("u-2", "online", "Apex"), null));
        _feed.Push(new RealtimeChange("presence", "UPDATE", Row("u-9", "online", "Stranger things"), null));

        var friend = Assert.Single(_friends.Friends);
        Assert.Equal("Apex", friend.Presence.Activity);

        static JsonObject Row(string id, string status, string activity) => new()
        {
            ["user_id"] = id, ["status"] = status, ["activity_kind"] = "game", ["activity"] = activity,
            ["updated_at"] = Now.AddSeconds(-5).ToString("O")
        };
    }

    [Fact]
    public async Task LiveInvite_RaisesInviteReceived_Once_AndSkipsExpiredOnes()
    {
        _friends.Attach();
        await SignInAsync();
        var received = new List<FriendInvite>();
        _friends.InviteReceived += (_, i) => received.Add(i);

        var live = Invite("i-1", Now.AddMinutes(9));
        _feed.Push(new RealtimeChange("friend_invites", "INSERT", live, null));
        _feed.Push(new RealtimeChange("friend_invites", "INSERT", live, null));
        _feed.Push(new RealtimeChange("friend_invites", "INSERT", Invite("i-2", Now.AddMinutes(-1)), null));

        var invite = Assert.Single(received);
        Assert.Equal(InviteKind.Watch, invite.Kind);
        Assert.Equal("http://100.64.0.2:8890/room#v=s", invite.RoomLink);
        Assert.Single(_friends.Invites);

        static JsonObject Invite(string id, DateTime expires) => new()
        {
            ["id"] = id, ["from_user"] = "u-2", ["to_user"] = Me, ["kind"] = "watch",
            ["room_link"] = "http://100.64.0.2:8890/room#v=s",
            ["created_at"] = Now.ToString("O"), ["expires_at"] = expires.ToString("O")
        };
    }

    [Fact]
    public async Task AFriendshipChange_TriggersAReload()
    {
        _friends.Attach();
        await SignInAsync();
        await Until(() => _api.Calls.Count(c => c == "ListFriends") == 1, "first load");

        _api.FriendsList = new[] { Friend("u-2", "player_two", FriendState.Incoming) };
        _feed.Push(new RealtimeChange("friendships", "INSERT", new JsonObject(), null));

        await Until(() => _friends.Friends.Count == 1, "reload after the change");
        Assert.Equal(FriendState.Incoming, _friends.Friends[0].State);
    }

    // -- Actions ------------------------------------------------------------------------------------

    [Fact]
    public async Task SendRequest_NormalisesTheName_AndReloads()
    {
        _friends.Attach();
        await SignInAsync();

        var result = await _friends.SendRequestAsync("  @Player_Two ");

        Assert.Equal(FriendRequestResult.Requested, result);
        Assert.Equal("player_two", _api.LastRequestedUsername);
    }

    [Fact]
    public async Task ACallRejectedAsExpired_IsRetriedOnceWithAFreshToken()
    {
        _friends.Attach();
        await SignInAsync();
        int attempts = 0;
        _api.OnSendRequest = () => { if (++attempts == 1) throw new AccountException(AccountError.SessionExpired, "expired"); };
        _accountApi.Refresh = _ => new AuthSession("tok-2", "ref-2", Now.AddHours(1), Me, "me@example.com", "google", new Dictionary<string, string>());

        await _friends.SendRequestAsync("player_two");

        Assert.Equal(2, attempts);
        Assert.Contains("Refresh", _accountApi.Calls);
    }

    [Fact]
    public async Task Invites_OnlyGoToFriends_WithAnHttpLink()
    {
        _friends.Attach();
        await SignInAsync();

        await Assert.ThrowsAsync<AccountException>(() => _friends.InviteAsync(Friend("u-3", "x", FriendState.Outgoing), InviteKind.Call, "http://a/b"));
        await Assert.ThrowsAsync<ArgumentException>(() => _friends.InviteAsync(Friend("u-2", "p"), InviteKind.Call, "javascript:alert(1)"));
    }

    // -- Fakes --------------------------------------------------------------------------------------

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Now;
    }
}

/// <summary>The Friends wire formats: Realtime frames and the REST calls.</summary>
public class FriendsWireTests
{
    [Fact]
    public void RealtimeJoin_ListensToTheThreeTables_WithInvitesFilteredToMe()
    {
        var join = JsonNode.Parse(RealtimeProtocol.Join("u-1", "jwt", 7))!.AsObject();

        Assert.Equal("realtime:axepower-friends", join["topic"]!.GetValue<string>());
        Assert.Equal("phx_join", join["event"]!.GetValue<string>());
        Assert.Equal("7", join["ref"]!.GetValue<string>());
        Assert.Equal("jwt", join["payload"]!["access_token"]!.GetValue<string>());
        var changes = join["payload"]!["config"]!["postgres_changes"]!.AsArray();
        Assert.Equal(new[] { "presence", "friendships", "friend_invites" }, changes.Select(c => c!["table"]!.GetValue<string>()));
        Assert.Equal("to_user=eq.u-1", changes[2]!["filter"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"topic":"realtime:axepower-friends","event":"phx_reply","payload":{"status":"ok","response":{}},"ref":"1"}""", RealtimeProtocol.Kind.Joined)]
    [InlineData("""{"topic":"realtime:axepower-friends","event":"phx_reply","payload":{"status":"error","response":{"reason":"bad token"}},"ref":"1"}""", RealtimeProtocol.Kind.JoinFailed)]
    [InlineData("""{"topic":"realtime:axepower-friends","event":"system","payload":{"status":"error","message":"invalid column for filter to_user"}}""", RealtimeProtocol.Kind.SubscriptionRefused)] // seen live, as a signed-out caller
    [InlineData("""{"topic":"realtime:axepower-friends","event":"phx_close","payload":{}}""", RealtimeProtocol.Kind.Closed)]
    [InlineData("""{"topic":"phoenix","event":"phx_reply","payload":{"status":"ok"},"ref":"2"}""", RealtimeProtocol.Kind.Other)]
    [InlineData("not json", RealtimeProtocol.Kind.Other)]
    public void RealtimeFrames_AreClassified(string frame, RealtimeProtocol.Kind kind) =>
        Assert.Equal(kind, RealtimeProtocol.Parse(frame).Kind);

    [Fact]
    public void RealtimeChange_CarriesTableTypeAndRow()
    {
        var (kind, change, _) = RealtimeProtocol.Parse("""
            {"topic":"realtime:axepower-friends","event":"postgres_changes","payload":{"ids":[1],"data":{
              "schema":"public","table":"presence","type":"UPDATE","commit_timestamp":"2026-09-26T12:00:00Z",
              "record":{"user_id":"u-2","status":"online","activity_kind":"game","activity":"Apex","updated_at":"2026-09-26T12:00:00Z"},
              "old_record":{"user_id":"u-2"}}},"ref":null}
            """);

        Assert.Equal(RealtimeProtocol.Kind.Change, kind);
        Assert.Equal("presence", change!.Table);
        Assert.Equal("UPDATE", change.Type);
        Assert.Equal("Apex", change.Record!["activity"]!.GetValue<string>());
    }

    [Fact]
    public void Heartbeat_AndTokenRefresh_HaveTheRightShape()
    {
        Assert.Equal("""{"topic":"phoenix","event":"heartbeat","payload":{},"ref":"3"}""", RealtimeProtocol.Heartbeat(3));
        Assert.Contains("\"event\":\"access_token\"", RealtimeProtocol.AccessToken("new", 4));
    }

    [Fact]
    public async Task PublishPresence_UpsertsMyRow()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://p.supabase.co"), "k"), handler);

        await api.PublishPresenceAsync("tok", "u-1", new MyPresence(PresenceStatus.Online, ActivityKind.Game, "Apex"), CancellationToken.None);

        Assert.Equal("https://p.supabase.co/rest/v1/presence?on_conflict=user_id", handler.Uri);
        Assert.Equal("resolution=merge-duplicates", handler.Prefer);
        Assert.Equal("""{"user_id":"u-1","status":"online","activity_kind":"game","activity":"Apex"}""", handler.Body);
    }

    [Theory]
    [InlineData("\"REQUESTED\"", FriendRequestResult.Requested)]
    [InlineData("\"ACCEPTED\"", FriendRequestResult.Accepted)]
    [InlineData("\"ALREADY_FRIENDS\"", FriendRequestResult.AlreadyFriends)]
    [InlineData("\"ALREADY_REQUESTED\"", FriendRequestResult.AlreadyRequested)]
    public async Task SendRequest_ReadsTheResult(string reply, FriendRequestResult expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply) });
        using var api = new SupabaseAccountApi(new SupabaseProject(new Uri("https://p.supabase.co"), "k"), handler);

        Assert.Equal(expected, await api.SendFriendRequestAsync("tok", "player_two", CancellationToken.None));
        Assert.Equal("""{"target_username":"player_two"}""", handler.Body);
    }

    [Theory]
    [InlineData("""{"code":"P0001","message":"USER_NOT_FOUND"}""", AccountError.FriendNotFound)]
    [InlineData("""{"code":"P0001","message":"CANNOT_ADD_SELF"}""", AccountError.CannotAddSelf)]
    [InlineData("""{"code":"P0001","message":"YOU_BLOCKED_THEM"}""", AccountError.YouBlockedThem)]
    [InlineData("""{"code":"P0001","message":"TOO_MANY_INVITES"}""", AccountError.RateLimited)]
    [InlineData("""{"code":"42501","message":"new row violates row-level security policy for table \"friend_invites\""}""", AccountError.NotFriends)]
    public void FriendsErrors_Map(string body, AccountError expected) =>
        Assert.Equal(expected, SupabaseAccountApi.MapError(HttpStatusCode.BadRequest, body).Error);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Prefer { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri!.ToString();
            Prefer = request.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }
}
