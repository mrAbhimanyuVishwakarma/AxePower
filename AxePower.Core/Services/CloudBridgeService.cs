using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;
using AxePower.Core.Services.Accounts;
using AxePower.Core.Services.Music;

namespace AxePower.Core.Services;

/// <summary>
/// Cloud Bridge service connecting AxePower PC to the user's private Supabase Realtime channel.
/// Enables secure, remote mobile access from anywhere in the world without port forwarding or local network restrictions.
/// Synchronizes system telemetry, power profiles, battery metrics, and music playback with AxePower Mobile.
/// Automatically suspends background streaming and sensor queries when disabled to conserve CPU and laptop battery.
/// </summary>
public sealed class CloudBridgeService : IDisposable
{
    private static readonly Uri SupabaseRealtimeEndpoint = new(
        "wss://kjnqsgdlcytpooueymdt.supabase.co/realtime/v1/websocket?apikey=sb_publishable__WFKuy8jF5iwSMH0dgTikw_Hv864_Pp&vsn=1.0.0");

    private readonly AccountService _accountService;
    private readonly SystemTelemetryService _telemetryService;
    private readonly BatteryService _batteryService;
    private readonly PowerPlanService _powerPlanService;
    private readonly SettingsService _settingsService;
    private readonly IMusicPlayerService? _musicPlayer;
    private readonly LogService _log;

    private CancellationTokenSource? _serviceCts;
    private Task? _bridgeTask;
    private ClientWebSocket? _webSocket;
    private int _messageRef;

    private SystemTelemetrySnapshot? _latestTelemetry;
    private readonly List<string> _activeTopics = new();
    private bool _isDisposed;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public CloudBridgeService(
        AccountService accountService,
        SystemTelemetryService telemetryService,
        BatteryService batteryService,
        PowerPlanService powerPlanService,
        SettingsService settingsService,
        LogService log,
        IMusicPlayerService? musicPlayer = null)
    {
        _accountService = accountService;
        _telemetryService = telemetryService;
        _batteryService = batteryService;
        _powerPlanService = powerPlanService;
        _settingsService = settingsService;
        _log = log;
        _musicPlayer = musicPlayer;

        _telemetryService.TelemetryUpdated += OnTelemetryUpdated;
        _accountService.StateChanged += OnAccountStateChanged;
        _settingsService.OnSettingsChanged += OnSettingsChanged;

        if (_musicPlayer != null)
        {
            _musicPlayer.StateChanged += OnMusicStateChanged;
            _musicPlayer.PositionChanged += OnMusicPositionChanged;
        }

        Start();
    }

    private void Start()
    {
        if (_bridgeTask != null || _isDisposed) return;
        _serviceCts = new CancellationTokenSource();
        _bridgeTask = Task.Run(() => BridgeWorkerLoopAsync(_serviceCts.Token));
    }

    private void OnTelemetryUpdated(SystemTelemetrySnapshot snapshot)
    {
        _latestTelemetry = snapshot;
    }

