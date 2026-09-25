using System;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// Working options for YouTube Music streaming and discovery. Local playback and YouTube Music
/// are fixed modes, so provider-selection controls are intentionally not exposed.
/// </summary>
public partial class SourcesViewModel : ObservableObject
{
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settings;
    private readonly StreamCache _cache;
    private readonly LogService _log;
    private bool _loading;

    public SourcesViewModel(ProviderRegistry registry, SettingsService settings, StreamCache cache, LogService log)
    {
        _registry = registry;
        _settings = settings;
        _cache = cache;
        _log = log;

        LoadOptions();
        UpdateCacheSize();
    }

    [ObservableProperty]
    private bool _cacheStreamsToDisk;

    [ObservableProperty]
    private string _cacheSizeText = "Empty";

    [ObservableProperty]
    private int _discoveryVariety = 50;

    partial void OnCacheStreamsToDiskChanged(bool value) =>
        SaveOptions(options => options.CacheStreamsToDisk = value);

    partial void OnDiscoveryVarietyChanged(int value) =>
        SaveOptions(options => options.DiscoveryVariety = Math.Clamp(value, 0, 100));

    [RelayCommand]
    private void ClearCache()
    {
        long freed = _cache.Clear();
        _log.LogInfo($"[Music] Cleared {freed / (1024 * 1024)} MB of streamed audio.");
        UpdateCacheSize();
    }

    private void UpdateCacheSize()
    {
        long bytes = _cache.SizeBytes();
        CacheSizeText = bytes < 1024 * 1024
            ? "Empty"
            : $"{bytes / (1024.0 * 1024.0):0.#} MB on disk";
    }

    private void LoadOptions()
    {
        _loading = true;
        try
        {
            var options = _registry.Current;
            CacheStreamsToDisk = options.CacheStreamsToDisk;
            DiscoveryVariety = options.DiscoveryVariety;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveOptions(Action<ProviderSettings> change)
    {
        if (_loading) return;

        var settings = _settings.GetSettings();
        change(settings.Music.Providers);
        _settings.SaveSettings(settings);
    }
}
