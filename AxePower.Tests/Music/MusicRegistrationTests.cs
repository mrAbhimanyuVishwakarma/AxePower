using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// Resolves music services from the app's real container (<c>App.ConfigureServices</c>), not a
/// hand-copied registration list, so a missing registration fails here instead of at startup.
/// Only music services and their direct dependencies are constructed.
/// </summary>
public class MusicRegistrationTests
{
    [Fact]
    public void RealContainer_ResolvesTheMusicServices()
    {
        var provider = AxePower.App.App.ConfigureServices();

        var clock = provider.GetRequiredService<IClock>();

        Assert.IsType<SystemClock>(clock);
        Assert.Same(clock, provider.GetRequiredService<IClock>());

        // Phase 1: the player and everything it needs. Resolving opens no audio device —
        // the engine only opens one on the first Play.
        var player = provider.GetRequiredService<IMusicPlayerService>();
        Assert.IsType<MusicPlayerService>(player);
        Assert.IsType<NAudioEngine>(provider.GetRequiredService<IAudioEngine>());
        Assert.IsType<TagLibTagReader>(provider.GetRequiredService<ITagReader>());
        Assert.IsType<CoverArtService>(provider.GetRequiredService<ICoverArtService>());
        Assert.NotNull(provider.GetRequiredService<PlaybackStateStore>());
        Assert.NotNull(provider.GetRequiredService<PlayerIntegrationHost>());

        var integrations = provider.GetServices<IPlayerIntegration>().Select(i => i.Name).ToList();
        Assert.Contains("Windows media controls", integrations);
        Assert.Contains("Taskbar buttons", integrations);
    }
}
