using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services.Friends;

/// <summary>
/// Friends on the AxePower account (SHARE_SOCIAL_PLAN.md, F2): the friend list and requests, live
/// presence, and call/watch invites.
///
/// It runs only while someone is signed in, and does nothing otherwise. Live changes arrive over
/// Realtime; a full reload every five minutes, and after every reconnect, covers anything missed.
/// Presence is published every minute (friends treat a missing heartbeat as offline) and whenever
/// it changes, already filtered by the privacy switches, so nothing hidden ever leaves this PC.
///
/// Events are raised on background threads; UI subscribers must marshal.
/// </summary>
public sealed class FriendsService : IDisposable
{
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(10);
    private const int FullReloadEveryBeats = 5;
    private const int ActivityMaxLength = 64;

    private readonly AccountService _account;
    private readonly IFriendsApi _api;
    private readonly Func<IRealtimeFeed> _newFeed;
    private readonly IClock _clock;
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly Func<Task<string?>> _runningGame;
    private readonly Func<TimeSpan> _idleTime;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly object _sync = new();

    private IRealtimeFeed? _feed;
    private CancellationTokenSource? _run;
    private string? _userId;
    private ActivityKind? _sessionActivity;
    private IReadOnlyList<Friend> _friends = Array.Empty<Friend>();
    private IReadOnlyList<FriendInvite> _invites = Array.Empty<FriendInvite>();

    // Invites that arrived live while a reload was fetching; the reload's older list must not drop them.
    private readonly List<FriendInvite> _arrivedDuringReload = new();
    private bool _reloading;

    public FriendsService(
        AccountService account,
        IFriendsApi api,
        Func<IRealtimeFeed> newFeed,
        IClock clock,
        LogService log,
        SettingsService settings,
        Func<Task<string?>>? runningGame = null,
        Func<TimeSpan>? idleTime = null)
    {
        _account = account;
        _api = api;
        _newFeed = newFeed;
        _clock = clock;
        _log = log;
        _settings = settings;
        _runningGame = runningGame ?? (() => Task.FromResult<string?>(null));
        _idleTime = idleTime ?? IdleTime.SinceLastInput;
    }

    // -- State ------------------------------------------------------------------------------------

    public event EventHandler? Changed;

    /// <summary>A friend invited you to a call or to watch their screen.</summary>
    public event EventHandler<FriendInvite>? InviteReceived;

    /// <summary>True while signed in and running.</summary>
    public bool IsActive => _userId is not null;

    /// <summary>True while live updates are connected.</summary>
    public bool IsLive { get; private set; }

    /// <summary>What friends currently see of you. Null before the first publish.</summary>
    public MyPresence? MyPresence { get; private set; }

    /// <summary>Friends and requests, with presence as of now (a stale heartbeat reads as offline).</summary>
    public IReadOnlyList<Friend> Friends
    {
        get
        {
            var now = _clock.UtcNow;
            lock (_sync) return _friends.Select(f => f with { Presence = f.Presence.Effective(now) }).ToList();
        }
    }

    /// <summary>Invites for you that have not expired.</summary>
    public IReadOnlyList<FriendInvite> Invites
    {
        get
        {
            var now = _clock.UtcNow;
            lock (_sync) return _invites.Where(i => i.ExpiresAtUtc > now).ToList();
        }
    }

    // -- Privacy ----------------------------------------------------------------------------------

    public bool ShareActivity
    {
        get => _settings.GetSettings().ShareScreen.ShareActivityWithFriends;
        set => UpdatePrivacy(s => s.ShareScreen.ShareActivityWithFriends = value);
    }

    public bool AppearOffline
    {
        get => _settings.GetSettings().ShareScreen.AppearOffline;
        set => UpdatePrivacy(s => s.ShareScreen.AppearOffline = value);
    }

    private void UpdatePrivacy(Action<Models.AppSettings> change)
    {
        var settings = _settings.GetSettings();
        change(settings);
        _settings.SaveSettings(settings, notifyListeners: false);
        _ = PublishPresenceSafeAsync(force: true);
    }

