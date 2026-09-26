using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AxePower.Core.Services;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Friends;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.ShareScreen;

/// <summary>One person in the Friends list, with what the row shows.</summary>
public sealed class FriendItem
{
    public FriendItem(Friend friend) => Friend = friend;

    public Friend Friend { get; }
    public string UserId => Friend.UserId;
    public string DisplayName => string.IsNullOrWhiteSpace(Friend.DisplayName) ? Friend.Username : Friend.DisplayName;
    public string Handle => "@" + Friend.Username;
    public string? AvatarUrl => Friend.AvatarUrl;
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarUrl);
    public string Initials => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    public PresenceStatus Status => Friend.Presence.Status;
    public bool IsOnline => Status != PresenceStatus.Offline;
    public bool IsSharing => IsOnline && Friend.Presence.Kind == ActivityKind.Sharing;

    public string StatusText => Status switch
    {
        PresenceStatus.Offline => "Offline",
        _ when Friend.Presence.Kind == ActivityKind.Game && Friend.Presence.Activity is { } game => "Playing " + game,
        _ when Friend.Presence.Activity is { } activity => activity,
        PresenceStatus.Away => "Away",
        _ => "Online"
    };

    /// <summary>The dot on the avatar: green online, amber away, grey offline.</summary>
    public string StatusColor => Status switch
    {
        PresenceStatus.Online => "#23A55A",
        PresenceStatus.Away => "#F0B232",
        _ => "#80848E"
    };
}

/// <summary>A person suggested while typing in "Add a friend", with the one action that fits.</summary>
public sealed partial class SuggestionItem : ObservableObject
{
    public SuggestionItem(ProfileSuggestion person)
    {
        Person = person;
        _state = person.State;
    }

    public ProfileSuggestion Person { get; }
    public string UserId => Person.UserId;
    public string Username => Person.Username;
    public string DisplayName => string.IsNullOrWhiteSpace(Person.DisplayName) ? Person.Username : Person.DisplayName;
    public string Handle => "@" + Person.Username;
    public string? AvatarUrl => Person.AvatarUrl;
    public bool HasAvatar => !string.IsNullOrEmpty(AvatarUrl);
    public string Initials => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(CanAct))]
    private SuggestionState _state;

    /// <summary>Add, Accept (they asked you), Requested, Friends, or Blocked (you blocked them).</summary>
    public string ActionText => State switch
    {
        SuggestionState.None => "Add",
        SuggestionState.Incoming => "Accept",
        SuggestionState.Outgoing => "Requested",
        SuggestionState.Friend => "Friends",
        _ => "Blocked"
    };

    public bool CanAct => State is SuggestionState.None or SuggestionState.Incoming;
}

/// <summary>
/// The account side of the Friends page: friends and requests from <see cref="FriendsService"/>,
/// adding by @username, sending and answering invites, and the privacy switches. The page keeps
/// its rooms, calls and screen sharing; this only decides who is asked to join them.
/// </summary>
public partial class FriendsHubViewModel : ObservableObject
{
    private readonly FriendsService _friends;
    private readonly NotificationService? _notifications;
    private readonly LogService _log;
    private string _filter = "Online";
    private string _search = string.Empty;

