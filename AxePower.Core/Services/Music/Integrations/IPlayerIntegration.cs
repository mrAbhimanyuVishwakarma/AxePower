using System;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// Something that reacts to the player: Windows media controls, the taskbar, play history,
/// Discord, Last.fm. The player broadcasts immutable snapshots; integrations never call back
/// into it synchronously from these methods.
/// </summary>
public interface IPlayerIntegration : IDisposable
{
    string Name { get; }

    Task InitializeAsync(IntPtr mainWindowHandle, CancellationToken ct);

    /// <summary>Must return quickly; slow work is offloaded.</summary>
    void OnStateChanged(PlayerState state);

    void OnPositionChanged(TimeSpan position) { }

    /// <summary>Lets an integration enable or disable itself.</summary>
    void OnSettingsChanged(MusicSettings settings) { }
}