    /// <summary>Whether others can find you by name; follows your profile (true until signed in).</summary>
    public bool Discoverable => _account.State.Profile?.Discoverable ?? true;

    public Task SetDiscoverableAsync(bool discoverable, CancellationToken ct = default) =>
        _account.SetDiscoverableAsync(discoverable, ct);

    /// <summary>People matching what was typed in "Add a friend". Empty below 2 characters.</summary>
    public async Task<IReadOnlyList<ProfileSuggestion>> SearchAsync(string query, CancellationToken ct = default)
    {
        var text = (query ?? string.Empty).Trim().TrimStart('@');
        if (text.Length < 2 || _userId is null) return Array.Empty<ProfileSuggestion>();
        return await CallAsync(t => _api.SearchProfilesAsync(t, text, ct), ct);
    }

    /// <summary>Set by the Friends page: in a call or sharing the screen (null when neither).</summary>
    public void SetSessionActivity(ActivityKind? kind)
    {
        if (kind is ActivityKind.Game) throw new ArgumentException("Games are detected, not set.", nameof(kind));
        if (_sessionActivity == kind) return;
        _sessionActivity = kind;
        _ = PublishPresenceSafeAsync(force: false);
    }

    // -- Lifetime ---------------------------------------------------------------------------------

    /// <summary>Follows the account from now on: starts on sign-in, stops on sign-out.</summary>
    public void Attach()
    {
        _account.StateChanged += (_, state) => OnAccountState(state);
        OnAccountState(_account.State);
    }

    private void OnAccountState(AccountState state)
    {
        if (state.Status == AccountStatus.SignedIn && state.UserId is { } id)
        {
            if (id != _userId) { Stop(); Start(id); }
        }
        else if (_userId is not null)
        {
            Stop();
        }
    }

    private void Start(string userId)
    {
        _userId = userId;
        _run = new CancellationTokenSource();
        _feed = _newFeed();
        _feed.Changed += OnLiveChange;
        _feed.ConnectionChanged += OnLiveConnection;
        _feed.Start(userId, ct => _account.GetAccessTokenAsync(ct: ct));
        _ = RunAsync(_run.Token);
        _log.LogInfo("[Friends] Started.");
    }

    private void Stop()
    {
        if (_userId is null) return;
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        if (_feed is not null)
        {
            _feed.Changed -= OnLiveChange;
            _feed.ConnectionChanged -= OnLiveConnection;
            _feed.Dispose();
            _feed = null;
        }

        _userId = null;
        IsLive = false;
        MyPresence = null;
        lock (_sync)
        {
            _friends = Array.Empty<Friend>();
            _invites = Array.Empty<FriendInvite>();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        _log.LogInfo("[Friends] Stopped.");
    }

    /// <summary>Best effort on app exit, so friends see you leave at once rather than after the heartbeat runs out.</summary>
    public async Task GoOfflineAsync(TimeSpan timeout)
    {
        if (_userId is not { } me) return;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await CallAsync(t => _api.PublishPresenceAsync(t, me, new MyPresence(PresenceStatus.Offline, null, null), cts.Token), cts.Token);
        }
        catch (Exception ex) when (ex is AccountException or OperationCanceledException)
        {
            // Friends will see the heartbeat stop instead.
        }
    }

