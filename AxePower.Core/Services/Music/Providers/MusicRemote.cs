using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The phone remote's view of the player: a JSON snapshot, and the commands it can send.
///
/// It lives apart from the LAN portal so the portal stays a transport and knows nothing about
/// music, and so every route below can be tested without opening a socket.
/// </summary>
public sealed class MusicRemote
{
    /// <summary>The phone shows a list, not the whole queue; a huge one is pointless to send.</summary>
    private const int MaxQueueItems = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IMusicPlayerService _player;
    private readonly DiscoveryService? _discovery;
    private readonly LogService _log;

    public MusicRemote(IMusicPlayerService player, LogService log, DiscoveryService? discovery = null)
    {
        _player = player;
        _log = log;
        _discovery = discovery;
    }

    /// <summary>Raised whenever the state the remote shows has changed, for the event stream.</summary>
    public event EventHandler? Changed;

    private bool _subscribed;

    public void Start()
    {
        if (_subscribed) return;
        _subscribed = true;
        _player.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The current state as JSON. Safe to call from any thread.</summary>
    public string StateJson()
    {
        var state = _player.State;
        var current = state.Current;

        var payload = new
        {
            playing = state.Playing,
            hasCurrent = current is not null,
            title = current?.Track.Title ?? string.Empty,
            artist = current is null ? string.Empty : string.Join(", ", current.Track.Artists),
            album = current?.Track.Album ?? string.Empty,
            artwork = current?.Track.ThumbnailUrl,

            // Seconds rather than a formatted string, so the phone can render its own clock
            // and drive a seek bar without parsing anything.
            position = (int)state.Position.TotalSeconds,
            duration = (int)state.Duration.TotalSeconds,

            volume = (int)Math.Round(state.Volume * 100),
            muted = state.Muted,
            shuffle = state.Shuffle,
            repeat = state.Repeat.ToString().ToLowerInvariant(),
            discovery = _discovery?.IsEnabled ?? false,
            discoveryAvailable = _discovery?.IsAvailable ?? false,
            index = state.Index,
            queueLength = state.Queue.Count,
            error = state.Error,

            queue = state.Queue.Take(MaxQueueItems).Select((item, i) => new
            {
                index = i,
                title = item.Track.Title,
                artist = string.Join(", ", item.Track.Artists),
                duration = (int)item.Track.Duration.TotalSeconds,
                current = i == state.Index
            })
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>
    /// Runs a command from the phone. Returns false for anything unrecognised or out of range,
    /// which the portal turns into a 400 rather than pretending it worked.
    /// </summary>
    public async Task<bool> CommandAsync(string action, string? value, CancellationToken ct)
    {
        try
        {
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "play": await _player.PlayAsync(); return true;
                case "pause": await _player.PauseAsync(); return true;
                case "toggle": await _player.TogglePlayPauseAsync(); return true;
                case "next": await _player.NextAsync(); return true;
                case "previous": await _player.PreviousAsync(); return true;

                case "seek":
                    if (!int.TryParse(value, out int seconds) || seconds < 0) return false;
                    await _player.SeekAsync(TimeSpan.FromSeconds(seconds));
                    return true;

                case "volume":
                    if (!int.TryParse(value, out int percent) || percent is < 0 or > 200) return false;
                    _player.SetVolume(percent / 100.0);
                    return true;

                case "mute": _player.ToggleMute(); return true;

                case "shuffle":
                    _player.SetShuffle(ParseBool(value, !_player.State.Shuffle));
                    return true;

                case "repeat":
                    if (!Enum.TryParse<RepeatMode>(value, ignoreCase: true, out var mode)) return false;
                    _player.SetRepeat(mode);
                    return true;

                case "jump":
                    if (!int.TryParse(value, out int index)) return false;
                    if (index < 0 || index >= _player.State.Queue.Count) return false;
                    await _player.JumpAsync(index);
                    return true;

                case "discovery":
                    if (_discovery is null) return false;
                    _discovery.SetEnabled(ParseBool(value, !_discovery.IsEnabled));
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Remote command '{action}' failed: {ex.Message}");
            return false;
        }
    }

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out bool parsed) ? parsed : fallback;
}
