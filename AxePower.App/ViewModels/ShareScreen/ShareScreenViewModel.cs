using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.ShareScreen;

/// <summary>
/// The sharing stage: the picture, the controls over it, and the session behind them.
///
/// It owns no preferences of its own -- those live in <see cref="ShareScreenSettingsViewModel"/>
/// on the Settings page -- so this page stays what the user came to it for. Every live number
/// comes from the encoder and the router, never from what was requested.
/// </summary>
public partial class ShareScreenViewModel : ObservableObject, IDisposable
{
    private readonly IShareScreenService _shareService;
    private readonly NotificationService _notificationService;
    private readonly LogService _logService;

    private CancellationTokenSource? _startCts;

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

    public bool IsLive => State == ShareSessionState.Live;
    public bool IsIdle => State is ShareSessionState.Idle or ShareSessionState.Faulted;
    public bool HasFault => State == ShareSessionState.Faulted;

    /// <summary>The name shown on the tile, as a viewer would label this stream.</summary>
    public string TileName => Environment.UserName;

    public string ElapsedText => Telemetry.Elapsed.TotalHours >= 1
        ? Telemetry.Elapsed.ToString(@"h\:mm\:ss")
        : Telemetry.Elapsed.ToString(@"mm\:ss");

    /// <summary>"1080p60 · 16.2 Mbps · 59 FPS" — only ever real measurements.</summary>
    public string StatsLine => IsLive
        ? $"{Settings.SelectedQuality.ShortLabel}  ·  {Telemetry.Fps:0} FPS  ·  {Telemetry.BitrateMbps:0.0} Mbps"
        : string.Empty;

    public string ViewerCountText => Telemetry.ViewerCount == 1 ? "1 watching" : $"{Telemetry.ViewerCount} watching";

    public bool ShowEncoderWarning => IsLive && Telemetry.IsEncoderOverloaded;

    /// <summary>Tooltip and label for the big share button, which doubles as stop.</summary>
    public string ShareButtonTooltip => IsLive ? "Stop sharing your screen" : "Share your screen";

    /// <summary>Speaker or muted-speaker, matching what the stream currently carries.</summary>
    public string AudioGlyph => Settings.AudioMode switch
    {
        ShareAudioMode.None => "",            // muted
        ShareAudioMode.Microphone => "",      // microphone
        _ => ""                               // speaker
    };

    public string AudioTooltip => Settings.AudioMode switch
    {
        ShareAudioMode.None => "Your friends hear nothing — click to share system audio",
        ShareAudioMode.Microphone => "Your friends hear your microphone — click to mute",
        _ => "Your friends hear this PC — click to mute"
    };

    public ShareScreenViewModel(
        IShareScreenService shareService,
        ShareScreenSettingsViewModel settings,
        NotificationService notificationService,
        LogService logService)
    {
        _shareService = shareService;
        Settings = settings;
        _notificationService = notificationService;
        _logService = logService;

        _shareService.StatusChanged += OnStatusChanged;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShareScreenSettingsViewModel.SelectedQuality)
                or nameof(ShareScreenSettingsViewModel.SourceSummary))
            {
                OnPropertyChanged(nameof(StatsLine));
            }

            if (e.PropertyName == nameof(ShareScreenSettingsViewModel.AudioMode))
            {
                OnPropertyChanged(nameof(AudioGlyph));
                OnPropertyChanged(nameof(AudioTooltip));
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
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(StatsLine));
        OnPropertyChanged(nameof(ShowEncoderWarning));
        OnPropertyChanged(nameof(ShareButtonTooltip));
        ToggleSharingCommand.NotifyCanExecuteChanged();
        CopyViewerLinkCommand.NotifyCanExecuteChanged();
        OpenViewerLinkCommand.NotifyCanExecuteChanged();
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
        await _shareService.StopAsync();
        StatusMessage = "Ready to share";
    }

    private bool HasViewerLink() => !string.IsNullOrWhiteSpace(ViewerUrl);

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

    [RelayCommand(CanExecute = nameof(HasViewerLink))]
    private void OpenViewerLink()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ViewerUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not open the viewer link", ex);
        }
    }

    [RelayCommand]
    private void TogglePreview() => IsPreviewEnabled = !IsPreviewEnabled;

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

    public string ControlGlyph => ControlState == RemoteControlState.Disabled ? "" : "";

    public string ControlTooltip => ControlState switch
    {
        RemoteControlState.Active => "Someone is controlling this PC — click to cut them off",
        RemoteControlState.Offered => "Control is on, waiting for a viewer — click to switch it off",
        _ => "Let a viewer control this PC"
    };

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
        OnPropertyChanged(nameof(ControlGlyph));
        OnPropertyChanged(nameof(ControlTooltip));
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

    /// <summary>Cycles the audio source from the stage without a trip to Settings.</summary>
    [RelayCommand]
    private void ToggleAudio()
    {
        Settings.AudioMode = Settings.AudioMode == ShareAudioMode.None
            ? ShareAudioMode.SystemAudio
            : ShareAudioMode.None;
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
        RoomCode = status.Room?.FormattedCode ?? string.Empty;
        EncoderName = status.Encoder?.DisplayName ?? EncoderName;
        ControlState = status.Control;
        ControlCode = status.ControlCode;

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
        _startCts?.Cancel();
        _startCts?.Dispose();
    }
}
