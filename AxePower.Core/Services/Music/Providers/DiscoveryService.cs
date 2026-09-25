using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Keeps a queue going. When playback reaches the last track and discovery is on, it asks the
/// active discovery source what to play next and appends the answer, so a session carries on
/// instead of stopping.
///
/// It only ever adds to the end of the queue: it never reorders, removes or interrupts
/// anything, so turning it on cannot lose a queue the listener built by hand.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    /// <summary>How much recent listening is sent as context.</summary>
    private const int ContextTracks = 10;

    /// <summary>A queue this long is already long enough; stop feeding it.</summary>
    private const int MaxQueueLength = 500;

    private readonly IMusicPlayerService _player;
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly IClock _clock;

    private CancellationTokenSource? _cts;
    private bool _fetching;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _started;

    public DiscoveryService(
        IMusicPlayerService player,
        ProviderRegistry registry,
        SettingsService settings,
        LogService log,
        IClock clock)
    {
        _player = player;
        _registry = registry;
        _settings = settings;
        _log = log;
        _clock = clock;
    }

    /// <summary>Raised when a fetch starts and when it ends, for the toggle's busy state.</summary>
    public event EventHandler<bool>? BusyChanged;

    public bool IsEnabled => _settings.GetSettings().Music.Providers?.DiscoveryEnabled ?? false;

    /// <summary>True when a source that can make recommendations is switched on.</summary>
    public bool IsAvailable => _registry.Active<IDiscoveryProvider>(ProviderKind.Discovery) is not null;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _player.StateChanged += OnStateChanged;
    }

    public void SetEnabled(bool enabled)
    {
        if (IsEnabled == enabled) return;

        var settings = _settings.GetSettings();
        settings.Music.Providers.DiscoveryEnabled = enabled;
        _settings.SaveSettings(settings);

        if (!enabled)
        {
            _cts?.Cancel();
            return;
        }

        // Turning it on at the end of a queue should act immediately, rather than waiting for
        // the next track change that may never come.
        Consider(_player.State);
    }

    private void OnStateChanged(object? sender, PlayerState state) => Consider(state);

    private void Consider(PlayerState state)
    {
        if (!IsEnabled || _fetching) return;
        if (state.Queue.Count == 0 || state.Queue.Count >= MaxQueueLength) return;

        // Only the last track triggers a fetch. Repeat modes already keep the queue going on
        // their own, so extending it as well would fight the listener's choice.
        if (state.Index != state.Queue.Count - 1) return;
        if (state.Repeat != RepeatMode.Off) return;

        // A source that keeps returning nothing must not be polled on every position update.
        if (_clock.UtcNow - _lastAttemptUtc < TimeSpan.FromSeconds(30)) return;

        var provider = _registry.Active<IDiscoveryProvider>(ProviderKind.Discovery);
        if (provider is null) return;

        // Browse modes are intentionally isolated. Recommendations may extend a YouTube Music
        // session, but must never inject online tracks into a local-file queue.
        if (state.Current?.Track.Source?.Provider != provider.Info.Id) return;

        _lastAttemptUtc = _clock.UtcNow;
        _fetching = true;
        BusyChanged?.Invoke(this, true);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = ExtendAsync(provider, state, _cts.Token);
    }

    private async Task ExtendAsync(IDiscoveryProvider provider, PlayerState state, CancellationToken ct)
    {
        try
        {
            var context = state.Queue
                .Skip(Math.Max(0, state.Queue.Count - ContextTracks))
                .Select(item => item.Track)
                .ToList();

            var options = new DiscoveryOptions(
                Variety: Math.Clamp((_settings.GetSettings().Music.Providers?.DiscoveryVariety ?? 50) / 100.0, 0, 1),
                Limit: Math.Clamp(_settings.GetSettings().Music.Providers?.DiscoveryBatchSize ?? 10, 1, 50));

            var tracks = await provider.GetRecommendationsAsync(context, options, ct);
            if (ct.IsCancellationRequested || tracks.Count == 0)
            {
                if (tracks.Count == 0)
                    _log.LogInfo($"[Music] {provider.Info.Name} had nothing to add to the queue.");
                return;
            }

            // The queue may have changed while the search was running, so anything that is now
            // already queued is dropped rather than added twice.
            var queued = _player.State.Queue
                .Select(item => item.Track.Source?.Id)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.Ordinal)!;

            var fresh = tracks.Where(t => t.Source is null || queued.Add(t.Source.Id)).ToList();
            if (fresh.Count == 0) return;

            _player.AddToQueue(fresh);
            _log.LogInfo($"[Music] Discovery added {fresh.Count} track(s) from {provider.Info.Name}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogWarning($"[Music] Discovery could not extend the queue: {ex.Message}");
        }
        finally
        {
            _fetching = false;
            BusyChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        if (_started) _player.StateChanged -= OnStateChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
