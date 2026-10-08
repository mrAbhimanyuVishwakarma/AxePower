using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Windows System Media Transport Controls: the media flyout, lock screen and hardware media
/// keys all drive the player through this, so no keyboard hook is needed. Attached to the main
/// window with <c>SystemMediaTransportControlsInterop.GetForWindow</c>.
/// </summary>
public sealed class SmtcIntegration : IPlayerIntegration
{
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);

    private readonly IMusicPlayerService _player;
    private readonly ICoverArtService _covers;
    private readonly LogService _log;

    private SystemMediaTransportControls? _smtc;
    private Guid? _displayedKey;
    private DateTime _lastTimelineUtc;
    private PlayerState _state = PlayerState.Empty;

    public SmtcIntegration(IMusicPlayerService player, ICoverArtService covers, LogService log)
    {
        _player = player;
        _covers = covers;
        _log = log;
    }

    public string Name => "Windows media controls";

    public Task InitializeAsync(IntPtr mainWindowHandle, CancellationToken ct)
    {
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(mainWindowHandle);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = false;
        _smtc.IsPauseEnabled = false;
        _smtc.IsNextEnabled = false;
        _smtc.IsPreviousEnabled = false;
        _smtc.IsStopEnabled = false;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
        _log.LogInfo("[Music] Windows media controls attached (GetForWindow).");
        return Task.CompletedTask;
    }

    public void OnStateChanged(PlayerState state)
    {
        var smtc = _smtc;
        if (smtc == null) return;
        _state = state;

        var current = state.Current;
        bool hasItem = current != null;
        bool canWrap = state.Repeat == RepeatMode.All && state.Queue.Count > 1;

        smtc.IsPlayEnabled = hasItem;
        smtc.IsPauseEnabled = hasItem;
        smtc.IsStopEnabled = hasItem;
        smtc.IsNextEnabled = hasItem && (state.Index < state.Queue.Count - 1 || canWrap);
        smtc.IsPreviousEnabled = hasItem;
        smtc.PlaybackStatus = !hasItem ? MediaPlaybackStatus.Closed
            : state.Playing ? MediaPlaybackStatus.Playing
            : MediaPlaybackStatus.Paused;

        if (current == null)
        {
            if (_displayedKey != null)
            {
                smtc.DisplayUpdater.ClearAll();
                smtc.DisplayUpdater.Update();
                _displayedKey = null;
            }
            return;
        }

        if (_displayedKey != current.Key)
        {
            _displayedKey = current.Key;
            _ = UpdateDisplayAsync(current);
        }

        UpdateTimeline(state.Position);
    }

    public void OnPositionChanged(TimeSpan position)
    {
        if (DateTime.UtcNow - _lastTimelineUtc >= TimelineInterval) UpdateTimeline(position);
    }

    private async Task UpdateDisplayAsync(QueueItem item)
    {
        var smtc = _smtc;
        if (smtc == null) return;

        try
        {
            var track = item.Track;
            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = string.Join(", ", track.Artists);
            updater.MusicProperties.AlbumTitle = track.Album ?? string.Empty;
            updater.MusicProperties.AlbumArtist = track.AlbumArtist ?? string.Empty;
            updater.MusicProperties.TrackNumber = (uint)Math.Max(0, track.TrackNumber ?? 0);
            updater.Thumbnail = null;
            updater.Update();

            var cover = await _covers.GetCoverFileAsync(track, 512, CancellationToken.None);
            if (cover != null && _displayedKey == item.Key)
            {
                var file = await StorageFile.GetFileFromPathAsync(cover);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                updater.Update();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Media controls display update failed: {ex.Message}");
        }
    }

    private void UpdateTimeline(TimeSpan position)
    {
        var smtc = _smtc;
        if (smtc == null || _state.Current == null) return;

        _lastTimelineUtc = DateTime.UtcNow;
        var duration = _state.Duration;
        smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = duration,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = duration,
            Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration && duration > TimeSpan.Zero ? duration : position
        });
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: _ = _player.PlayAsync(); break;
            case SystemMediaTransportControlsButton.Pause: _ = _player.PauseAsync(); break;
            case SystemMediaTransportControlsButton.Stop: _ = _player.PauseAsync(); break;
            case SystemMediaTransportControlsButton.Next: _ = _player.NextAsync(); break;
            case SystemMediaTransportControlsButton.Previous: _ = _player.PreviousAsync(); break;
        }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        _ = _player.SeekAsync(args.RequestedPlaybackPosition);

    public void Dispose()
    {
        var smtc = _smtc;
        if (smtc == null) return;

        try
        {
            smtc.ButtonPressed -= OnButtonPressed;
            smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
            smtc.DisplayUpdater.ClearAll();
            smtc.DisplayUpdater.Update();
            smtc.IsEnabled = false;
        }
        catch { /* Best-effort: Windows clears the media overlay itself when AxePower exits. */ }
        _smtc = null;
    }
}
