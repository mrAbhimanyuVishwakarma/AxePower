using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services.LanDrop;

namespace AxePower.Core.Services.ShareScreen;

/// <summary>
/// Drives one sharing session end to end: provision tools, pick an encoder, start the router,
/// start the encoder, then watch the numbers and step the quality down if the encoder cannot keep
/// up.
///
/// The host encodes once and publishes once; the router fans that single stream out, so a fifth
/// viewer costs the host nothing extra.
/// </summary>
public sealed class ShareScreenService : IShareScreenService, IDisposable
{
    private readonly LogService _logService;
    private readonly IShareToolProvider _toolProvider;
    private readonly IShareDeviceProvider _deviceProvider;
    private readonly IShareEncoderProbe _encoderProbe;
    private readonly MediaMtxServer _server;
    private readonly ShareEncodePipeline _pipeline;
    private readonly ShareAudioPump _audioPump;
    private readonly ShareViewerHost _viewerHost;
    private readonly Func<string?, NetworkClassificationResult> _networkClassifier;
    private readonly TailscaleCertificateProvider _tlsProvider;

    /// <summary>The certificate the open room is served with, kept so a later screen share matches it.</summary>
    private ShareTlsCertificate? _roomTls;

    /// <summary>Video-only viewers, from the media server's WebRTC sessions on this room's path.</summary>
    private IReadOnlyList<ShareParticipant> _videoViewers = Array.Empty<ShareParticipant>();

    private readonly SemaphoreSlim _transition = new(1, 1);
    private CancellationTokenSource? _monitorCts;

    /// <summary>Consecutive telemetry samples showing an overloaded encoder. Hysteresis, so one
    /// bad half-second does not wreck the picture.</summary>
    private int _overloadedSamples;

    private ShareScreenSettings _activeSettings = new();
    private string _ffmpegPath = string.Empty;

    public ShareSessionStatus Status { get; private set; } = ShareSessionStatus.Idle;

    public event EventHandler<ShareSessionStatus>? StatusChanged;

    public ShareScreenService(
        LogService logService,
        IShareToolProvider toolProvider,
        IShareDeviceProvider deviceProvider,
        IShareEncoderProbe encoderProbe,
        MediaMtxServer server,
        ShareEncodePipeline pipeline,
        ShareAudioPump audioPump,
        ShareViewerHost viewerHost,
        Func<string?, NetworkClassificationResult>? networkClassifier = null,
        TailscaleCertificateProvider? tlsProvider = null)
    {
        _tlsProvider = tlsProvider ?? new TailscaleCertificateProvider(logService);
        _networkClassifier = networkClassifier ?? NetworkProfileClassifier.ClassifyCurrentNetwork;
        _logService = logService;
        _toolProvider = toolProvider;
        _deviceProvider = deviceProvider;
        _encoderProbe = encoderProbe;
        _server = server;
        _pipeline = pipeline;
        _audioPump = audioPump;
        _viewerHost = viewerHost;

        _viewerHost.ControlHolderChanged += OnControlHolderChanged;
        _viewerHost.ControlDisabledAfterFailedAttempts += OnControlDisabledAfterFailedAttempts;
        _viewerHost.ParticipantsChanged += OnHostParticipantsChanged;
        _viewerHost.ApproveControl = AskHostToApproveAsync;
        _viewerHost.ChatMessageReceived += OnChatMessageReceived;
        _viewerHost.VoicePacketReceived += OnVoicePacketReceived;
        _pipeline.TelemetryUpdated += OnTelemetryUpdated;
        _pipeline.Failed += OnPipelineFailed;
    }

    // -- Start ---------------------------------------------------------------

