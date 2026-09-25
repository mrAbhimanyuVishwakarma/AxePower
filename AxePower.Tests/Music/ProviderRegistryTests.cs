using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;

namespace AxePower.Tests.Music;

/// <summary>
/// Which source answers which job. Settings live in a temp folder, so nothing here touches the
/// real settings.json.
/// </summary>
public sealed class ProviderRegistryTests : IDisposable
{
    private readonly TestAudio _files = new();
    private readonly LogService _log = new();
    private readonly SettingsService _settings;
    private readonly FakeProvider _youtube = new("youtube", "YouTube", ProviderKind.Streaming, ProviderKind.Metadata);
    private readonly FakeProvider _catalog = new("catalog", "Test Catalog", pairedStreaming: "youtube", kinds: ProviderKind.Metadata);
    private readonly LocalFileProvider _local = new();
    private readonly ProviderRegistry _registry;

    public ProviderRegistryTests()
    {
        _settings = new SettingsService(_log, Path.Combine(_files.Folder, "settings"));
        _registry = new ProviderRegistry(new IMusicProvider[] { _local, _youtube, _catalog }, _settings, _log);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _files.Dispose();
    }

    [Fact]
    public void OutOfTheBox_LocalAndYouTubeAreOn_AndOptionalCatalogIsNot()
    {
        Assert.True(_registry.IsEnabled("local"));
        Assert.True(_registry.IsEnabled("youtube"));
        Assert.False(_registry.IsEnabled("catalog"));
    }

    [Fact]
    public void TheLocalSourceCannotBeSwitchedOff()
    {
        _registry.SetEnabled("local", false);

        Assert.True(_registry.IsEnabled("local"));
        Assert.False(_local.Info.CanBeDisabled);
    }

    [Fact]
    public void EnablingASourceIsRemembered()
    {
        _registry.SetEnabled("catalog", true);

        Assert.True(_registry.IsEnabled("catalog"));
        Assert.Contains("catalog", _settings.GetSettings().Music.Providers.Enabled);
    }

    [Fact]
    public void ChoosingAPairedCatalogueAlsoTurnsOnTheSourceThatSuppliesItsAudio()
    {
        // The catalog carries no audio of its own, so picking it for search must leave a working
        // audio source behind it rather than silently breaking playback.
        _registry.SetActive(ProviderKind.Metadata, "catalog");

        Assert.Equal("catalog", _registry.ActiveId(ProviderKind.Metadata));
        Assert.Equal("youtube", _registry.ActiveId(ProviderKind.Streaming));
        Assert.True(_registry.IsEnabled("youtube"));
    }

    [Fact]
    public void TheFixedYouTubeSourceCannotBeSwitchedOff()
    {
        _registry.SetEnabled("youtube", false);

        Assert.True(_registry.IsEnabled("youtube"));
        Assert.Equal("youtube", _registry.ActiveId(ProviderKind.Metadata));
        Assert.Equal("youtube", _registry.ActiveId(ProviderKind.Streaming));
    }

    [Fact]
    public void AStoredIdForASourceThisBuildNoLongerHasIsIgnored()
    {
        var current = _settings.GetSettings();
        current.Music.Providers.ActiveStreaming = "some-removed-plugin";
        _settings.SaveSettings(current);

        // The first enabled source that can do the job takes over, instead of the feature dying.
        Assert.Equal("youtube", _registry.Active<IStreamingProvider>(ProviderKind.Streaming)?.Info.Id);
    }

    [Fact]
    public void AStoredLocalId_IsNeverUsedForRemoteStreaming()
    {
        var current = _settings.GetSettings();
        current.Music.Providers.ActiveStreaming = "local";
        _settings.SaveSettings(current);

        Assert.Equal("youtube", _registry.ActiveId(ProviderKind.Streaming));
    }

    [Fact]
    public void AStoredDisabledYouTubeSource_RemainsAvailableAsAFixedMode()
    {
        var current = _settings.GetSettings();
        current.Music.Providers.Enabled.Remove("youtube");
        current.Music.Providers.ActiveStreaming = "local";
        _settings.SaveSettings(current);

        Assert.True(_registry.IsEnabled("youtube"));
        Assert.Equal("youtube", _registry.ActiveId(ProviderKind.Streaming));
    }

    [Fact]
    public void ASourceIsNeverMadeActiveForAJobItCannotDo()
    {
        _registry.SetActive(ProviderKind.Streaming, "catalog");

        Assert.NotEqual("catalog", _registry.ActiveId(ProviderKind.Streaming));
    }

    [Fact]
    public void LocalFilesCannotBeSelectedAsTheRemoteStreamingSource()
    {
        _registry.SetActive(ProviderKind.Streaming, "local");

        Assert.Equal("youtube", _registry.ActiveId(ProviderKind.Streaming));
    }

    [Fact]
    public void ADisabledOptionalSourceIsNotHandedAnyWork()
    {
        Assert.False(_registry.IsEnabled("catalog"));
        Assert.DoesNotContain(_registry.OfKind(ProviderKind.Metadata).Where(p => _registry.IsEnabled(p.Info.Id)),
            p => p.Info.Id == "catalog");
    }

    private sealed class FakeProvider : IStreamingProvider, IMetadataProvider
    {
        public FakeProvider(string id, string name, params ProviderKind[] kinds)
            : this(id, name, null, kinds)
        {
        }

        public FakeProvider(string id, string name, string? pairedStreaming, params ProviderKind[] kinds)
        {
            Info = new ProviderInfo(id, name, name, "Tests", kinds, Array.Empty<string>(),
                PairedStreamingProviderId: pairedStreaming);
        }

        public ProviderInfo Info { get; }
        public bool SupportsLocalFiles => false;

        public Task<ProviderState> GetStateAsync(CancellationToken ct) => Task.FromResult(ProviderState.Ready);

        public Task<IReadOnlyList<StreamCandidate>> FindCandidatesAsync(Track track, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StreamCandidate>>(Array.Empty<StreamCandidate>());

        public Task<AudioStream?> ResolveAsync(StreamCandidate candidate, bool preferLocalCopy, CancellationToken ct) =>
            Task.FromResult<AudioStream?>(null);

        public Task<IReadOnlyList<Track>> SearchTracksAsync(string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());

        public Task<IReadOnlyList<ProviderPlaylist>> GetPlaylistsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderPlaylist>>(Array.Empty<ProviderPlaylist>());

        public Task<IReadOnlyList<Track>> GetPlaylistTracksAsync(string playlistId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>());
    }
}
