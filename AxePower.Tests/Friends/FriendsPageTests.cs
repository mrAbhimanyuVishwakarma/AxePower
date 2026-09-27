using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AxePower.App.ViewModels;
using AxePower.App.ViewModels.ShareScreen;
using AxePower.App.Views.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Friends;
using AxePower.Core.Services.Music;
using AxePower.Tests.Accounts;

namespace AxePower.Tests.Friends;

/// <summary>
/// The Friends page on the account (F3): lists, requests, invites and privacy, over a fake Supabase
/// and a fake live feed. Views are loaded to catch broken bindings; how they look is checked by the
/// owner, and two real accounts are needed for F4.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class FriendsPageTests : IDisposable
{
    private static readonly DateTime Now = FakeFriendsApi.Now;

    private readonly WpfApplicationFixture _wpf;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AxePowerFriendsPage", Guid.NewGuid().ToString("N"));
    private readonly FakeAccountApi _accountApi = new();
    private readonly FakeFriendsApi _api = new();
    private readonly FakeFeed _feed = new();
    private readonly AccountService _account;
    private readonly SettingsService _settings;
    private readonly FriendsService _friends;
    private readonly FriendsHubViewModel _hub;

    public FriendsPageTests(WpfApplicationFixture wpf)
    {
        _wpf = wpf;
        Directory.CreateDirectory(_dir);
        _account = new AccountService(_accountApi, new AccountSessionStore(new LogService(), Path.Combine(_dir, "account")), new Clock(), new LogService());
        _settings = new SettingsService(new LogService(), Path.Combine(_dir, "settings"));
        _friends = new FriendsService(_account, _api, () => _feed, new Clock(), new LogService(), _settings, idleTime: () => TimeSpan.Zero);
        _hub = new FriendsHubViewModel(_friends, new AccountViewModel(_account, new Clock()), new LogService()) { ConfirmBlock = _ => true };
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
        await Until(() => _api.Calls.Contains("ListFriends"), "first load");
        await _friends.ReloadAsync();
        Flush();
    }

    /// <summary>The page applies service events on its UI thread; let it catch up before looking.</summary>
    private void Flush() => _wpf.Invoke(() => { });

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    private static Friend Person(string id, string name, FriendState state, PresenceStatus status = PresenceStatus.Offline, string? game = null) =>
        new("f-" + id, id, name, name, null, state, Now,
            new FriendPresence(status, game is null ? null : ActivityKind.Game, game, Now.AddSeconds(-10)));

    private void SeedPeople()
    {
        _api.FriendsList = new[]
        {
            Person("u-2", "player_two", FriendState.Friend, PresenceStatus.Online, "Valorant"),
            Person("u-3", "sleepy", FriendState.Friend),
            Person("u-4", "new_person", FriendState.Incoming),
            Person("u-5", "waiting_on", FriendState.Outgoing),
        };
        foreach (var f in _api.FriendsList) _api.Presence[f.UserId] = f.Presence;
    }

    // -- Lists ----------------------------------------------------------------------------------

    [Fact]
    public async Task SignedIn_TheListSplitsFriendsAndRequests_AndOnlineShowsOnlyOnlineFriends()
    {
        SeedPeople();
        await SignInAsync();

        Assert.Equal(2, _hub.Friends.Count);
        Assert.Single(_hub.Incoming);
        Assert.Single(_hub.Outgoing);
        Assert.True(_hub.HasRequests);
        Assert.Equal("REQUESTS · 1 NEW", _hub.RequestsHeader);

        _hub.SetFilter("Online", string.Empty);
        var online = Assert.Single(_hub.FilteredFriends);
        Assert.Equal("Playing Valorant", online.StatusText);
        Assert.Equal("#23A55A", online.StatusColor);

        _hub.SetFilter("All", "sle");
        Assert.Equal("sleepy", Assert.Single(_hub.FilteredFriends).DisplayName);
    }

    [Fact]
    public void SignedOut_ItInvitesYouToSignIn()
    {
        Assert.True(_hub.IsSignedOut);
        Assert.False(_hub.ShowsNoFriendsHint);
        Assert.Empty(_hub.FilteredFriends);
    }

    // -- Requests -------------------------------------------------------------------------------

    [Theory]
    [InlineData(FriendRequestResult.Requested, "Request sent to @player_two.")]
    [InlineData(FriendRequestResult.Accepted, "You and @player_two are now friends.")]
    [InlineData(FriendRequestResult.AlreadyFriends, "You're already friends with @player_two.")]
    public async Task SendRequest_SaysWhatHappened(FriendRequestResult result, string message)
    {
        await SignInAsync();
        _api.RequestResult = result;
        _hub.AddUsername = "@Player_Two";

        await _hub.SendRequestCommand.ExecuteAsync(null);

        Assert.Equal(message, _hub.AddStatus);
        Assert.False(_hub.AddStatusIsError);
        Assert.Equal(string.Empty, _hub.AddUsername);
    }

    [Fact]
    public async Task SendRequest_ToNobody_ShowsTheError()
    {
        await SignInAsync();
        _api.OnSendRequest = () => throw new AccountException(AccountError.FriendNotFound, "No one has that username.");
        _hub.AddUsername = "ghost_user";

        await _hub.SendRequestCommand.ExecuteAsync(null);

        Assert.Equal("No one has that username.", _hub.AddStatus);
        Assert.True(_hub.AddStatusIsError);
    }

    [Fact]
    public async Task Accept_Decline_Remove_AndBlock_ReachSupabase()
    {
        SeedPeople();
        await SignInAsync();

        // Taken first: each action reloads, and the page rebuilds its lists on the UI thread.
        var incoming = _hub.Incoming[0];
        var outgoing = _hub.Outgoing[0];
        var friend = _hub.Friends[0];

        await _hub.AcceptCommand.ExecuteAsync(incoming);
        Flush();
        await _hub.RemoveCommand.ExecuteAsync(outgoing);
        Flush();
        await _hub.BlockCommand.ExecuteAsync(friend);

        Assert.Contains("Respond:f-u-4:True", _api.Calls);
        Assert.Contains("Remove:u-5", _api.Calls);
        Assert.Contains("Block:u-2", _api.Calls);
    }

    [Fact]
    public async Task Block_AsksFirst()
    {
        SeedPeople();
        await SignInAsync();
        _hub.ConfirmBlock = _ => false;

        var friend = _hub.Friends[0];
        await _hub.BlockCommand.ExecuteAsync(friend);

        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("Block:"));
    }

    // -- Invites --------------------------------------------------------------------------------

    [Fact]
    public async Task AnInvite_PopsUp_AndJoiningHandsTheLinkToThePage()
    {
        SeedPeople();
        await SignInAsync();
        FriendInvite? joined = null;
        _hub.JoinRequested += (_, i) => joined = i;

        _feed.Push(new RealtimeChange("friend_invites", "INSERT", new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "i-1", ["from_user"] = "u-2", ["to_user"] = FakeFriendsApi.Me, ["kind"] = "call",
            ["room_link"] = "http://192.168.1.20:8890/abc#v=s",
            ["created_at"] = Now.ToString("O"), ["expires_at"] = Now.AddMinutes(9).ToString("O")
        }, null));
        Flush();

        Assert.True(_hub.IsInvitePopupOpen);
        Assert.Equal("player_two invites you", _hub.InviteTitle);
        Assert.Equal("to a call", _hub.InviteSubtitle);

        await _hub.JoinInviteCommand.ExecuteAsync(null);

        Assert.False(_hub.IsInvitePopupOpen);
        Assert.Equal("http://192.168.1.20:8890/abc#v=s", joined!.RoomLink);
        Assert.Contains("Dismiss:i-1", _api.Calls);
    }

    [Fact]
    public async Task InvitingToWatch_WhileNotSharing_SendsNothing()
    {
        SeedPeople();
        await SignInAsync();
        _hub.GetRoomLink = _ => Task.FromResult<string?>(null); // the page says: share first

        var friend = _hub.Friends[0];
        await _hub.InviteToWatchCommand.ExecuteAsync(friend);

        Assert.DoesNotContain(_api.Calls, c => c.StartsWith("Invite:"));
    }

    [Fact]
    public async Task InvitingToACall_SendsTheRoomLink()
    {
        SeedPeople();
        await SignInAsync();
        _hub.GetRoomLink = _ => Task.FromResult<string?>("http://192.168.1.10:8890/room#v=x");

        var friend = _hub.Friends[0];
        await _hub.InviteToCallCommand.ExecuteAsync(friend);

        Assert.Contains("Invite:u-2:Call:http://192.168.1.10:8890/room#v=x", _api.Calls);
    }

    // -- Presence -------------------------------------------------------------------------------

    [Fact]
    public async Task TheCallState_BecomesYourActivity()
    {
        await SignInAsync();

        _hub.UpdateSession(inCall: true, sharing: false);
        Assert.Equal(new MyPresence(PresenceStatus.Online, ActivityKind.Call, "In a call"), await _friends.ComputePresenceAsync());

        _hub.UpdateSession(inCall: true, sharing: true);
        Assert.Equal(ActivityKind.Sharing, (await _friends.ComputePresenceAsync()).Kind);
    }

    [Fact]
    public void PrivacySwitches_GoThroughToTheService()
    {
        _hub.ShareActivity = false;
        _hub.AppearOffline = true;

        Assert.False(_friends.ShareActivity);
        Assert.True(_friends.AppearOffline);
    }

    // -- The page itself ------------------------------------------------------------------------

    [Fact]
    public void ThePage_LoadsWithTheAccountRoster_AndNoFooter() =>
        _wpf.Invoke(() =>
        {
            var settingsVm = new ShareScreenSettingsViewModel(new FriendsHubTests.FakeDeviceProvider(), new FriendsHubTests.FakeToolProvider(), _settings, new LogService(), _friends);
            var vm = new ShareScreenViewModel(new FriendsHubTests.FakeShareService(), settingsVm, new NotificationService(), new LogService(),
                friendsHub: _hub);
            var view = new ShareScreenView { DataContext = vm };
            view.Measure(new Size(1200, 800));
            view.Arrange(new Rect(0, 0, 1200, 800));
            view.UpdateLayout();

            var texts = Texts(view).ToList();
            Assert.Contains("Add friends by @username", texts);
            // The old footer ("<name> · Online") is gone: the sidebar profile already shows you.
            // Its "Online" label was the only text block with that text (the tab is button content).
            Assert.DoesNotContain("Online", texts);
            Assert.Same(_hub, vm.AccountFriends);
        });

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock block) yield return block.Text;
            foreach (var nested in Texts(child)) yield return nested;
        }
    }

    private sealed class Clock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
