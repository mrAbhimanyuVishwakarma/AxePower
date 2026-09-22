using Microsoft.Extensions.DependencyInjection;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// Resolves music services from the app's real container (<c>App.ConfigureServices</c>), not a
/// hand-copied registration list, so a missing registration fails here instead of at startup.
/// Only the music services are resolved; nothing else in the container is constructed.
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
    }
}