    public void Dispose()
    {
        Stop();
        _reloadGate.Dispose();
        _publishGate.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int beat = 0;
        while (!ct.IsCancellationRequested)
        {
            if (beat % FullReloadEveryBeats == 0) await ReloadSafeAsync(ct);
            await PublishPresenceSafeAsync(force: true, ct);
            Changed?.Invoke(this, EventArgs.Empty); // presence may have gone stale
            beat++;

            try { await Task.Delay(HeartbeatEvery, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // -- Loading ----------------------------------------------------------------------------------

    /// <summary>Reloads friends, their presence and your invites.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (_userId is not { } me) return;
        await _reloadGate.WaitAsync(ct);
        lock (_sync) { _reloading = true; _arrivedDuringReload.Clear(); }
        try
        {
            var friends = await CallAsync(t => _api.ListFriendsAsync(t, ct), ct);
            var ids = friends.Where(f => f.State == FriendState.Friend).Select(f => f.UserId).ToList();
            var presence = await CallAsync(t => _api.GetPresenceAsync(t, ids, ct), ct);
            var invites = await CallAsync(t => _api.ListIncomingInvitesAsync(t, me, ct), ct);

            lock (_sync)
            {
                _friends = friends
                    .Select(f => presence.TryGetValue(f.UserId, out var p) ? f with { Presence = p } : f)
                    .ToList();
                _invites = invites.Concat(_arrivedDuringReload.Where(a => invites.All(i => i.Id != a.Id))).ToList();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lock (_sync) { _reloading = false; _arrivedDuringReload.Clear(); }
            _reloadGate.Release();
        }
    }

    private async Task ReloadSafeAsync(CancellationToken ct = default)
    {
        try { await ReloadAsync(ct); }
        catch (OperationCanceledException) { }
        catch (AccountException ex) { _log.LogInfo($"[Friends] Reload skipped ({ex.Error})."); }
    }

    private void OnLiveConnection(object? sender, bool connected)
    {
        IsLive = connected;
        Changed?.Invoke(this, EventArgs.Empty);
        if (connected) _ = ReloadSafeAsync(_run?.Token ?? CancellationToken.None); // catch up on anything missed
    }

    private void OnLiveChange(object? sender, RealtimeChange change)
    {
        switch (change.Table)
        {
            case "presence" when change.Record is { } row && change.Type is "INSERT" or "UPDATE":
                var userId = row["user_id"]?.GetValue<string>();
                var presence = SupabaseAccountApi.ParsePresence(row);
                bool known;
                lock (_sync)
                {
                    known = _friends.Any(f => f.UserId == userId);
                    if (known) _friends = _friends.Select(f => f.UserId == userId ? f with { Presence = presence } : f).ToList();
                }
                if (known) Changed?.Invoke(this, EventArgs.Empty);
                break;

            case "friendships":
                _ = ReloadSafeAsync(_run?.Token ?? CancellationToken.None);
                break;

            case "friend_invites" when change.Type == "INSERT" && change.Record is { } row:
                var invite = SupabaseAccountApi.ParseInvite(row);
                if (invite.ToUserId != _userId || invite.ExpiresAtUtc <= _clock.UtcNow) break;
                lock (_sync)
                {
                    if (_invites.Any(i => i.Id == invite.Id)) break;
                    _invites = _invites.Prepend(invite).ToList();
                    if (_reloading) _arrivedDuringReload.Add(invite);
                }
                InviteReceived?.Invoke(this, invite);
                Changed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // -- Presence ---------------------------------------------------------------------------------

    /// <summary>What to publish right now, after the privacy switches.</summary>
    public async Task<MyPresence> ComputePresenceAsync()
    {
        if (AppearOffline) return new MyPresence(PresenceStatus.Offline, null, null);

        var status = _idleTime() >= AwayAfter ? PresenceStatus.Away : PresenceStatus.Online;
        if (!ShareActivity) return new MyPresence(status, null, null);

        string? game = null;
        try { game = await _runningGame(); }
        catch (Exception ex) { _log.LogInfo($"[Friends] Game check failed ({ex.GetType().Name})."); }

        game = Clean(game);
        if (game is not null) return new MyPresence(status, ActivityKind.Game, game);
        return _sessionActivity switch
        {
            ActivityKind.Sharing => new MyPresence(status, ActivityKind.Sharing, "Sharing screen"),
            ActivityKind.Call => new MyPresence(status, ActivityKind.Call, "In a call"),
            _ => new MyPresence(status, null, null)
        };
    }

    private static string? Clean(string? name)
    {
        var text = UsernameRules.StripHidden(name).Trim();
        if (text.Length == 0) return null;
        return text.Length <= ActivityMaxLength ? text : text[..ActivityMaxLength].Trim();
    }

    private async Task PublishPresenceSafeAsync(bool force, CancellationToken ct = default)
    {
        if (_userId is not { } me) return;
        await _publishGate.WaitAsync(ct);
        try
        {
            var presence = await ComputePresenceAsync();
            if (!force && presence == MyPresence) return;
            await CallAsync(t => _api.PublishPresenceAsync(t, me, presence, ct), ct);
            MyPresence = presence;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (AccountException ex) { _log.LogInfo($"[Friends] Presence not published ({ex.Error})."); }
        finally
        {
            _publishGate.Release();
        }
    }

    // -- Actions ----------------------------------------------------------------------------------

    public async Task<FriendRequestResult> SendRequestAsync(string username, CancellationToken ct = default)
    {
        var name = UsernameRules.Normalize(username);
        if (!UsernameRules.IsValid(name))
            throw new AccountException(AccountError.FriendNotFound, "No one has that username.");
        var result = await CallAsync(t => _api.SendFriendRequestAsync(t, name, ct), ct);
        await ReloadSafeAsync(ct);
        return result;
    }

    public async Task RespondAsync(Friend request, bool accept, CancellationToken ct = default)
    {
        await CallAsync(async t => { await _api.RespondToRequestAsync(t, request.FriendshipId, accept, ct); return true; }, ct);
        await ReloadSafeAsync(ct);
    }

    /// <summary>Unfriend, or cancel/decline a request.</summary>
    public async Task RemoveAsync(Friend friend, CancellationToken ct = default)
    {
        await CallAsync(async t => { await _api.RemoveFriendAsync(t, friend.UserId, ct); return true; }, ct);
        await ReloadSafeAsync(ct);
    }

    public async Task BlockAsync(string userId, CancellationToken ct = default)
    {
        await CallAsync(async t => { await _api.BlockAsync(t, userId, ct); return true; }, ct);
        await ReloadSafeAsync(ct);
    }

    public Task UnblockAsync(string userId, CancellationToken ct = default) =>
        CallAsync(async t => { await _api.UnblockAsync(t, userId, ct); return true; }, ct);

    public Task<IReadOnlyList<BlockedUser>> ListBlockedAsync(CancellationToken ct = default) =>
        CallAsync(t => _api.ListBlockedAsync(t, ct), ct);

    public Task<FriendInvite> InviteAsync(Friend friend, InviteKind kind, string roomLink, CancellationToken ct = default)
    {
        if (friend.State != FriendState.Friend) throw new AccountException(AccountError.NotFriends, "You can only invite friends.");
        if (!Uri.TryCreate(roomLink, UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https"))
            throw new ArgumentException("An invite needs the room's http link.", nameof(roomLink));
        return CallAsync(t => _api.SendInviteAsync(t, friend.UserId, kind, roomLink, ct), ct);
    }

    /// <summary>Removes an invite (after joining or declining it).</summary>
    public async Task DismissInviteAsync(FriendInvite invite, CancellationToken ct = default)
    {
        lock (_sync) _invites = _invites.Where(i => i.Id != invite.Id).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
        try { await CallAsync(async t => { await _api.DismissInviteAsync(t, invite.Id, ct); return true; }, ct); }
        catch (AccountException ex) { _log.LogInfo($"[Friends] Invite not removed on the server ({ex.Error}); it expires anyway."); }
    }

    /// <summary>Runs a call with a valid token, refreshing once if the server says it has expired.</summary>
    private async Task<T> CallAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        if (_userId is null) throw new AccountException(AccountError.NotSignedIn, "Sign in to use Friends.");
        try
        {
            return await call(await _account.GetAccessTokenAsync(ct: ct));
        }
        catch (AccountException ex) when (ex.Error == AccountError.SessionExpired)
        {
            return await call(await _account.GetAccessTokenAsync(forceRefresh: true, ct: ct));
        }
    }

    private Task CallAsync(Func<string, Task> call, CancellationToken ct) =>
        CallAsync(async t => { await call(t); return true; }, ct);
}

/// <summary>How long since the user last touched the keyboard or mouse (for Away).</summary>
public static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static TimeSpan SinceLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }
}