    public async Task<ShareSessionStatus> StartRoomAsync(ShareScreenSettings settings, CancellationToken ct)
    {
        await _transition.WaitAsync(ct);
        try
        {
            if (Status.State is ShareSessionState.RoomOpen or ShareSessionState.Live) return Status;
            _activeSettings = settings.Clone();
            if (RefuseUntrustedNetwork(_activeSettings) is { } refused) return refused;
            var room = ShareRoomFactory.Create();
            var viewerHostName = MediaMtxServer.ResolveViewerHost(_activeSettings.ViewerHostOverride);
            _roomTls = await _tlsProvider.TryGetAsync(viewerHostName, ct);
            _viewerHost.Start(room, _roomTls?.HostName ?? viewerHostName, _activeSettings.WebrtcPort, _activeSettings.ViewerPort, _roomTls?.Certificate);
            Status = new ShareSessionStatus(
                ShareSessionState.RoomOpen,
                _roomTls != null ? "Private room open (HTTPS over Tailscale)" : "Private room open",
                room,
                _viewerHost.ViewerUrl,
                QualityPreset.For(_activeSettings.Quality),
                null,
                ShareTelemetry.Empty);
            RaiseStatusChanged();
            return Status;
        }
        catch (Exception ex)
        {
            _logService.LogError("[Friends] Could not open the private room", ex);
            return Fault(ex.Message);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task<ShareSessionStatus> StartAsync(
        ShareScreenSettings settings, IProgress<string>? step, CancellationToken ct)
    {
        await _transition.WaitAsync(ct);
        var reuseOpenRoom = false;
        try
        {
            if (Status.State is ShareSessionState.Live or ShareSessionState.Starting) return Status;

            reuseOpenRoom = Status.State == ShareSessionState.RoomOpen && Status.Room != null && _viewerHost.IsRunning;
            var openRoom = reuseOpenRoom ? Status.Room : null;

            _activeSettings = settings.Clone();
            _overloadedSamples = 0;

            // An open room already passed this check; a fresh start has not.
            if (!reuseOpenRoom && RefuseUntrustedNetwork(_activeSettings) is { } refused) return refused;

            Publish(ShareSessionState.Preparing, "Checking FFmpeg and the media server...");

            step?.Report("Locating FFmpeg");
            var ffmpeg = await _toolProvider.EnsureFfmpegAsync(null, ct);
            if (!ffmpeg.IsAvailable)
            {
                return await EndFailedShareStartAsync(
                    reuseOpenRoom,
                    "FFmpeg could not be found or downloaded. Check the network connection and try again.");
            }
            _ffmpegPath = ffmpeg.Path;

            step?.Report("Locating the media server");
            var mediaMtx = await _toolProvider.EnsureMediaMtxAsync(null, ct);
            if (!mediaMtx.IsAvailable)
            {
                return await EndFailedShareStartAsync(
                    reuseOpenRoom,
                    "The media server could not be downloaded. Check the network connection and try again.");
            }

            step?.Report("Checking hardware encoders");
            var encoders = await _encoderProbe.ProbeAsync(_ffmpegPath, ct);
            var encoder = _encoderProbe.SelectBest(encoders, _activeSettings.Codec, _activeSettings.PreferredEncoder);
            if (encoder == null)
            {
                return await EndFailedShareStartAsync(reuseOpenRoom, "No usable video encoder was found on this PC.");
            }

            if (!encoder.IsHardware)
            {
                _logService.LogInfo("[ShareScreen] No hardware encoder is available; falling back to software.");
            }

            if (encoder.Codec != ShareVideoCodec.H264)
            {
                // Viewers watch over WebRTC, where H.264 is the only codec every browser can
                // decode. HEVC plays in Safari and nowhere else; AV1 support is patchy. There is
                // no negotiation here to fall back for us, so say plainly what will happen.
                _logService.LogWarning(
                    $"[ShareScreen] Sharing as {encoder.Codec}. Most browsers cannot play this over " +
                    "WebRTC — viewers may see a black player. H.264 is the compatible choice.");
            }

            var display = ResolveDisplay(_activeSettings.DisplayIndex);
            var preset = QualityPreset.For(_activeSettings.Quality);
            var room = openRoom ?? ShareRoomFactory.Create();

            Publish(ShareSessionState.Starting, "Starting the media server...", room, preset, encoder);

            // Our own viewer page, not the media server's: control needs a channel back, and the
            // server has none. It also means the page can carry the warning a controller sees.
            // It starts first because the media server asks it whether each publish and read
            // may go ahead.
            if (!reuseOpenRoom)
            {
                var viewerHostName = MediaMtxServer.ResolveViewerHost(_activeSettings.ViewerHostOverride);
                _roomTls = await _tlsProvider.TryGetAsync(viewerHostName, ct);
                _viewerHost.Start(room, _roomTls?.HostName ?? viewerHostName, _activeSettings.WebrtcPort, _activeSettings.ViewerPort, _roomTls?.Certificate);
            }

            step?.Report("Starting the media server");
            await _server.StartAsync(mediaMtx.Path, room, _activeSettings, _viewerHost.MediaAuthAddress, ct, _roomTls);

            step?.Report("Starting the encoder");
            _pipeline.Start(_ffmpegPath, display, preset, encoder, _activeSettings, room, _audioPump);
            _controlTarget = display;

            var viewerUrl = _viewerHost.ViewerUrl;

            Status = Status with
            {
                State = ShareSessionState.Live,
                Message = "Live",
                Room = room,
                ViewerUrl = viewerUrl,
                Quality = preset,
                Encoder = encoder,
                // Every session starts with control off, whatever the last one did.
                Control = RemoteControlState.Disabled,
                ControlCode = string.Empty
            };
            RaiseStatusChanged();

            StartMonitoring();
            _logService.LogInfo($"[ShareScreen] Live at {preset.ShortLabel} using {encoder.DisplayName}.");
            return Status;
        }
        catch (OperationCanceledException)
        {
            return await EndFailedShareStartAsync(reuseOpenRoom, string.Empty, cancelled: true);
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not start sharing", ex);
            return await EndFailedShareStartAsync(reuseOpenRoom, ex.Message);
        }
        finally
        {
            _transition.Release();
        }
    }

    private CaptureDisplay ResolveDisplay(int requestedIndex)
    {
        var displays = _deviceProvider.GetDisplays();

        // A monitor that was unplugged since the last session must not take the whole feature down.
        return displays.FirstOrDefault(d => d.OutputIndex == requestedIndex)
               ?? displays.FirstOrDefault(d => d.IsPrimary)
               ?? displays[0];
    }

    // -- Monitoring ----------------------------------------------------------

    private void StartMonitoring()
    {
        _monitorCts?.Cancel();
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;

        _lastBytesReceived = 0;
        _lastStatsAt = DateTime.UtcNow;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, token);

                    var room = Status.Room;
                    if (room == null) continue;

                    var stats = await _server.GetPathStatsAsync(room.PathName, token);
                    ApplyPathStats(stats);
                    UpdateVideoViewers(await _server.GetWebRtcViewersAsync(room.PathName, token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logService.LogError("[ShareScreen] Stats poll failed", ex);
                }
            }
        }, token);
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private long _lastBytesReceived;
    private DateTime _lastStatsAt;

