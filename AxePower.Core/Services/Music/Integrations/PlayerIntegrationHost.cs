using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Broadcasts player snapshots to every <see cref="IPlayerIntegration"/>. One integration
/// failing (to start or to handle a change) never affects the others or the player.
/// </summary>
public sealed class PlayerIntegrationHost : IDisposable
{
    private readonly IMusicPlayerService _player;
    private readonly IReadOnlyList<IPlayerIntegration> _integrations;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private bool _initialized;

    public PlayerIntegrationHost(IMusicPlayerService player, IEnumerable<IPlayerIntegration> integrations, SettingsService settings, LogService log)
    {
        _player = player;
        _integrations = integrations.ToList();
        _settings = settings;
        _log = log;
    }

    public async Task InitializeAsync(IntPtr mainWindowHandle, CancellationToken ct)
    {
        if (_initialized) return;
        _initialized = true;

        foreach (var integration in _integrations)
        {
            try
            {
                await integration.InitializeAsync(mainWindowHandle, ct);
                integration.OnSettingsChanged(_settings.GetSettings().Music);
                integration.OnStateChanged(_player.State);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[Music] Integration '{integration.Name}' could not start: {ex.Message}");
            }
        }

        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _settings.OnSettingsChanged += OnSettingsChanged;
    }

    private void OnStateChanged(object? sender, Models.Music.PlayerState state) =>
        ForEach(i => i.OnStateChanged(state));

    private void OnPositionChanged(object? sender, TimeSpan position) =>
        ForEach(i => i.OnPositionChanged(position));

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        ForEach(i => i.OnSettingsChanged(settings.Music));

    private void ForEach(Action<IPlayerIntegration> action)
    {
        foreach (var integration in _integrations)
        {
            try { action(integration); }
            catch (Exception ex) { _log.LogWarning($"[Music] Integration '{integration.Name}' failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _player.StateChanged -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _settings.OnSettingsChanged -= OnSettingsChanged;

        foreach (var integration in _integrations)
        {
            try { integration.Dispose(); } catch { }
        }
    }
}