    public FriendsHubViewModel(FriendsService friends, AccountViewModel account, LogService log, NotificationService? notifications = null)
    {
        _friends = friends;
        Account = account;
        _log = log;
        _notifications = notifications;

        _friends.Changed += (_, _) => OnUi(Refresh);
        _friends.InviteReceived += (_, invite) => OnUi(() => ShowInvite(invite));
        Account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or nameof(AccountViewModel.IsSignedIn)) OnUi(Refresh);
        };
        Refresh();
    }

    public AccountViewModel Account { get; }
    public bool IsSignedIn => Account.IsSignedIn;
    public bool IsSignedOut => !Account.IsSignedIn;

    public ObservableCollection<FriendItem> Friends { get; } = new();
    public ObservableCollection<FriendItem> Incoming { get; } = new();
    public ObservableCollection<FriendItem> Outgoing { get; } = new();

    public IEnumerable<FriendItem> FilteredFriends
    {
        get
        {
            var query = Friends.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(_search))
            {
                query = query.Where(f => f.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                         || f.Handle.Contains(_search, StringComparison.OrdinalIgnoreCase));
            }

            return _filter switch
            {
                "Online" => query.Where(f => f.IsOnline),
                "Streams" => query.Where(f => f.IsSharing),
                _ => query
            };
        }
    }

    public bool HasRequests => Incoming.Count + Outgoing.Count > 0;
    public string RequestsHeader => Incoming.Count > 0 ? $"REQUESTS · {Incoming.Count} NEW" : "REQUESTS";
    public bool ShowsNoFriendsHint => IsSignedIn && Friends.Count == 0 && !HasRequests;
    public bool ShowsNoneInFilterHint => IsSignedIn && Friends.Count > 0 && !FilteredFriends.Any();
    public string NoneInFilterText => _filter == "Streams" ? "No friend is sharing a screen right now." : "No friends online right now.";

    /// <summary>Called by the page when its tab or search box changes; both apply to rooms and friends alike.</summary>
    public void SetFilter(string filter, string search)
    {
        _filter = filter;
        _search = search ?? string.Empty;
        OnPropertyChanged(nameof(FilteredFriends));
        OnPropertyChanged(nameof(ShowsNoneInFilterHint));
        OnPropertyChanged(nameof(NoneInFilterText));
    }

    private void Refresh()
    {
        var all = _friends.Friends;
        Replace(Friends, all.Where(f => f.State == FriendState.Friend).OrderByDescending(f => f.Presence.Status).ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase));
        Replace(Incoming, all.Where(f => f.State == FriendState.Incoming));
        Replace(Outgoing, all.Where(f => f.State == FriendState.Outgoing));

        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(FilteredFriends));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(RequestsHeader));
        OnPropertyChanged(nameof(ShowsNoFriendsHint));
        OnPropertyChanged(nameof(ShowsNoneInFilterHint));
        OnPropertyChanged(nameof(ShareActivity));
        OnPropertyChanged(nameof(AppearOffline));

        static void Replace(ObservableCollection<FriendItem> target, IEnumerable<Friend> items)
        {
            target.Clear();
            foreach (var item in items) target.Add(new FriendItem(item));
        }
    }

    // -- Adding friends ---------------------------------------------------------------------------

    [ObservableProperty] private string _addUsername = string.Empty;

    /// <summary>People matching what is typed (2+ characters), refreshed a moment after typing stops.</summary>
    public ObservableCollection<SuggestionItem> Suggestions { get; } = new();

    public bool HasSuggestions => Suggestions.Count > 0;

    [ObservableProperty] private bool _showsNoMatches;

    /// <summary>How long to wait after a keystroke before searching. Shortened in tests.</summary>
    public TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _searchRun;

    partial void OnAddUsernameChanged(string value)
    {
        _searchRun?.Cancel();
        var query = (value ?? string.Empty).Trim().TrimStart('@');
        if (query.Length < 2 || !IsSignedIn)
        {
            ShowSuggestions(Array.Empty<ProfileSuggestion>(), searched: false);
            return;
        }

        var cts = _searchRun = new CancellationTokenSource();
        _ = SearchAsync(query, cts.Token);
    }

    private async Task SearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDelay, ct);
            var found = await _friends.SearchAsync(query, ct);
            if (!ct.IsCancellationRequested) OnUi(() => ShowSuggestions(found, searched: true));
        }
        catch (OperationCanceledException) { }
        catch (AccountException ex)
        {
            _log.LogInfo($"[Friends] Search failed ({ex.Error}).");
        }
    }

    private void ShowSuggestions(IReadOnlyList<ProfileSuggestion> found, bool searched)
    {
        Suggestions.Clear();
        foreach (var person in found) Suggestions.Add(new SuggestionItem(person));
        ShowsNoMatches = searched && found.Count == 0;
        OnPropertyChanged(nameof(HasSuggestions));
    }

    /// <summary>The row's button: Add sends a request, Accept accepts theirs.</summary>
    [RelayCommand]
    private async Task ActOnSuggestionAsync(SuggestionItem? item)
    {
        if (item is null || !item.CanAct || IsBusy) return;
        IsBusy = true;
        try
        {
            if (item.State == SuggestionState.Incoming
                && _friends.Friends.FirstOrDefault(f => f.UserId == item.UserId && f.State == FriendState.Incoming) is { } request)
            {
                await _friends.RespondAsync(request, accept: true);
                item.State = SuggestionState.Friend;
                SetAddStatus($"You and {item.Handle} are now friends.", error: false);
            }
            else
            {
                var result = await _friends.SendRequestAsync(item.Username);
                item.State = result is FriendRequestResult.Accepted or FriendRequestResult.AlreadyFriends
                    ? SuggestionState.Friend
                    : SuggestionState.Outgoing;
                SetAddStatus(result == FriendRequestResult.Accepted
                    ? $"You and {item.Handle} are now friends."
                    : $"Request sent to {item.Handle}.", error: false);
            }
        }
        catch (AccountException ex)
        {
            SetAddStatus(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
    [ObservableProperty] private string _addStatus = string.Empty;
    [ObservableProperty] private bool _addStatusIsError;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task SendRequestAsync()
    {
        if (IsBusy) return;
        var name = UsernameRules.Normalize(AddUsername);
        if (name.Length == 0) { SetAddStatus("Type their @username.", error: true); return; }

        IsBusy = true;
        try
        {
            var result = await _friends.SendRequestAsync(name);
            SetAddStatus(result switch
            {
                FriendRequestResult.Accepted => $"You and @{name} are now friends.",
                FriendRequestResult.AlreadyFriends => $"You're already friends with @{name}.",
                FriendRequestResult.AlreadyRequested => $"You already asked @{name}.",
                _ => $"Request sent to @{name}."
            }, error: false);
            AddUsername = string.Empty;
        }
        catch (AccountException ex)
        {
            SetAddStatus(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetAddStatus(string text, bool error)
    {
        AddStatus = text;
        AddStatusIsError = error;
    }

    [RelayCommand] private Task AcceptAsync(FriendItem? item) => Act(item, f => _friends.RespondAsync(f.Friend, accept: true));
    [RelayCommand] private Task DeclineAsync(FriendItem? item) => Act(item, f => _friends.RespondAsync(f.Friend, accept: false));

    /// <summary>Unfriend, or cancel a request you sent.</summary>
    [RelayCommand] private Task RemoveAsync(FriendItem? item) => Act(item, f => _friends.RemoveAsync(f.Friend));

    [RelayCommand]
    private Task BlockAsync(FriendItem? item)
    {
        if (item is null) return Task.CompletedTask;
        var sure = ConfirmBlock?.Invoke(item) ?? true;
        return sure ? Act(item, f => _friends.BlockAsync(f.UserId), $"Blocked {item.Handle}.") : Task.CompletedTask;
    }

    /// <summary>Asks before blocking. Replaced in tests.</summary>
    public Func<FriendItem, bool>? ConfirmBlock { get; set; } = item =>
        MessageBox.Show(
            $"Block {item.DisplayName} ({item.Handle})?\n\nThis removes them from your friends and stops their requests and invites. They aren't told.",
            "Block", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private async Task Act(FriendItem? item, Func<FriendItem, Task> action, string? done = null)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;
        try
        {
            await action(item);
            if (done is not null) _notifications?.Show(done);
        }
        catch (AccountException ex)
        {
            _notifications?.Show(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -- Invites ----------------------------------------------------------------------------------

    /// <summary>
    /// Opens (or reuses) this PC's room and returns its link; null when an invite can't be sent yet
    /// (for Watch: you are not sharing). Set by the Friends page, which owns the room.
    /// </summary>
    public Func<InviteKind, Task<string?>>? GetRoomLink { get; set; }

    /// <summary>Raised when the user joins an invite: the page connects to the link.</summary>
    public event EventHandler<FriendInvite>? JoinRequested;

    [RelayCommand] private Task InviteToCallAsync(FriendItem? item) => InviteAsync(item, InviteKind.Call);
    [RelayCommand] private Task InviteToWatchAsync(FriendItem? item) => InviteAsync(item, InviteKind.Watch);

    private async Task InviteAsync(FriendItem? item, InviteKind kind)
    {
        if (item is null || GetRoomLink is null) return;
        try
        {
            var link = await GetRoomLink(kind);
            if (link is null) return;
            await _friends.InviteAsync(item.Friend, kind, link);
            _notifications?.Show(kind == InviteKind.Watch
                ? $"Invited {item.DisplayName} to watch your screen."
                : $"Invited {item.DisplayName} to your call.");
        }
        catch (AccountException ex)
        {
            _notifications?.Show(ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError("[Friends] Invite failed", ex);
            _notifications?.Show("The invite could not be sent.");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvitePopupOpen), nameof(InviteTitle), nameof(InviteSubtitle), nameof(InviteFromInitials))]
    private FriendInvite? _pendingInvite;

    public bool IsInvitePopupOpen => PendingInvite is not null;

    private FriendItem? InviteSender => PendingInvite is { } i ? Friends.FirstOrDefault(f => f.UserId == i.FromUserId) : null;
    public string InviteTitle => $"{InviteSender?.DisplayName ?? "A friend"} invites you";
    public string InviteSubtitle => PendingInvite?.Kind == InviteKind.Watch ? "to watch their screen" : "to a call";
    public string InviteFromInitials => InviteSender?.Initials ?? "?";

    private void ShowInvite(FriendInvite invite) => PendingInvite = invite;

    [RelayCommand]
    private async Task JoinInviteAsync()
    {
        if (PendingInvite is not { } invite) return;
        PendingInvite = null;
        JoinRequested?.Invoke(this, invite);
        await _friends.DismissInviteAsync(invite);
    }

    [RelayCommand]
    private async Task DeclineInviteAsync()
    {
        if (PendingInvite is not { } invite) return;
        PendingInvite = null;
        await _friends.DismissInviteAsync(invite);
    }

    /// <summary>The name to show for a friend who sent an invite (for the room's title).</summary>
    public string NameOf(string userId) => Friends.FirstOrDefault(f => f.UserId == userId)?.DisplayName ?? "Friend";

    // -- Presence and privacy ----------------------------------------------------------------------

    public bool ShareActivity
    {
        get => _friends.ShareActivity;
        set { _friends.ShareActivity = value; OnPropertyChanged(); }
    }

    public bool AppearOffline
    {
        get => _friends.AppearOffline;
        set { _friends.AppearOffline = value; OnPropertyChanged(); }
    }

    /// <summary>From the page: in a call, sharing, or neither.</summary>
    public void UpdateSession(bool inCall, bool sharing) =>
        _friends.SetSessionActivity(sharing ? ActivityKind.Sharing : inCall ? ActivityKind.Call : null);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
