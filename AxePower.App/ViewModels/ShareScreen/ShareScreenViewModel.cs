using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AxePower.Core.Models.Friends;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AxePower.App.ViewModels.ShareScreen;

/// <summary>
/// The Friends hub & stage: friends management, active call/stage, chat, and low-latency screen sharing.
/// </summary>
public partial class ShareScreenViewModel : ObservableObject, IDisposable
{
    private readonly IShareScreenService _shareService;
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;

    // The work behind the page lives in Core; this view model turns it into UI state and commands.
    private readonly ShareRoomSession _session;          // a joined friend's room: sockets, keys, replay windows
    private readonly ShareRoomConversation _conversation; // chat history, files, incoming wire messages
    private readonly ShareVoiceCoordinator _voice;        // audio devices and voice packets

    private CancellationTokenSource? _startCts;
    private bool _joinForScreen = true;

    /// <summary>The shared preferences instance, bound directly for the summary line.</summary>
    public ShareScreenSettingsViewModel Settings { get; }

    /// <summary>Set by the shell so the gear can open the Settings page.</summary>
    public Action<string>? NavigateTo { get; set; }

    // -- Session state -------------------------------------------------------

    [ObservableProperty]
    private ShareSessionState _state = ShareSessionState.Idle;

    [ObservableProperty]
    private string _statusMessage = "Ready to share";

    [ObservableProperty]
    private string _viewerUrl = string.Empty;

    [ObservableProperty]
    private string _roomCode = string.Empty;

    [ObservableProperty]
    private ShareTelemetry _telemetry = ShareTelemetry.Empty;

    [ObservableProperty]
    private string _encoderName = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Plays the outgoing stream back on the stage. On by default.</summary>
    [ObservableProperty]
    private bool _isPreviewEnabled = true;

    // -- Room and call state -------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChannelPlaceholderText))]
    private string _channelName = "DEV";

    public string ChannelPlaceholderText => $"Message {ChannelName}";

    [ObservableProperty]
    private bool _isMicMuted;

    [ObservableProperty]
    private bool _isFriendJoined;

    [ObservableProperty]
    private string _friendName = "Friend";

    [ObservableProperty]
    private bool _isJoinModalOpen;

    [ObservableProperty]
    private string _joinAddressInput = string.Empty;

    [ObservableProperty]
    private string _joinStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isWatchingFriend;

    [ObservableProperty]
    private string _activeFriendWatchUrl = string.Empty;

    // -- Friends list --------------------------------------------------------

    [ObservableProperty]
    private string _friendNameInput = string.Empty;

    [ObservableProperty]
    private bool _rememberFriend = true;

    public ObservableCollection<SavedFriend> SavedFriends => Settings.SavedFriends;

    [ObservableProperty]
    private FriendsProfile _userProfile = new();

    [ObservableProperty]
    private string _friendsFilterTab = "Online";

    [ObservableProperty]
    private string _friendSearchText = string.Empty;

    [ObservableProperty]
    private bool _isFriendsPanelOpen = true;

    [ObservableProperty]
    private bool _isDeafened;

    [ObservableProperty]
    private bool _isVoiceConnected;

    public int OnlineFriendsCount => SavedFriends.Count(f => f.Status != FriendStatus.Offline);
    public int AllFriendsCount => SavedFriends.Count;
    public int StreamingFriendsCount => SavedFriends.Count(f => f.IsStreaming);