    private void OnAccountStateChanged(object? sender, AccountState state)
    {
        Restart();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!settings.RemoteAccess.Enabled && IsConnected)
        {
            _ = DisconnectSocketAsync();
        }
    }

    private void OnMusicStateChanged(object? sender, PlayerState state)
    {
        if (!IsConnected || _activeTopics.Count == 0) return;
        _ = BroadcastMusicStateToAllTopicsAsync();
    }

    private void OnMusicPositionChanged(object? sender, TimeSpan pos)
    {
        // Periodic position sync is handled in the broadcast loop or on state changes
    }

    public void Restart()
    {
        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _serviceCts = new CancellationTokenSource();
        _bridgeTask = Task.Run(() => BridgeWorkerLoopAsync(_serviceCts.Token));
    }

    private async Task BridgeWorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_isDisposed)
        {
            var accountState = _accountService.State;
            var settings = _settingsService.GetSettings();

            if (accountState.Status != AccountStatus.SignedIn ||
                string.IsNullOrEmpty(accountState.UserId) ||
                !settings.RemoteAccess.Enabled)
            {
                // Standby mode: wait 3 seconds before checking again (0% resource usage)
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            var userId = accountState.UserId;
            var topics = new List<string> { $"realtime:axepower:user_{userId}" };

            // Also join by clean username so mobile can pair instantly with @username
            if (!string.IsNullOrWhiteSpace(accountState.Profile?.Username))
            {
                var usernameTopic = $"realtime:axepower:user_{accountState.Profile.Username.Trim().ToLowerInvariant().TrimStart('@')}";
                if (!topics.Contains(usernameTopic)) topics.Add(usernameTopic);
            }

            // Also join by email prefix
            if (!string.IsNullOrWhiteSpace(accountState.Email))
            {
                var emailPrefix = accountState.Email.Split('@')[0].Trim().ToLowerInvariant();
                var emailTopic = $"realtime:axepower:user_{emailPrefix}";
                if (!topics.Contains(emailTopic)) topics.Add(emailTopic);
            }

            lock (_activeTopics)
            {
                _activeTopics.Clear();
                _activeTopics.AddRange(topics);
            }

            try
            {
                _log.LogInfo($"[CloudBridge] Connecting to Supabase Realtime channel {topics[0]}...");
                using var ws = new ClientWebSocket();
                _webSocket = ws;

                await ws.ConnectAsync(SupabaseRealtimeEndpoint, ct);
                _log.LogInfo("[CloudBridge] WebSocket connected to Supabase.");

                // 1. Join all matching user topics
                foreach (var topic in topics)
                {
                    await SendPhoenixMessageAsync(ws, topic, "phx_join", new JsonObject(), ct);
                }

                // 2. Announce PC presence & initial music state
                await BroadcastDeviceStateToAllTopicsAsync(ws, ct);
                await BroadcastMusicStateToAllTopicsAsync();

                // Run concurrent receive and telemetry broadcast loops
                var receiveLoop = ReceiveLoopAsync(ws, ct);
                var sendLoop = TelemetryBroadcastLoopAsync(ws, ct);
                var heartbeatLoop = HeartbeatLoopAsync(ws, ct);

                await Task.WhenAny(receiveLoop, sendLoop, heartbeatLoop);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[CloudBridge] Connection dropped: {ex.Message}. Reconnecting in 5s...");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                await DisconnectSocketAsync();
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(25000, ct);
            await SendPhoenixMessageAsync(ws, "phoenix", "heartbeat", new JsonObject(), ct);
        }
    }

    private async Task TelemetryBroadcastLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var settings = _settingsService.GetSettings();
            if (!settings.RemoteAccess.Enabled)
            {
                await Task.Delay(2000, ct);
                continue;
            }

            if (settings.RemoteAccess.AllowTelemetry || settings.RemoteAccess.AllowBatterySync)
            {
                var payload = BuildTelemetryPayload(settings);
                await BroadcastToAllTopicsAsync(ws, "telemetry_frame", payload, ct);
            }

            // Sync music position every 2 seconds if playing
            tick++;
            if (tick % 2 == 0 && _musicPlayer?.State.Playing == true)
            {
                await BroadcastMusicStateToAllTopicsAsync();
            }

            // Stream at 1Hz rolling rate while active
            await Task.Delay(1000, ct);
        }
    }

    private JsonObject BuildTelemetryPayload(AppSettings settings)
    {
        var payload = new JsonObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["deviceName"] = Environment.MachineName,
            ["activeMode"] = settings.LastSelectedPowerMode.ToString()
        };

        if (settings.RemoteAccess.AllowTelemetry && _latestTelemetry != null)
        {
            payload["cpuLoad"] = _latestTelemetry.CpuPercent;
            payload["gpuLoad"] = _latestTelemetry.GpuPercent;
            payload["ramUsedPercent"] = _latestTelemetry.MemoryPercent;
            payload["cpuTempC"] = double.IsNaN(_latestTelemetry.CpuTempC) ? 0 : _latestTelemetry.CpuTempC;
            payload["gpuTempC"] = double.IsNaN(_latestTelemetry.GpuTempC) ? 0 : _latestTelemetry.GpuTempC;
            payload["isDedicatedGpu"] = _latestTelemetry.IsDedicatedGpuActive;
        }

        if (settings.RemoteAccess.AllowBatterySync)
        {
            var bat = _batteryService.GetCurrentStatus();
            payload["hasBattery"] = bat.HasBattery;
            payload["batteryPercent"] = bat.BatteryPercentage;
            payload["isPluggedIn"] = bat.IsPluggedIn;
            payload["isCharging"] = bat.IsCharging;
            payload["healthPercentage"] = bat.HealthPercentage;
            payload["conservationMode"] = settings.ConservationModeEnabled;
            payload["rapidCharge"] = settings.RapidChargeEnabled;
        }

        return payload;
    }

    private async Task BroadcastDeviceStateToAllTopicsAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var settings = _settingsService.GetSettings();
        var state = new JsonObject
        {
            ["deviceName"] = Environment.MachineName,
            ["platform"] = "windows",
            ["activeMode"] = settings.LastSelectedPowerMode.ToString(),
            ["remoteSettings"] = JsonSerializer.SerializeToNode(settings.RemoteAccess)
        };
        await BroadcastToAllTopicsAsync(ws, "device_online", state, ct);
    }

    public async Task BroadcastMusicStateToAllTopicsAsync()
    {
        var ws = _webSocket;
        if (ws == null || ws.State != WebSocketState.Open || _musicPlayer == null) return;

        try
        {
            var state = _musicPlayer.State;
            var currentTrack = state.Current?.Track;
            var payload = new JsonObject
            {
                ["title"] = currentTrack?.Title ?? (state.Playing ? "Playing" : "No Track Playing"),
                ["artist"] = currentTrack?.Artists != null && currentTrack.Artists.Count > 0 ? string.Join(", ", currentTrack.Artists) : "AxePower Music",
                ["album"] = currentTrack?.Album ?? string.Empty,
                ["durationSeconds"] = state.Duration.TotalSeconds,
                ["positionSeconds"] = state.Position.TotalSeconds,
                ["isPlaying"] = state.Playing,
                ["volume"] = state.Volume
            };

            await BroadcastToAllTopicsAsync(ws, "music_state", payload, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or IOException)
        {
            // Best-effort: the next playback change sends the state again, and the bridge loop reconnects a dead socket.
        }
    }

    private async Task BroadcastToAllTopicsAsync(ClientWebSocket ws, string eventName, JsonObject payload, CancellationToken ct)
    {
        List<string> topicsCopy;
        lock (_activeTopics)
        {
            topicsCopy = new List<string>(_activeTopics);
        }

        foreach (var topic in topicsCopy)
        {
            await BroadcastChannelEventAsync(ws, topic, eventName, payload, ct);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult res;
            ms.SetLength(0);

            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (res.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            HandleIncomingMessage(json, ws);
        }
    }

    private void HandleIncomingMessage(string json, ClientWebSocket ws)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return;

            var evt = obj["event"]?.ToString();
            var payload = obj["payload"] as JsonObject;

            if (evt == "broadcast" && payload != null)
            {
                var innerEvent = payload["event"]?.ToString();
                var innerPayload = payload["payload"] as JsonObject;

                switch (innerEvent)
                {
                    case "set_mode":
                        HandleSetMode(innerPayload);
                        break;
                    case "set_battery_tuning":
                        HandleSetBatteryTuning(innerPayload);
                        break;
                    case "update_settings":
                        HandleUpdateSettings(innerPayload);
                        break;
                    case "music_play":
                        if (_musicPlayer != null) _ = _musicPlayer.PlayAsync();
                        break;
                    case "music_pause":
                        if (_musicPlayer != null) _ = _musicPlayer.PauseAsync();
                        break;
                    case "music_toggle":
                        if (_musicPlayer != null) _ = _musicPlayer.TogglePlayPauseAsync();
                        break;
                    case "music_next":
                        if (_musicPlayer != null) _ = _musicPlayer.NextAsync();
                        break;
                    case "music_prev":
                        if (_musicPlayer != null) _ = _musicPlayer.PreviousAsync();
                        break;
                    case "music_seek":
                        if (_musicPlayer != null && innerPayload?["positionSeconds"] != null)
                        {
                            double pos = innerPayload["positionSeconds"]!.GetValue<double>();
                            _ = _musicPlayer.SeekAsync(TimeSpan.FromSeconds(pos));
                        }
                        break;
                    case "music_volume":
                        if (_musicPlayer != null && innerPayload?["volume"] != null)
                        {
                            double vol = innerPayload["volume"]!.GetValue<double>();
                            _musicPlayer.SetVolume(vol);
                        }
                        break;
                    case "music_get_state":
                        _ = BroadcastMusicStateToAllTopicsAsync();
                        break;
                    case "ping":
                        _ = BroadcastDeviceStateToAllTopicsAsync(ws, CancellationToken.None);
                        _ = BroadcastMusicStateToAllTopicsAsync();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[CloudBridge] Error parsing incoming message: {ex.Message}");
        }
    }

    private void HandleSetMode(JsonObject? payload)
    {
        var settings = _settingsService.GetSettings();
        if (!settings.RemoteAccess.AllowPowerModeControl) return;

        var modeStr = payload?["mode"]?.ToString();
        if (!string.IsNullOrEmpty(modeStr) && Enum.TryParse<PowerMode>(modeStr, true, out var mode))
        {
            _log.LogInfo($"[CloudBridge] Remote power mode change requested: {mode}");
            _ = _powerPlanService.ApplyModeAsync(mode);
            settings.LastSelectedPowerMode = mode;
            _settingsService.SaveSettings(settings);
        }
    }

    private void HandleSetBatteryTuning(JsonObject? payload)
    {
        var settings = _settingsService.GetSettings();
        if (!settings.RemoteAccess.AllowBatteryTuning) return;

        if (payload?["conservationMode"] != null)
        {
            bool val = payload["conservationMode"]!.GetValue<bool>();
            _batteryService.SetConservationMode(val);
            settings.ConservationModeEnabled = val;
        }

        if (payload?["rapidCharge"] != null)
        {
            bool val = payload["rapidCharge"]!.GetValue<bool>();
            _batteryService.SetRapidCharge(val);
            settings.RapidChargeEnabled = val;
        }

        _settingsService.SaveSettings(settings);
    }

    private void HandleUpdateSettings(JsonObject? payload)
    {
        if (payload == null) return;
        var settings = _settingsService.GetSettings();

        if (payload["allowTelemetry"] != null)
            settings.RemoteAccess.AllowTelemetry = payload["allowTelemetry"]!.GetValue<bool>();

        if (payload["allowBatterySync"] != null)
            settings.RemoteAccess.AllowBatterySync = payload["allowBatterySync"]!.GetValue<bool>();

        if (payload["allowPowerModeControl"] != null)
            settings.RemoteAccess.AllowPowerModeControl = payload["allowPowerModeControl"]!.GetValue<bool>();

        if (payload["allowBatteryTuning"] != null)
            settings.RemoteAccess.AllowBatteryTuning = payload["allowBatteryTuning"]!.GetValue<bool>();

        _settingsService.SaveSettings(settings);
        _log.LogInfo("[CloudBridge] Remote settings updated and synchronized with mobile.");
    }

    private async Task BroadcastChannelEventAsync(ClientWebSocket ws, string topic, string eventName, JsonObject payload, CancellationToken ct)
    {
        var broadcastPayload = new JsonObject
        {
            ["type"] = "broadcast",
            ["event"] = eventName,
            ["payload"] = payload
        };
        await SendPhoenixMessageAsync(ws, topic, "broadcast", broadcastPayload, ct);
    }

    private async Task SendPhoenixMessageAsync(ClientWebSocket ws, string topic, string eventName, JsonObject payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;

        int refId = Interlocked.Increment(ref _messageRef);
        var msg = new JsonObject
        {
            ["topic"] = topic,
            ["event"] = eventName,
            ["payload"] = payload,
            ["ref"] = refId.ToString()
        };

        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task DisconnectSocketAsync()
    {
        try
        {
            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.Connecting)
                {
                    using var cts = new CancellationTokenSource(1500);
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
                }
                _webSocket.Dispose();
                _webSocket = null;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or IOException)
        {
            // Best-effort teardown of the bridge socket; it is discarded either way.
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _telemetryService.TelemetryUpdated -= OnTelemetryUpdated;
        _accountService.StateChanged -= OnAccountStateChanged;
        _settingsService.OnSettingsChanged -= OnSettingsChanged;

        if (_musicPlayer != null)
        {
            _musicPlayer.StateChanged -= OnMusicStateChanged;
            _musicPlayer.PositionChanged -= OnMusicPositionChanged;
        }

        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _ = DisconnectSocketAsync();
    }
}
