using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    private readonly ShareChatStore _chatStore;
    private readonly ShareAttachmentStore _attachmentStore;
    private readonly IRoomVoiceService _voiceService;
    private readonly Dictionary<string, IncomingAttachment> _incomingAttachments = new(StringComparer.Ordinal);
    private readonly object _attachmentLock = new();
    private readonly SemaphoreSlim _voiceSendGate = new(1, 1);

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
        _voiceService.SetDeafened(value);
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

    private ClientWebSocket? _clientChatSocket;
    private CancellationTokenSource? _chatCts;
    private string _roomSessionSecret = string.Empty;
    private ClientWebSocket? _clientVoiceSocket;
    private CancellationTokenSource? _voiceCts;

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

    public string MicGlyph => IsMicMuted ? "\uE721" : "\uE720";

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
        _chatStore = chatStore ?? new ShareChatStore();
        _attachmentStore = attachmentStore ?? new ShareAttachmentStore();
        _voiceService = voiceService ?? new RoomVoiceService(logService);
        _voiceService.EncodedPacketReady += OnLocalVoicePacketReady;

        // Restore persisted chat messages across restarts, updates, and reinstalls
        foreach (var msg in _chatStore.LoadMessages())
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
            var result = await _shareService.StartAsync(Settings.BuildSessionSettings(), progress, _startCts.Token);

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
                var result = await _shareService.StartRoomAsync(Settings.BuildSessionSettings(), CancellationToken.None);
                Apply(result);
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
            var result = await _shareService.StartRoomAsync(Settings.BuildSessionSettings(), CancellationToken.None);
            Apply(result);
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
        _voiceService.SetMuted(IsMicMuted);
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
        var raw = JoinAddressInput?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            JoinStatusMessage = "Please enter your friend's room link or IP address.";
            return;
        }

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            JoinStatusMessage = "Invalid address. Format: http://<ip>:<port>/<room>";
            return;
        }

        ActiveFriendWatchUrl = raw;
        _roomSessionSecret = ParseViewerSecret(uri);
        IsWatchingFriend = _joinForScreen;
        IsFriendJoined = true;
        FriendName = !string.IsNullOrWhiteSpace(FriendNameInput) ? FriendNameInput.Trim() : uri.Host;
        IsJoinModalOpen = false;
        JoinStatusMessage = string.Empty;

        if (RememberFriend)
        {
            Settings.AddOrUpdateFriend(FriendName, raw, FriendStatus.InCall);
        }

        ConnectChatClient(uri);
        ConnectVoiceClient(uri);

        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(ActiveStreamUrl));
        OnPropertyChanged(nameof(FriendAvatarInitial));
        OnPropertyChanged(nameof(StatsLine));

        _notificationService.Show($"Joined room with {FriendName}!");
    }

    private void ConnectChatClient(Uri uri)
    {
        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _chatCts = new CancellationTokenSource();

        var chatUri = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "https" ? "wss" : "ws",
            Path = uri.AbsolutePath.TrimEnd('/') + "/chat",
            Query = "v=" + Uri.EscapeDataString(_roomSessionSecret),
            Fragment = string.Empty
        }.Uri;

        _ = Task.Run(async () =>
        {
            try
            {
                var ws = new ClientWebSocket();
                _clientChatSocket = ws;
                await ws.ConnectAsync(chatUri, _chatCts.Token);

                var buffer = new byte[8192];
                while (!_chatCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using var messageBuffer = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer.AsMemory(), _chatCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        messageBuffer.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage && messageBuffer.Length <= 64 * 1024);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text || messageBuffer.Length > 64 * 1024) continue;

                    try
                    {
                        if (RoomMessageCrypto.TryDecrypt(messageBuffer.ToArray(), _roomSessionSecret, out var msg) && msg != null)
                            DispatchIncomingWireMessage(msg with { IsSelf = false });
                    }
                    catch (JsonException) { }
                }
            }
            catch { }
        });
    }

    private void ConnectVoiceClient(Uri uri)
    {
        StopVoice();
        if (string.IsNullOrWhiteSpace(_roomSessionSecret))
        {
            _notificationService.Show("Voice requires a current AxePower private-room link.");
            return;
        }

        _voiceCts = new CancellationTokenSource();
        var voiceUri = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "https" ? "wss" : "ws",
            Path = uri.AbsolutePath.TrimEnd('/') + "/voice",
            Query = "v=" + Uri.EscapeDataString(_roomSessionSecret),
            Fragment = string.Empty
        }.Uri;

        _ = Task.Run(async () =>
        {
            try
            {
                var ws = new ClientWebSocket();
                _clientVoiceSocket = ws;
                await ws.ConnectAsync(voiceUri, _voiceCts.Token);
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    StartVoiceDevices();
                    IsVoiceConnected = true;
                });

                var buffer = new byte[8192];
                while (!_voiceCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using var packetBuffer = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer.AsMemory(), _voiceCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        packetBuffer.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage && packetBuffer.Length <= 8192);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Binary &&
                        RoomVoiceCrypto.TryDecrypt(packetBuffer.ToArray(), _roomSessionSecret, out var opusPacket))
                    {
                        _voiceService.ReceivePacket(opusPacket);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logService.LogWarning($"[FriendsVoice] Voice connection ended: {ex.Message}");
            }
            finally
            {
                Application.Current?.Dispatcher.BeginInvoke(() => IsVoiceConnected = false);
            }
        });
    }

    private void StartVoiceDevices()
    {
        if (_voiceService.Start())
        {
            _voiceService.SetMuted(IsMicMuted);
            _voiceService.SetDeafened(IsDeafened);
            IsVoiceConnected = true;
        }
        else
        {
            IsVoiceConnected = false;
            _notificationService.Show("Voice devices are unavailable. Screen sharing and chat still work.");
        }
    }

    private void OnLocalVoicePacketReady(object? sender, byte[] opusPacket) => _ = SendVoicePacketAsync(opusPacket);

    private void OnHostVoicePacketReceived(object? sender, byte[] opusPacket) => _voiceService.ReceivePacket(opusPacket);

    private async Task SendVoicePacketAsync(byte[] opusPacket)
    {
        if (State is ShareSessionState.RoomOpen or ShareSessionState.Live)
        {
            _shareService.SendVoicePacket(opusPacket);
            return;
        }
        if (!IsFriendJoined || _clientVoiceSocket?.State != WebSocketState.Open || string.IsNullOrWhiteSpace(_roomSessionSecret)) return;

        await _voiceSendGate.WaitAsync();
        try
        {
            var encrypted = RoomVoiceCrypto.Encrypt(opusPacket, _roomSessionSecret);
            await _clientVoiceSocket.SendAsync(encrypted.AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"[FriendsVoice] Could not send voice packet: {ex.Message}");
        }
        finally
        {
            _voiceSendGate.Release();
        }
    }

    private void StopVoice()
    {
        _voiceCts?.Cancel();
        _voiceCts?.Dispose();
        _voiceCts = null;
        if (_clientVoiceSocket != null)
        {
            try { _clientVoiceSocket.Dispose(); } catch { }
            _clientVoiceSocket = null;
        }
        _voiceService.Stop();
        IsVoiceConnected = false;
    }

    private void OnHostChatMessageReceived(object? sender, ShareChatMessage msg)
    {
        DispatchIncomingWireMessage(msg with { IsSelf = false });
    }

    private void DispatchIncomingWireMessage(ShareChatMessage message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ProcessIncomingWireMessage(message)));
            return;
        }

        ProcessIncomingWireMessage(message);
    }

    private void ProcessIncomingWireMessage(ShareChatMessage message)
    {
        switch (message.Kind)
        {
            case ShareChatKinds.Text when !string.IsNullOrWhiteSpace(message.Text):
                AddIncomingMessage(message);
                break;

            case ShareChatKinds.FileOffer:
                if (message.AttachmentSize <= 0 || message.AttachmentSize > ShareAttachmentStore.MaxAttachmentBytes ||
                    message.ChunkCount <= 0 || message.ChunkCount > ShareAttachmentStore.MaxWireChunks ||
                    message.AttachmentSha256.Length != 64 || string.IsNullOrWhiteSpace(message.AttachmentId)) return;
                lock (_attachmentLock)
                {
                    if (_incomingAttachments.Count >= 8 && !_incomingAttachments.ContainsKey(message.AttachmentId)) return;
                    _incomingAttachments[message.AttachmentId] = new IncomingAttachment(message);
                }
                break;

            case ShareChatKinds.FileChunk:
                lock (_attachmentLock)
                {
                    if (_incomingAttachments.TryGetValue(message.AttachmentId, out var transfer))
                        transfer.AddChunk(message.ChunkIndex, message.ChunkData);
                }
                break;

            case ShareChatKinds.FileComplete:
                CompleteIncomingAttachment(message.AttachmentId);
                break;
        }
    }

    private void AddIncomingMessage(ShareChatMessage incoming)
    {
        _chatStore.AppendMessage(incoming);
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

    private void CompleteIncomingAttachment(string attachmentId)
    {
        IncomingAttachment? transfer;
        lock (_attachmentLock)
        {
            if (!_incomingAttachments.Remove(attachmentId, out transfer)) return;
        }

        try
        {
            var bytes = transfer.Combine();
            _attachmentStore.Accept(
                transfer.Offer.AttachmentId,
                transfer.Offer.AttachmentName,
                transfer.Offer.AttachmentSize,
                transfer.Offer.AttachmentSha256,
                bytes);

            var card = transfer.Offer with
            {
                Kind = ShareChatKinds.File,
                Text = string.Empty,
                ChunkData = string.Empty,
                ChunkIndex = 0
            };
            AddIncomingMessage(card);
        }
        catch (Exception ex)
        {
            _logService.LogError("[Friends] Rejected an invalid attachment", ex);
            _notificationService.Show("A received attachment failed its safety check and was discarded.");
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
        _chatStore.Clear();
        _notificationService.Show("Chat history cleared");
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var text = ChatInputText?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var message = new ShareChatMessage(
            Guid.NewGuid().ToString("N"),
            SelfName,
            text,
            DateTime.Now,
            IsSelf: true);

        Messages.Add(message);
        _chatStore.AppendMessage(message);
        ChatInputText = string.Empty;

        await SendWireMessageAsync(message);
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
            var file = _attachmentStore.Import(dialog.FileName);
            var chunkCount = (int)Math.Ceiling(file.Bytes.Length / (double)ShareAttachmentStore.WireChunkBytes);
            var card = new ShareChatMessage(
                Guid.NewGuid().ToString("N"), SelfName, string.Empty, DateTime.Now, true,
                ShareChatKinds.File, file.Id, file.Name, file.Size, file.Sha256, 0, chunkCount);

            Messages.Add(card);
            SharedFiles.Add(card);
            _chatStore.AppendMessage(card);
            ConversationPanelTab = "Files";
            IsChatOpen = true;

            var offer = card with { Kind = ShareChatKinds.FileOffer, IsSelf = false };
            await SendWireMessageAsync(offer);

            for (var i = 0; i < chunkCount; i++)
            {
                var offset = i * ShareAttachmentStore.WireChunkBytes;
                var length = Math.Min(ShareAttachmentStore.WireChunkBytes, file.Bytes.Length - offset);
                var chunk = Convert.ToBase64String(file.Bytes, offset, length);
                await SendWireMessageAsync(offer with
                {
                    Kind = ShareChatKinds.FileChunk,
                    ChunkIndex = i,
                    ChunkData = chunk
                });
            }

            await SendWireMessageAsync(offer with { Kind = ShareChatKinds.FileComplete, ChunkData = string.Empty });
            _notificationService.Show($"Shared {file.Name}");
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
        if (attachment == null || !attachment.IsAttachment || !_attachmentStore.IsAvailable(attachment.AttachmentId))
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
            _attachmentStore.Export(attachment.AttachmentId, dialog.FileName);
            _notificationService.Show($"Saved {attachment.AttachmentName}");
        }
        catch (Exception ex)
        {
            _logService.LogError("[Friends] Could not save attachment", ex);
            _notificationService.Show("The attachment could not be saved.");
        }
    }

    private async Task SendWireMessageAsync(ShareChatMessage message)
    {
        if (State is ShareSessionState.RoomOpen or ShareSessionState.Live)
        {
            _shareService.SendChatMessage(message);
            return;
        }

        if (!IsFriendJoined || _clientChatSocket?.State != WebSocketState.Open) return;
        try
        {
            var bytes = RoomMessageCrypto.Encrypt(message, _roomSessionSecret);
            await _clientChatSocket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logService.LogError("[Friends] Could not send room data", ex);
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

        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _chatCts = null;
        if (_clientChatSocket != null)
        {
            try { _clientChatSocket.Dispose(); } catch { }
            _clientChatSocket = null;
        }
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
            ? ParseViewerSecret(viewerUri)
            : string.Empty;
        RoomCode = status.Room?.FormattedCode ?? string.Empty;
        EncoderName = status.Encoder?.DisplayName ?? EncoderName;
        ControlState = status.Control;
        ControlCode = status.ControlCode;

        OnPropertyChanged(nameof(ActiveStreamUrl));
        OnPropertyChanged(nameof(IsStageActive));
        OnPropertyChanged(nameof(IsSecureTransport));
        OnPropertyChanged(nameof(PrivacyStatusText));

        StatusMessage = status.State switch
        {
            ShareSessionState.Idle => "Ready to share",
            ShareSessionState.Live => string.IsNullOrEmpty(status.Message) ? "Live" : status.Message,
            _ => status.Message
        };
    }

    public void Dispose()
    {
        _shareService.StatusChanged -= OnStatusChanged;
        _shareService.ChatMessageReceived -= OnHostChatMessageReceived;
        _shareService.VoicePacketReceived -= OnHostVoicePacketReceived;
        _voiceService.EncodedPacketReady -= OnLocalVoicePacketReady;
        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _clientChatSocket?.Dispose();
        _startCts?.Cancel();
        _startCts?.Dispose();
        StopVoice();
        _voiceService.Dispose();
        _voiceSendGate.Dispose();
    }

    private sealed class IncomingAttachment
    {
        private readonly byte[][] _chunks;

        public ShareChatMessage Offer { get; }

        public IncomingAttachment(ShareChatMessage offer)
        {
            Offer = offer;
            _chunks = new byte[offer.ChunkCount][];
        }

        public void AddChunk(int index, string base64)
        {
            if (index < 0 || index >= _chunks.Length || string.IsNullOrWhiteSpace(base64)) return;
            try
            {
                var bytes = Convert.FromBase64String(base64);
                if (bytes.Length <= ShareAttachmentStore.WireChunkBytes) _chunks[index] = bytes;
            }
            catch (FormatException) { }
        }

        public byte[] Combine()
        {
            if (_chunks.Any(chunk => chunk == null))
                throw new InvalidDataException("The attachment transfer was incomplete.");

            using var output = new MemoryStream((int)Offer.AttachmentSize);
            foreach (var chunk in _chunks) output.Write(chunk!);
            return output.ToArray();
        }
    }

    private static string ParseViewerSecret(Uri uri)
    {
        var fragment = uri.Fragment.TrimStart('#');
        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Equals("v", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return string.Empty;
    }
}
