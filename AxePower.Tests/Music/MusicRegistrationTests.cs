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
        Assert.Contains("Recently played", integrations);
        Assert.Same(provider.GetRequiredService<RecentlyPlayedService>(),
            provider.GetServices<IPlayerIntegration>().OfType<RecentlyPlayedService>().Single());
    }

    [Fact]
    public void RealContainer_ResolvesEverySourceAndTheResolverThatUsesThem()
    {
        var provider = AxePower.App.App.ConfigureServices();

        var registry = provider.GetRequiredService<ProviderRegistry>();
        Assert.Same(registry, provider.GetRequiredService<ProviderRegistry>());

        var ids = registry.All.Select(p => p.Info.Id).ToList();
        Assert.Equal(new[] { "local", "youtube" }, ids.OrderBy(id => id));

        // Every source has to be reachable through the registry as well as on its own, or the
        // Sources list and the resolver would disagree about what exists.
        Assert.IsType<LocalFileProvider>(registry.ById("local"));
        Assert.IsType<YouTubeProvider>(registry.ById("youtube"));
        Assert.Same(registry.ById("youtube"), provider.GetRequiredService<YouTubeProvider>());

        Assert.IsType<StreamResolver>(provider.GetRequiredService<IStreamResolver>());
        Assert.IsType<LrcLibLyricsService>(provider.GetRequiredService<ILyricsService>());
        Assert.IsType<AxePower.App.Services.MusicPaletteExtractor>(provider.GetRequiredService<IPaletteExtractor>());

        // Resolving these must not open or create the database: the library is opened on the
        // first read, so a cold start never pays for it.
        Assert.NotNull(provider.GetRequiredService<LibraryDatabase>());
        Assert.NotNull(provider.GetRequiredService<MusicLibraryRepository>());
        Assert.Same(provider.GetRequiredService<MusicLibraryRepository>(), provider.GetRequiredService<MusicLibraryRepository>());
        Assert.IsType<DpapiSecretStore>(provider.GetRequiredService<ISecretStore>());
        Assert.NotNull(provider.GetRequiredService<StreamCache>());
    }

    [Fact]
    public void ThePlayerIsGivenTheStreamResolver()
    {
        var provider = AxePower.App.App.ConfigureServices();

        // Without this the player silently treats every track as a local file, and nothing
        // from a streaming source would ever play.
        var player = Assert.IsType<MusicPlayerService>(provider.GetRequiredService<IMusicPlayerService>());
        var field = typeof(MusicPlayerService).GetField("_resolver",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.NotNull(field!.GetValue(player));
    }
}