    /// <summary>
    /// Folds the router's counters into the telemetry. Bitrate is measured from the change in
    /// bytes received over real elapsed time, so it reflects what is actually going out.
    /// </summary>
    private void ApplyPathStats(SharePathStats stats)
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _lastStatsAt).TotalSeconds;
        _lastStatsAt = now;

        var bitrateMbps = Status.Telemetry.BitrateMbps;
        if (seconds > 0.1 && stats.BytesReceived >= _lastBytesReceived && _lastBytesReceived > 0)
        {
            var deltaBytes = stats.BytesReceived - _lastBytesReceived;
            bitrateMbps = deltaBytes * 8.0 / seconds / 1_000_000.0;
        }
        _lastBytesReceived = stats.BytesReceived;

        Status = Status with
        {
            Telemetry = Status.Telemetry with
            {
                ViewerCount = stats.Readers,
                BytesSent = stats.BytesReceived,
                BitrateMbps = bitrateMbps
            }
        };
        RaiseStatusChanged();
    }

    private void OnTelemetryUpdated(object? sender, ShareTelemetry telemetry)
    {
        if (Status.State != ShareSessionState.Live) return;

        // FFmpeg owns frame rate and drops; the router owns viewers, bytes and bitrate, because
        // the RTSP muxer reports "N/A" for those. Keep each source's fields.
        Status = Status with
        {
            Telemetry = telemetry with
            {
                ViewerCount = Status.Telemetry.ViewerCount,
                BitrateMbps = Status.Telemetry.BitrateMbps,
                BytesSent = Status.Telemetry.BytesSent
            }
        };
        RaiseStatusChanged();

        if (_activeSettings.AdaptiveQuality) ConsiderSteppingDown(telemetry);
    }

    /// <summary>
    /// Drops a rung when the encoder has been behind for several seconds running. Resolution goes
    /// before frame rate, and a single spike is ignored, because destroying the picture over one
    /// bad sample is worse than the sample.
    /// </summary>
    private void ConsiderSteppingDown(ShareTelemetry telemetry)
    {
        if (!telemetry.IsEncoderOverloaded)
        {
            _overloadedSamples = 0;
            return;
        }

        // FFmpeg reports about twice a second, so this is roughly four seconds of trouble.
        if (++_overloadedSamples < 8) return;

        var lower = Status.Quality.StepDown();
        if (lower == null) return;

        _overloadedSamples = 0;
        _logService.LogInfo(
            $"[ShareScreen] Encoder is behind (speed {telemetry.Speed:0.00}x); " +
            $"stepping {Status.Quality.ShortLabel} down to {lower.ShortLabel}.");

        _ = RestartAtAsync(lower);
    }

    /// <summary>Restarts the encoder at a new rung. The router and the viewer link survive, so
    /// viewers see a brief reconnect rather than a dead link.</summary>
    private async Task RestartAtAsync(QualityPreset preset)
    {
        await _transition.WaitAsync();
        try
        {
            if (Status.State != ShareSessionState.Live || Status.Room == null || Status.Encoder == null) return;

            _activeSettings.Quality = preset.Quality;
            _pipeline.Stop();

            var display = ResolveDisplay(_activeSettings.DisplayIndex);
            _pipeline.Start(_ffmpegPath, display, preset, Status.Encoder, _activeSettings, Status.Room, _audioPump);

            Status = Status with { Quality = preset, Message = $"Live - stepped down to {preset.ShortLabel}" };
            RaiseStatusChanged();
        }
        catch (Exception ex)
        {
            _logService.LogError("[ShareScreen] Could not step the quality down", ex);
        }
        finally
        {
            _transition.Release();
        }
    }

    private void OnPipelineFailed(object? sender, string reason)
    {
        if (Status.State is ShareSessionState.Idle or ShareSessionState.Stopping) return;

        _logService.LogError($"[ShareScreen] The encoder stopped: {reason}");
        _ = Task.Run(async () =>
        {
            await StopInternalAsync();
            Fault(reason);
        });
    }

    // -- Stop ----------------------------------------------------------------

    public async Task StopScreenShareAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (Status.State != ShareSessionState.Live) return;
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
            _pipeline.Stop();
            _server.Stop();
            _viewerHost.DisableControl();
            _controlTarget = null;
            Status = Status with
            {
                State = ShareSessionState.RoomOpen,
                Message = "Private room open",
                Encoder = null,
                Telemetry = ShareTelemetry.Empty,
                Control = RemoteControlState.Disabled,
                ControlCode = string.Empty
            };
            RaiseStatusChanged();
            await Task.Delay(100);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (Status.State == ShareSessionState.Idle) return;

            Publish(ShareSessionState.Stopping, "Stopping...");
            await StopInternalAsync();
            Publish(ShareSessionState.Idle, string.Empty);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        _pipeline.Stop();
        _viewerHost.Stop();      // also revokes control and releases anything held down
        _server.Stop();
        _controlTarget = null;
        UpdateVideoViewers(Array.Empty<ShareParticipant>());
        _roomTls = null;

        // Give the OS a moment to release the ports before a restart tries to bind them again.
        await Task.Delay(150);
    }

    private async Task<ShareSessionStatus> EndFailedShareStartAsync(
        bool keepRoomOpen,
        string message,
        bool cancelled = false)
    {
        if (!keepRoomOpen)
        {
            await StopInternalAsync();
            if (cancelled)
            {
                Publish(ShareSessionState.Idle, string.Empty);
                return Status;
            }

            return Fault(message);
        }

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _pipeline.Stop();
        _server.Stop();
        _viewerHost.DisableControl();
        _controlTarget = null;
        Status = Status with
        {
            State = ShareSessionState.RoomOpen,
            Message = cancelled || string.IsNullOrWhiteSpace(message)
                ? "Private room open"
                : $"Private room open - screen share failed: {message}",
            Encoder = null,
            Telemetry = ShareTelemetry.Empty,
            Control = RemoteControlState.Disabled,
            ControlCode = string.Empty
        };
        RaiseStatusChanged();
        await Task.Delay(100);
        return Status;
    }

    // -- Status plumbing -----------------------------------------------------

    private void Publish(
        ShareSessionState state,
        string message,
        ShareRoom? room = null,
        QualityPreset? preset = null,
        ShareEncoder? encoder = null)
    {
        Status = new ShareSessionStatus(
            state,
            message,
            room ?? (state == ShareSessionState.Idle ? null : Status.Room),
            state == ShareSessionState.Idle ? string.Empty : Status.ViewerUrl,
            preset ?? Status.Quality,
            encoder ?? Status.Encoder,
            state is ShareSessionState.Idle or ShareSessionState.Faulted
                ? ShareTelemetry.Empty
                : Status.Telemetry);

        RaiseStatusChanged();
    }

    private ShareSessionStatus Fault(string message)
    {
        Publish(ShareSessionState.Faulted, message);
        return Status;
    }

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, Status);

    // -- Remote control ------------------------------------------------------

    private CaptureDisplay? _controlTarget;

    public string? SetRemoteControl(bool enabled)
    {
        if (Status.State != ShareSessionState.Live || _controlTarget == null) return null;

        if (!enabled)
        {
            _viewerHost.DisableControl();
            Status = Status with { Control = RemoteControlState.Disabled, ControlCode = string.Empty };
            RaiseStatusChanged();
            return null;
        }

        var code = _viewerHost.EnableControl(_controlTarget);
        Status = Status with { Control = RemoteControlState.Offered, ControlCode = code };
        RaiseStatusChanged();
        return code;
    }

    private void OnControlDisabledAfterFailedAttempts(object? sender, EventArgs e)
    {
        Status = Status with
        {
            Control = RemoteControlState.Disabled,
            ControlCode = string.Empty,
            Message = "Remote control was switched off after repeated wrong codes."
        };
        RaiseStatusChanged();
    }

    private void OnControlHolderChanged(object? sender, bool taken)
    {
        if (Status.Control == RemoteControlState.Disabled) return;

        Status = Status with { Control = taken ? RemoteControlState.Active : RemoteControlState.Offered };
        RaiseStatusChanged();
    }

    // -- Control approval ----------------------------------------------------

    public event EventHandler<PendingControlApproval>? ControlApprovalRequested;

    private async Task<bool> AskHostToApproveAsync(ControlApprovalRequest request, CancellationToken ct)
    {
        var handler = ControlApprovalRequested;
        if (handler == null) return false;

        var pending = new PendingControlApproval(request);
        await using var lapse = ct.Register(pending.Deny);
        handler(this, pending);
        return await pending.Answer;
    }

    // -- Untrusted networks --------------------------------------------------

    /// <summary>
    /// Refuses to open the room on a network Windows calls Public or cannot classify, unless the
    /// user allowed it for this session. Returns the refused status, or null to go ahead.
    /// </summary>
    private ShareSessionStatus? RefuseUntrustedNetwork(ShareScreenSettings settings)
    {
        var viewerHost = MediaMtxServer.ResolveViewerHost(settings.ViewerHostOverride);
        var classification = _networkClassifier(ResolveToLocalAddress(viewerHost));
        if (!classification.IsPublicOrUnclassified) return null;

        if (settings.AllowUntrustedNetwork)
        {
            _logService.LogWarning($"[ShareScreen] Opening the room on an untrusted network because the user allowed it for this session: {classification.WarningMessage}");
            return null;
        }

        _logService.LogWarning($"[ShareScreen] Refusing to open the room on an untrusted network: {classification.WarningMessage}");
        Status = ShareSessionStatus.Idle with
        {
            Message = classification.WarningMessage ?? "This network is Public or could not be classified.",
            UntrustedNetworkRefused = true
        };
        RaiseStatusChanged();
        return Status;
    }

    /// <summary>A host name (such as a Tailscale MagicDNS name) mapped to this PC's address for it.</summary>
    private static string ResolveToLocalAddress(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try
        {
            return Dns.GetHostAddresses(host)
                       .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && NetworkProfileClassifier.IsLocalAddress(a))
                       ?.ToString()
                   ?? host;
        }
        catch
        {
            // Name lookup failed: the host is used as written, and the trust check then judges it.
            return host;
        }
    }

    // -- Participants --------------------------------------------------------

    public event EventHandler? ParticipantsChanged;

    /// <summary>Everyone in the room: chat, voice and control connections, and video-only viewers.</summary>
    public IReadOnlyList<ShareParticipant> GetParticipants() =>
        _viewerHost.GetActiveParticipants().Concat(_videoViewers).OrderBy(p => p.ConnectedAt).ToList();

    private void OnHostParticipantsChanged(object? sender, EventArgs e) => ParticipantsChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateVideoViewers(IReadOnlyList<ShareParticipant> viewers)
    {
        var before = string.Join(",", _videoViewers.Select(v => v.Id));
        var after = string.Join(",", viewers.Select(v => v.Id));
        _videoViewers = viewers;
        if (before != after) ParticipantsChanged?.Invoke(this, EventArgs.Empty);
    }

    // -- Room chat -----------------------------------------------------------

    public event EventHandler<ShareChatMessage>? ChatMessageReceived;

    private void OnChatMessageReceived(object? sender, ShareChatMessage msg) => ChatMessageReceived?.Invoke(this, msg);

    public void SendChatMessage(ShareChatMessage message) => _viewerHost.BroadcastChatMessage(message);

    public event EventHandler<byte[]>? VoicePacketReceived;

    private void OnVoicePacketReceived(object? sender, byte[] packet) => VoicePacketReceived?.Invoke(this, packet);

    public void SendVoicePacket(byte[] opusPacket) => _viewerHost.BroadcastVoicePacket(opusPacket);

    public void Dispose()
    {
        _viewerHost.ControlHolderChanged -= OnControlHolderChanged;
        _viewerHost.ControlDisabledAfterFailedAttempts -= OnControlDisabledAfterFailedAttempts;
        _viewerHost.ParticipantsChanged -= OnHostParticipantsChanged;
        _viewerHost.ApproveControl = null;
        _viewerHost.ChatMessageReceived -= OnChatMessageReceived;
        _viewerHost.VoicePacketReceived -= OnVoicePacketReceived;
        _viewerHost.Dispose();
        _pipeline.TelemetryUpdated -= OnTelemetryUpdated;
        _pipeline.Failed -= OnPipelineFailed;

        _monitorCts?.Cancel();
        _monitorCts?.Dispose();

        _pipeline.Dispose();
        _audioPump.Dispose();
        _server.Dispose();
        _transition.Dispose();
    }
}
