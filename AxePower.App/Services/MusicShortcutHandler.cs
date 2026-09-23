using System;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.App.Services;

/// <summary>
/// Turns a pressed shortcut into a player action. Kept apart from <see cref="HotkeyService"/>,
/// which only knows about Windows and key codes and nothing about music.
/// </summary>
public sealed class MusicShortcutHandler : IDisposable
{
    private readonly HotkeyService _hotkeys;
    private readonly IMusicPlayerService _player;
    private readonly DiscoveryService _discovery;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private bool _started;

    public MusicShortcutHandler(
        HotkeyService hotkeys,
        IMusicPlayerService player,
        DiscoveryService discovery,
        SettingsService settings,
        LogService log)
    {
        _hotkeys = hotkeys;
        _player = player;
        _discovery = discovery;
        _settings = settings;
        _log = log;
    }

    /// <summary>Raised for shortcuts only the window can act on, such as the mini player.</summary>
    public event EventHandler<ShortcutAction>? WindowActionRequested;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _hotkeys.Pressed += OnPressed;
    }

    private void OnPressed(object? sender, ShortcutAction action) => _ = HandleAsync(action);

    private async Task HandleAsync(ShortcutAction action)
    {
        var shortcuts = _settings.GetSettings().Music.Shortcuts ?? new ShortcutSettings();
        var state = _player.State;

        try
        {
            switch (action)
            {
                case ShortcutAction.PlayPause:
                    await _player.TogglePlayPauseAsync();
                    break;

                case ShortcutAction.NextTrack:
                    await _player.NextAsync();
                    break;

                case ShortcutAction.PreviousTrack:
                    await _player.PreviousAsync();
                    break;

                case ShortcutAction.SeekForward:
                case ShortcutAction.SeekBackward:
                {
                    if (state.Current is null) return;

                    int direction = action == ShortcutAction.SeekForward ? 1 : -1;
                    var target = state.Position + TimeSpan.FromSeconds(direction * Math.Max(1, shortcuts.SeekSeconds));

                    // Seeking past either end is clamped rather than ignored, so holding the
                    // key does something sensible at the start and the end of a track.
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    if (state.Duration > TimeSpan.Zero && target > state.Duration) target = state.Duration;

                    await _player.SeekAsync(target);
                    break;
                }

                case ShortcutAction.VolumeUp:
                case ShortcutAction.VolumeDown:
                {
                    double step = Math.Max(1, shortcuts.VolumeStepPercent) / 100.0;
                    double volume = state.Volume + (action == ShortcutAction.VolumeUp ? step : -step);
                    double ceiling = _settings.GetSettings().Music.AllowVolumeBoost ? 2.0 : 1.0;

                    _player.SetVolume(Math.Clamp(volume, 0, ceiling));
                    break;
                }

                case ShortcutAction.ToggleMute:
                    _player.ToggleMute();
                    break;

                case ShortcutAction.ToggleShuffle:
                    _player.SetShuffle(!state.Shuffle);
                    break;

                case ShortcutAction.CycleRepeat:
                    _player.SetRepeat(state.Repeat switch
                    {
                        RepeatMode.Off => RepeatMode.All,
                        RepeatMode.All => RepeatMode.One,
                        _ => RepeatMode.Off
                    });
                    break;

                case ShortcutAction.ToggleDiscovery:
                    _discovery.SetEnabled(!_discovery.IsEnabled);
                    break;

                case ShortcutAction.ToggleMiniPlayer:
                    WindowActionRequested?.Invoke(this, action);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Shortcut '{ShortcutSettings.Describe(action)}' failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_started) _hotkeys.Pressed -= OnPressed;
    }
}