    public IEnumerable<SavedFriend> FilteredFriends
    {
        get
        {
            var query = SavedFriends.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(FriendSearchText))
            {
                query = query.Where(f => (f.Name != null && f.Name.Contains(FriendSearchText, StringComparison.OrdinalIgnoreCase)) ||
                                         (f.Address != null && f.Address.Contains(FriendSearchText, StringComparison.OrdinalIgnoreCase)));
            }

            return FriendsFilterTab switch
            {
                "Online" => query.Where(f => f.Status != FriendStatus.Offline),
                "Streams" => query.Where(f => f.IsStreaming),
                _ => query
            };
        }
    }

    partial void OnFriendsFilterTabChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredFriends));
        AccountFriends?.SetFilter(value, FriendSearchText);
    }

    partial void OnFriendSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredFriends));
        AccountFriends?.SetFilter(FriendsFilterTab, value);
    }

    /// <summary>Friends on the AxePower account (null in tests that build the page without one).</summary>
    public FriendsHubViewModel? AccountFriends { get; }

    public bool HasSavedRooms => SavedFriends.Count > 0;

    partial void OnIsDeafenedChanged(bool value)
    {
        _voice.SetDeafened(value);
        OnPropertyChanged(nameof(AudioGlyph));
        OnPropertyChanged(nameof(AudioTooltip));
    }

    // -- In-call Chat --------------------------------------------------------

    [ObservableProperty]
    private bool _isChatOpen;

    [ObservableProperty]
    private int _unreadChatCount;

    public bool HasUnreadChat => UnreadChatCount > 0;

    partial void OnUnreadChatCountChanged(int value) => OnPropertyChanged(nameof(HasUnreadChat));

    partial void OnFriendNameChanged(string value) => OnPropertyChanged(nameof(CallTitle));

    partial void OnIsFriendJoinedChanged(bool value)
    {
        OnPropertyChanged(nameof(CallTitle));
        OnPropertyChanged(nameof(IsInCall));
        OnPropertyChanged(nameof(InviteButtonText));
        OnPropertyChanged(nameof(ParticipantCountText));
        OnPropertyChanged(nameof(PrivacyStatusText));
        OnPropertyChanged(nameof(PrivacyStatusColor));
    }

    partial void OnActiveFriendWatchUrlChanged(string value)
    {
        OnPropertyChanged(nameof(IsSecureTransport));
        OnPropertyChanged(nameof(PrivacyStatusText));
    }

    [ObservableProperty]
    private string _chatInputText = string.Empty;

    [ObservableProperty]
    private string _conversationPanelTab = "Chat";

    public ObservableCollection<ShareChatMessage> Messages { get; } = new();
    public ObservableCollection<ShareChatMessage> SharedFiles { get; } = new();

    public string CallTitle => IsFriendJoined
        ? $"{TileName} + {FriendName}"
        : IsInCall ? "Private call" : "Friends";

    public string InviteButtonText => IsInCall ? "Copy invite" : "Start call";

    public string PrivacyStatusColor => IsInCall ? "#19C875" : "#7F8A9B";

    public string PrivacyStatusText => IsSecureTransport
        ? "Private - encrypted chat and files"
        : IsFriendJoined ? "Private room - use a trusted LAN or VPN"
        : IsInCall ? "Private room open" : "No active call";

    public bool IsSecureTransport => !string.IsNullOrWhiteSpace(_roomSessionSecret);

    private string _roomSessionSecret = string.Empty;

    // -- Untrusted networks and who is in the room ---------------------------

    /// <summary>Set when the last start was refused because the network is Public or unclassified.</summary>
    [ObservableProperty]
    private bool _isUntrustedNetworkRefused;

    [ObservableProperty]
    private string _untrustedNetworkWarning = string.Empty;

    private enum UntrustedRetry { None, Room, Share }
    private UntrustedRetry _untrustedRetry;
    private bool _allowUntrustedNetworkOnce;

    /// <summary>Everyone connected to this PC's room: watching, listening, chatting or in control.</summary>
    public ObservableCollection<ShareParticipant> Participants { get; } = new();
    public bool HasParticipants => Participants.Count > 0;
    public string ParticipantsSummary => Participants.Count switch
    {
        0 => "Nobody is connected",
        1 => "1 connection",
        var n => $"{n} connections"
    };

    /// <summary>Session settings for one start, carrying the one-session network override if the user gave it.</summary>
    private ShareScreenSettings SessionSettings()
    {
        var settings = Settings.BuildSessionSettings();
        settings.AllowUntrustedNetwork = _allowUntrustedNetworkOnce;
        return settings;
    }

    /// <summary>
    /// Repeats the start that was refused, this time allowed on the current network. The
    /// permission covers this session only; the next room asks again.
    /// </summary>
    [RelayCommand]
    private async Task ShareAnywayOnThisNetworkAsync()
    {
        var retry = _untrustedRetry;
        _allowUntrustedNetworkOnce = true;
        try
        {
            if (retry == UntrustedRetry.Share) await ToggleSharingAsync();
            else await StartOrCopyInviteAsync();
        }
        finally
        {
            _allowUntrustedNetworkOnce = false;
        }
    }

    // -- Control approval ----------------------------------------------------

    /// <summary>A viewer with the right code asking to take control, until the host answers or it lapses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingControlApproval))]
    [NotifyPropertyChangedFor(nameof(PendingControlApprovalText))]
    private PendingControlApproval? _pendingControlApproval;

    public bool HasPendingControlApproval => PendingControlApproval != null;

    public string PendingControlApprovalText => PendingControlApproval is { } p
        ? $"A viewer at {p.Request.Endpoint} entered your control code and wants to move your mouse and type on this PC."
        : string.Empty;

    private void OnControlApprovalRequested(object? sender, PendingControlApproval pending)
    {
        void Show()
        {
            PendingControlApproval?.Deny(); // only one can be pending; the host sees the newest
            PendingControlApproval = pending;
            _notificationService.Show("Someone with your control code wants to control this PC. Open Friends to allow or deny.");
            pending.Answer.ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (PendingControlApproval == pending) PendingControlApproval = null;
            }), TaskScheduler.Default);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Show);
        else Show();
    }

    [RelayCommand]
    private void AllowControlRequest() => PendingControlApproval?.Allow();

    [RelayCommand]
    private void DenyControlRequest() => PendingControlApproval?.Deny();

    private void OnParticipantsChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(RefreshParticipants));
            return;
        }
        RefreshParticipants();
    }

    private void RefreshParticipants()
    {
        Participants.Clear();
        foreach (var participant in _shareService.GetParticipants()) Participants.Add(participant);
        OnPropertyChanged(nameof(HasParticipants));
        OnPropertyChanged(nameof(ParticipantsSummary));
    }


    public bool IsLive => State == ShareSessionState.Live;
    public bool IsRoomOpen => State == ShareSessionState.RoomOpen;
    public bool IsInCall => IsRoomOpen || IsLive || IsFriendJoined;
    public bool IsIdle => State is ShareSessionState.Idle or ShareSessionState.Faulted;
    public bool HasFault => State == ShareSessionState.Faulted;

    public bool IsStageActive => IsLive || IsWatchingFriend;

    public string ParticipantCountText => IsFriendJoined ? "2" : "1";

    /// <summary>Your name in rooms and chat: the account's display name when signed in, else the Windows user name.</summary>
    public string SelfName => AccountFriends?.Account is { IsSignedIn: true } account ? account.DisplayName : Environment.UserName;

    public string SelfAvatarInitial => !string.IsNullOrEmpty(SelfName)
        ? SelfName[..1].ToUpperInvariant()
        : "U";

    public string FriendAvatarInitial => !string.IsNullOrEmpty(FriendName)
        ? FriendName[..1].ToUpperInvariant()
        : "F";

    public string ActiveStreamUrl => IsWatchingFriend ? ActiveFriendWatchUrl : LocalPreviewUrl;

    /// <summary>
    /// The host previews through loopback so its own firewall or selected LAN/VPN adapter cannot
    /// block the embedded player. <see cref="ViewerUrl"/> remains the address copied to friends.
    /// </summary>
    public string LocalPreviewUrl
    {
        get
        {
            if (!Uri.TryCreate(ViewerUrl, UriKind.Absolute, out var uri)) return string.Empty;
            // An HTTPS room's certificate names its Tailscale host, which also reaches this PC;
            // swapping in loopback would fail the certificate check.
            if (uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
            return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.AbsoluteUri;
        }
    }

    /// <summary>The name shown on the tile, as a viewer would label this stream.</summary>
    public string TileName => SelfName;

    public string ElapsedText => Telemetry.Elapsed.TotalHours >= 1
        ? Telemetry.Elapsed.ToString(@"h\:mm\:ss")
        : Telemetry.Elapsed.ToString(@"mm\:ss");

    /// <summary>"1080p60 · 16.2 Mbps · 59 FPS" — only ever real measurements.</summary>
    public string StatsLine => IsLive
        ? $"{Settings.SelectedQuality.ShortLabel}  ·  {Telemetry.Fps:0} FPS  ·  {Telemetry.BitrateMbps:0.0} Mbps"
        : (IsWatchingFriend ? "Connected to friend" : string.Empty);

    public string ViewerCountText => Telemetry.ViewerCount == 1 ? "1 watching" : $"{Telemetry.ViewerCount} watching";

    public bool ShowEncoderWarning => IsLive && Telemetry.IsEncoderOverloaded;

    /// <summary>Tooltip and label for the big share button, which doubles as stop.</summary>
    public string ShareButtonTooltip => IsLive ? "Stop sharing your screen" : "Share your screen";

    /// <summary>Speaker or muted-speaker, matching what the stream currently carries.</summary>
    public string AudioGlyph => IsDeafened ? "\uE74F" : "\uE767";

    /// <summary>Microphone (E720), or the slashed microphone (F781) when muted. E721 is the search magnifier.</summary>
    public string MicGlyph => IsMicMuted ? "\uF781" : "\uE720";

    public string AudioTooltip => IsDeafened ? "Hear the call" : "Deafen call audio";

    public ShareScreenViewModel(
        IShareScreenService shareService,
        ShareScreenSettingsViewModel settings,
        NotificationService notificationService,
        LogService logService,
        ShareChatStore? chatStore = null,
        ShareAttachmentStore? attachmentStore = null,
        IRoomVoiceService? voiceService = null,
        FriendsHubViewModel? friendsHub = null)
    {
        _shareService = shareService;
        AccountFriends = friendsHub;
        Settings = settings;
        _notificationService = notificationService;
        _logService = logService;
        _session = new ShareRoomSession(logService);
        var router = new ShareRoomRouter(shareService, _session, () => State is ShareSessionState.RoomOpen or ShareSessionState.Live);
        _conversation = new ShareRoomConversation(chatStore ?? new ShareChatStore(), attachmentStore ?? new ShareAttachmentStore(), router.SendChatAsync, logService);
        _voice = new ShareVoiceCoordinator(voiceService ?? new RoomVoiceService(logService), router.SendVoiceAsync);

        _session.ChatReceived += (_, message) => _conversation.Receive(message);
        _session.VoiceReceived += (_, packet) => _voice.Receive(packet);
        _session.VoiceConnectionChanged += OnJoinedVoiceConnectionChanged;
        _session.ChatEnded += OnJoinedChatEnded;
        _conversation.MessageArrived += (_, message) => OnUiThread(() => ShowIncomingMessage(message));
        _conversation.AttachmentRejected += (_, _) => OnUiThread(() =>
            _notificationService.Show("A received attachment failed its safety check and was discarded."));

        // Restore persisted chat messages across restarts, updates, and reinstalls
        foreach (var msg in _conversation.LoadHistory())
        {
            Messages.Add(msg);
            if (msg.IsAttachment) SharedFiles.Add(msg);
        }

        if (AccountFriends is { } hub)
        {
            hub.SetFilter(FriendsFilterTab, FriendSearchText);
            hub.GetRoomLink = GetRoomLinkForInviteAsync;
            hub.JoinRequested += (_, invite) => JoinFromInvite(invite, hub.NameOf(invite.FromUserId));
            hub.Account.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SelfName));
                OnPropertyChanged(nameof(SelfAvatarInitial));
                OnPropertyChanged(nameof(TileName));
            };

            // Friends see "In a call" / "Sharing screen" while that is true
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(IsInCall) or nameof(IsLive)) hub.UpdateSession(IsInCall, IsLive);
            };
        }

        // Ensure friend list updates propagate to filtered properties
        Settings.SavedFriends.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSavedRooms));
            OnPropertyChanged(nameof(FilteredFriends));
            OnPropertyChanged(nameof(OnlineFriendsCount));
            OnPropertyChanged(nameof(AllFriendsCount));
            OnPropertyChanged(nameof(StreamingFriendsCount));
        };

        _shareService.StatusChanged += OnStatusChanged;
        _shareService.ParticipantsChanged += OnParticipantsChanged;
        _shareService.ControlApprovalRequested += OnControlApprovalRequested;
        _shareService.ChatMessageReceived += OnHostChatMessageReceived;
        _shareService.VoicePacketReceived += OnHostVoicePacketReceived;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShareScreenSettingsViewModel.SelectedQuality)
                or nameof(ShareScreenSettingsViewModel.SourceSummary))
            {
                OnPropertyChanged(nameof(StatsLine));
            }

            if (e.PropertyName == nameof(ShareScreenSettingsViewModel.AllowRemoteControl))
            {
                OnPropertyChanged(nameof(ShowControlButton));
            }
        };
    }

    partial void OnStateChanged(ShareSessionState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsRoomOpen));
        OnPropertyChanged(nameof(IsInCall));
        OnPropertyChanged(nameof(CallTitle));
        OnPropertyChanged(nameof(InviteButtonText));
        OnPropertyChanged(nameof(PrivacyStatusText));
        OnPropertyChanged(nameof(PrivacyStatusColor));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(StatsLine));
        OnPropertyChanged(nameof(ShowEncoderWarning));
        OnPropertyChanged(nameof(ShareButtonTooltip));
        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(ActiveStreamUrl));
        ToggleSharingCommand.NotifyCanExecuteChanged();
        CopyViewerLinkCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => ToggleSharingCommand.NotifyCanExecuteChanged();

    partial void OnTelemetryChanged(ShareTelemetry value)
    {
        OnPropertyChanged(nameof(StatsLine));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(ViewerCountText));
        OnPropertyChanged(nameof(ShowEncoderWarning));
    }

    // -- Commands ------------------------------------------------------------

    private bool CanToggleSharing() => !IsBusy && State is not (ShareSessionState.Starting or ShareSessionState.Stopping);

    /// <summary>One button for start and stop, the way a call control behaves.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSharing))]
    private async Task ToggleSharingAsync()
    {
        if (IsLive)
        {
            await StopSharingAsync();
            return;
        }

        IsBusy = true;
        _startCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message + "...");
            var result = await _shareService.StartAsync(SessionSettings(), progress, _startCts.Token);
            if (result.UntrustedNetworkRefused) _untrustedRetry = UntrustedRetry.Share;

            if (result.State == ShareSessionState.Live)
            {
                StartVoiceDevices();
                _notificationService.Show($"Sharing {Settings.SelectedQuality.ShortLabel} — room {result.Room?.FormattedCode}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Start failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _startCts?.Dispose();
            _startCts = null;
        }
    }

    [RelayCommand]
    private async Task StopSharingAsync()
    {
        _startCts?.Cancel();
        await _shareService.StopScreenShareAsync();
        StatusMessage = "Private room open";
    }

    [RelayCommand]
    private async Task StartOrCopyInviteAsync()
    {
        if (State is ShareSessionState.Idle or ShareSessionState.Faulted)
        {
            IsBusy = true;
            try
            {
                var result = await _shareService.StartRoomAsync(SessionSettings(), CancellationToken.None);
                Apply(result);
                if (result.UntrustedNetworkRefused) _untrustedRetry = UntrustedRetry.Room;
                if (result.State == ShareSessionState.RoomOpen) StartVoiceDevices();
            }
            catch (Exception ex)
            {
                _logService.LogError("[Friends] Could not open private room", ex);
                StatusMessage = ex.Message;
                return;
            }
            finally
            {
                IsBusy = false;
            }
        }

        if (HasViewerLink()) CopyViewerLink();
    }

    private bool HasViewerLink() => !string.IsNullOrWhiteSpace(ViewerUrl);

    /// <summary>
    /// For an invite from the Friends list: opens this PC's room if it isn't open yet and returns
    /// its link. A watch invite needs the screen shared first, since there'd be nothing to watch.
    /// </summary>
    private async Task<string?> GetRoomLinkForInviteAsync(AxePower.Core.Services.Friends.InviteKind kind)
    {
        if (kind == AxePower.Core.Services.Friends.InviteKind.Watch && !IsLive)
        {
            _notificationService.Show("Share your screen first, then invite friends to watch.");
            return null;
        }

        if (State is ShareSessionState.Idle or ShareSessionState.Faulted)
        {
            var result = await _shareService.StartRoomAsync(SessionSettings(), CancellationToken.None);
            Apply(result);
            if (result.UntrustedNetworkRefused)
            {
                _untrustedRetry = UntrustedRetry.Room;
                _notificationService.Show("Your room did not open: this network is Public or unclassified. Allow it on the Friends page to invite.");
            }
            if (result.State == ShareSessionState.RoomOpen) StartVoiceDevices();
        }

        return HasViewerLink() ? ViewerUrl : null;
    }

    /// <summary>Joining a friend's invite: the same as joining by link, without saving it as a room.</summary>
    private void JoinFromInvite(AxePower.Core.Services.Friends.FriendInvite invite, string friendName)
    {
        NavigateTo?.Invoke("Friends");
        _joinForScreen = invite.Kind == AxePower.Core.Services.Friends.InviteKind.Watch;
        JoinAddressInput = invite.RoomLink;
        FriendNameInput = friendName;
        var remember = RememberFriend;
        RememberFriend = false;
        try { ConnectToRoom(); }
        finally { RememberFriend = remember; }
    }

    [RelayCommand(CanExecute = nameof(HasViewerLink))]
    private void CopyViewerLink()
    {
        try
        {
            Clipboard.SetText(ViewerUrl);
            _notificationService.Show("Viewer link copied — send it to your friend.");
        }
        catch (Exception ex)
        {
            // Another app can hold the clipboard open; that is not worth a crash.
            _logService.LogError("[ShareScreen] Could not copy the viewer link", ex);
        }
    }

    // -- Remote control ------------------------------------------------------

    [ObservableProperty]
    private RemoteControlState _controlState = RemoteControlState.Disabled;

    /// <summary>The code a viewer must type to take control. Never part of the viewing link.</summary>
    [ObservableProperty]
    private string _controlCode = string.Empty;

    public bool IsControlOffered => ControlState != RemoteControlState.Disabled;
    public bool IsControlActive => ControlState == RemoteControlState.Active;

    /// <summary>The control button only appears once it has been switched on in Settings.</summary>
    public bool ShowControlButton => Settings.AllowRemoteControl;

    public string ControlBannerText => ControlState switch
    {
        RemoteControlState.Active => "A viewer is controlling this PC right now.",
        RemoteControlState.Offered => $"Remote control is on. Give your friend the code {ControlCode} — it is not in the link.",
        _ => string.Empty
    };

    partial void OnControlStateChanged(RemoteControlState value)
    {
        OnPropertyChanged(nameof(IsControlOffered));
        OnPropertyChanged(nameof(IsControlActive));
        OnPropertyChanged(nameof(ControlBannerText));
    }

    partial void OnControlCodeChanged(string value) => OnPropertyChanged(nameof(ControlBannerText));

    /// <summary>
    /// Turns remote control on or off for this session.
    ///
    /// Switching it on asks first, in plain words, because this is the one control on the page
    /// that hands someone else the keyboard rather than just showing them a picture. Switching it
    /// off never asks -- taking control away must always be one click.
    /// </summary>
    [RelayCommand]
    private void ToggleRemoteControl()
    {
        if (!IsLive) return;

        if (ControlState != RemoteControlState.Disabled)
        {
            _shareService.SetRemoteControl(false);
            _notificationService.Show("Remote control is off.");
            return;
        }

        var answer = MessageBox.Show(
            "Letting a viewer control this PC gives them your mouse and keyboard.\n\n" +
            "While it is on they can open your files, read your email, install software, and act " +
            "as you on anything you are signed in to. You will see what they do, but nothing here " +
            "asks you to approve it first.\n\n" +
            "Only turn this on for someone you trust, and turn it off the moment you are done.\n\n" +
            "Turn remote control on?",
            "Let a viewer control this PC?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        var code = _shareService.SetRemoteControl(true);
        if (!string.IsNullOrEmpty(code))
        {
            _notificationService.Show($"Remote control is on. Control code: {code}");
        }
    }

    [RelayCommand]
    private void CopyControlCode()
    {
        if (string.IsNullOrWhiteSpace(ControlCode)) return;

        try
        {
            Clipboard.SetText(ControlCode);
            _notificationService.Show("Control code copied. Send it separately from the link.");
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not copy the control code", ex);
        }
    }

    [RelayCommand]
    private void ToggleAudio()
    {
        IsDeafened = !IsDeafened;
        _notificationService.Show(IsDeafened ? "Call audio deafened" : "Call audio active");
    }

    [RelayCommand]
    private void ToggleMic()
    {
        IsMicMuted = !IsMicMuted;
        _voice.SetMuted(IsMicMuted);
        _notificationService.Show(IsMicMuted ? "Microphone muted" : "Microphone active");
        OnPropertyChanged(nameof(MicGlyph));
    }

    [RelayCommand]
    private void OpenJoinModal()
    {
        _joinForScreen = false;
        JoinStatusMessage = string.Empty;
        IsJoinModalOpen = true;
    }

    [RelayCommand]
    private void CloseJoinModal()
    {
        IsJoinModalOpen = false;
    }

    [RelayCommand]
    private void SetFriendsFilter(string tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
        {
            FriendsFilterTab = tab;
        }
    }

    [RelayCommand]
    private void ToggleFriendsPanel() => IsFriendsPanelOpen = !IsFriendsPanelOpen;

    [RelayCommand]
    private void SetConversationPanelTab(string tab)
    {
        if (tab is "Chat" or "Files") ConversationPanelTab = tab;
    }

    [RelayCommand]
    private void CallFriend(SavedFriend? friend)
    {
        if (friend == null) return;
        if (!string.IsNullOrWhiteSpace(friend.Address))
        {
            _joinForScreen = false;
            JoinAddressInput = friend.Address;
            FriendNameInput = friend.Name;
            ConnectToRoom();
        }
        else
        {
            _notificationService.Show($"No address saved for {friend.Name}. Invite them to your room!");
            OpenJoinModal();
        }
    }

    [RelayCommand]
    private void WatchFriendStream(SavedFriend? friend)
    {
        if (friend == null) return;
        var address = !string.IsNullOrWhiteSpace(friend.StreamUrl) ? friend.StreamUrl : friend.Address;
        if (string.IsNullOrWhiteSpace(address))
        {
            _notificationService.Show($"No room address saved for {friend.Name}.");
            return;
        }

        _joinForScreen = true;
        JoinAddressInput = address;
        FriendNameInput = friend.Name;
        ConnectToRoom();
    }

    [RelayCommand]
    private void MessageFriend(SavedFriend? friend)
    {
        if (friend == null) return;
        ChannelName = friend.Name;
        OpenChat();
    }

    [RelayCommand]
    private void RemoveFriend(SavedFriend? friend)
    {
        if (friend == null) return;
        Settings.RemoveFriend(friend);
        OnPropertyChanged(nameof(FilteredFriends));
        OnPropertyChanged(nameof(OnlineFriendsCount));
        OnPropertyChanged(nameof(AllFriendsCount));
        OnPropertyChanged(nameof(StreamingFriendsCount));
        _notificationService.Show($"Removed {friend.Name} from friends.");
    }

    [RelayCommand]
    private void PasteJoinAddress()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    JoinAddressInput = text;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not paste from clipboard", ex);
        }
    }

    [RelayCommand]
    private void ConnectToRoom()
    {
        switch (ShareRoomLink.TryNormalize(JoinAddressInput, out var raw, out var uri))
        {
            case ShareRoomLink.JoinAddressProblem.Empty:
                JoinStatusMessage = "Please enter your friend's room link or IP address.";
                return;
            case ShareRoomLink.JoinAddressProblem.Invalid:
                JoinStatusMessage = "Invalid address. Format: http://<ip>:<port>/<room>";
                return;
        }

        ActiveFriendWatchUrl = raw;
        _roomSessionSecret = ShareRoomLink.ParseViewerSecret(uri!);
        IsWatchingFriend = _joinForScreen;
        IsFriendJoined = true;
        FriendName = !string.IsNullOrWhiteSpace(FriendNameInput) ? FriendNameInput.Trim() : uri!.Host;
        IsJoinModalOpen = false;
        JoinStatusMessage = string.Empty;

        if (RememberFriend)
        {
            Settings.AddOrUpdateFriend(FriendName, raw, FriendStatus.InCall);
        }

        _session.JoinChat(uri!);
        StopVoice();
        if (!_session.JoinVoice(uri!))
        {
            _notificationService.Show("Voice requires a current AxePower private-room link.");
        }

        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(ActiveStreamUrl));
        OnPropertyChanged(nameof(FriendAvatarInitial));
        OnPropertyChanged(nameof(StatsLine));

        _notificationService.Show($"Joined room with {FriendName}!");
    }

    /// <summary>A joined room's chat stopped on its own: say why where the join status shows, and as a toast.</summary>
    private void OnJoinedChatEnded(object? sender, string reason)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            JoinStatusMessage = reason;
            _notificationService.Show(reason);
        });
    }

    private void OnJoinedVoiceConnectionChanged(object? sender, bool connected)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (connected) StartVoiceDevices();
            IsVoiceConnected = connected;
        });
    }

    private void StartVoiceDevices()
    {
        if (_voice.Start(IsMicMuted, IsDeafened))
        {
            IsVoiceConnected = true;
        }
        else
        {
            IsVoiceConnected = false;
            _notificationService.Show("Voice devices are unavailable. Screen sharing and chat still work.");
        }
    }

    private void OnHostVoicePacketReceived(object? sender, byte[] opusPacket) => _voice.Receive(opusPacket);

    private void StopVoice()
    {
        _session.CloseVoice();
        _voice.Stop();
        IsVoiceConnected = false;
    }

    private void OnHostChatMessageReceived(object? sender, ShareChatMessage msg) => _conversation.Receive(msg);

    /// <summary>Runs on the UI thread when there is one; inline otherwise (tests, shutdown).</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
            return;
        }
        action();
    }

    private void ShowIncomingMessage(ShareChatMessage incoming)
    {
        Messages.Add(incoming);
        if (incoming.IsAttachment) SharedFiles.Add(incoming);
        if (!IsChatOpen)
        {
            UnreadChatCount++;
            _notificationService.Show(incoming.IsAttachment
                ? $"{incoming.Sender} shared {incoming.AttachmentName}"
                : $"{incoming.Sender}: {incoming.Text}");
        }
    }

    [RelayCommand]
    private void ToggleChat()
    {
        IsChatOpen = !IsChatOpen;
        if (IsChatOpen)
        {
            UnreadChatCount = 0;
        }
    }

    [RelayCommand]
    private void OpenChat()
    {
        IsChatOpen = true;
        UnreadChatCount = 0;
    }

    [RelayCommand]
    private void CloseChat()
    {
        IsChatOpen = false;
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        SharedFiles.Clear();
        _conversation.Clear();
        _notificationService.Show("Chat history cleared");
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        await _conversation.SendTextAsync(SelfName, ChatInputText, message =>
        {
            Messages.Add(message);
            ChatInputText = string.Empty;
        });
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Share a file with this room",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var outgoing = _conversation.PrepareFile(SelfName, dialog.FileName);
            Messages.Add(outgoing.Card);
            SharedFiles.Add(outgoing.Card);
            ConversationPanelTab = "Files";
            IsChatOpen = true;

            await _conversation.SendFileAsync(outgoing);
            _notificationService.Show($"Shared {outgoing.Card.AttachmentName}");
        }
        catch (Exception ex)
        {
            _logService.LogError("[Friends] Could not share attachment", ex);
            _notificationService.Show(ex.Message);
        }
    }

    [RelayCommand]
    private void SaveAttachment(ShareChatMessage? attachment)
    {
        if (attachment == null || !attachment.IsAttachment || !_conversation.IsAvailable(attachment.AttachmentId))
        {
            _notificationService.Show("This attachment is no longer available on this device.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save shared file",
            FileName = ShareAttachmentStore.SanitizeFileName(attachment.AttachmentName),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _conversation.Export(attachment.AttachmentId, dialog.FileName);
            _notificationService.Show($"Saved {attachment.AttachmentName}");
        }
        catch (Exception ex)
        {
            _logService.LogError("[Friends] Could not save attachment", ex);
            _notificationService.Show("The attachment could not be saved.");
        }
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        if (State != ShareSessionState.Idle)
        {
            await _shareService.StopAsync();
        }

        if (IsFriendJoined)
        {
            Settings.UpdateFriendStatus(FriendName, FriendStatus.Offline);
            IsWatchingFriend = false;
            ActiveFriendWatchUrl = string.Empty;
            _roomSessionSecret = string.Empty;
            IsFriendJoined = false;
            _notificationService.Show("Disconnected from call");
        }

        _joinForScreen = true;

        _session.Leave();
        StopVoice();

        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(ActiveStreamUrl));
        OnPropertyChanged(nameof(StatsLine));
    }

    /// <summary>The gear: the preferences live on the Settings page, so go there.</summary>
    [RelayCommand]
    private void OpenSettings() => NavigateTo?.Invoke("Settings:ShareScreen");

    // -- Service events ------------------------------------------------------

    private void OnStatusChanged(object? sender, ShareSessionStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Apply(status)));
            return;
        }

        Apply(status);
    }

    private void Apply(ShareSessionStatus status)
    {
        State = status.State;
        Telemetry = status.Telemetry;
        ViewerUrl = status.ViewerUrl;
        _roomSessionSecret = Uri.TryCreate(ViewerUrl, UriKind.Absolute, out var viewerUri)
            ? ShareRoomLink.ParseViewerSecret(viewerUri)
            : string.Empty;
        RoomCode = status.Room?.FormattedCode ?? string.Empty;
        EncoderName = status.Encoder?.DisplayName ?? EncoderName;
        ControlState = status.Control;
        ControlCode = status.ControlCode;
        if (status.UntrustedNetworkRefused)
        {
            IsUntrustedNetworkRefused = true;
            UntrustedNetworkWarning = status.Message;
        }
        else if (status.State is not ShareSessionState.Idle)
        {
            IsUntrustedNetworkRefused = false;
            UntrustedNetworkWarning = string.Empty;
        }
        if (status.State is ShareSessionState.Idle or ShareSessionState.Faulted) RefreshParticipants();

        OnPropertyChanged(nameof(ActiveStreamUrl));
        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(IsSecureTransport));
        OnPropertyChanged(nameof(PrivacyStatusText));

        StatusMessage = status.State switch
        {
            ShareSessionState.Idle when status.UntrustedNetworkRefused => "Not started: untrusted network",
            ShareSessionState.Idle => "Ready to share",
            ShareSessionState.Live => string.IsNullOrEmpty(status.Message) ? "Live" : status.Message,
            _ => status.Message
        };
    }

    public void Dispose()
    {
        _shareService.StatusChanged -= OnStatusChanged;
        _shareService.ParticipantsChanged -= OnParticipantsChanged;
        _shareService.ControlApprovalRequested -= OnControlApprovalRequested;
        PendingControlApproval?.Deny();
        _shareService.ChatMessageReceived -= OnHostChatMessageReceived;
        _shareService.VoicePacketReceived -= OnHostVoicePacketReceived;
        _session.VoiceConnectionChanged -= OnJoinedVoiceConnectionChanged;
        _session.ChatEnded -= OnJoinedChatEnded;
        _startCts?.Cancel();
        _startCts?.Dispose();
        StopVoice();
        _session.Dispose();
        _voice.Dispose();
    }
}
