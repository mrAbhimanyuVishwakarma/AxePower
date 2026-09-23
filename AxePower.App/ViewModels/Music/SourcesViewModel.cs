using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AxePower.Core.Models.Music;
using AxePower.Core.Services;
using AxePower.Core.Services.Music;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AxePower.App.ViewModels.Music;

/// <summary>
/// The Sources list in Music Settings: one card per music source, with what it does, whether it
/// is switched on, and whether it is the one answering searches or supplying audio.
/// </summary>
public partial class SourcesViewModel : ObservableObject
{
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settings;
    private readonly StreamCache _cache;
    private readonly LogService _log;

    public SourcesViewModel(ProviderRegistry registry, SettingsService settings, StreamCache cache, LogService log)
    {
        _registry = registry;
        _settings = settings;
        _cache = cache;
        _log = log;

        foreach (var provider in _registry.All.OrderBy(p => p.Info.RequiresSetup).ThenBy(p => p.Info.Name))
            Sources.Add(new ProviderCardViewModel(provider, _registry, _settings));

        _registry.Changed += (_, _) => Application.Current?.Dispatcher?.BeginInvoke(Refresh);

        LoadOptions();
        _ = RefreshStatusAsync();
    }

    public ObservableCollection<ProviderCardViewModel> Sources { get; } = new();

    public string ActiveStreamingName =>
        _registry.All.FirstOrDefault(p => p.Info.Id == _registry.ActiveId(ProviderKind.Streaming))?.Info.Name ?? "None";

    public string ActiveMetadataName =>
        _registry.All.FirstOrDefault(p => p.Info.Id == _registry.ActiveId(ProviderKind.Metadata))?.Info.Name ?? "None";

    // ── Streaming options ───────────────────────────────────────────────────

    [ObservableProperty] private bool _cacheStreamsToDisk;
    [ObservableProperty] private int _streamCacheMaxMb;
    [ObservableProperty] private string _cacheSizeText = "Empty";
    [ObservableProperty] private int _discoveryVariety = 50;

    private bool _loading;

    partial void OnCacheStreamsToDiskChanged(bool value) => SaveOptions(o => o.CacheStreamsToDisk = value);

    partial void OnStreamCacheMaxMbChanged(int value) => SaveOptions(o => o.StreamCacheMaxMb = Math.Clamp(value, 128, 20480));

    partial void OnDiscoveryVarietyChanged(int value) => SaveOptions(o => o.DiscoveryVariety = Math.Clamp(value, 0, 100));

    [RelayCommand]
    private void ClearCache()
    {
        long freed = _cache.Clear();
        _log.LogInfo($"[Music] Cleared {freed / (1024 * 1024)} MB of streamed audio.");
        UpdateCacheSize();
    }

    [RelayCommand]
    private Task Refresh() => RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        foreach (var card in Sources)
            await card.RefreshAsync(CancellationToken.None);

        OnPropertyChanged(nameof(ActiveStreamingName));
        OnPropertyChanged(nameof(ActiveMetadataName));
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
            StreamCacheMaxMb = options.StreamCacheMaxMb;
            DiscoveryVariety = options.DiscoveryVariety;
        }
        finally { _loading = false; }
    }

    private void SaveOptions(Action<ProviderSettings> change)
    {
        if (_loading) return;

        var settings = _settings.GetSettings();
        change(settings.Music.Providers);
        _settings.SaveSettings(settings);
    }
}

/// <summary>One source's card. Everything shown here is read from the provider itself.</summary>
public partial class ProviderCardViewModel : ObservableObject
{
    private readonly IMusicProvider _provider;
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settings;

    public ProviderCardViewModel(IMusicProvider provider, ProviderRegistry registry, SettingsService settings)
    {
        _provider = provider;
        _registry = registry;
        _settings = settings;

        _isEnabled = _registry.IsEnabled(Id);
        _clientId = settings.GetSettings().Music.Integrations?.SpotifyClientId ?? string.Empty;

        if (provider is IAuthenticatedProvider auth)
        {
            auth.AuthChanged += (_, _) => Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(AccountLine));
                _ = RefreshAsync(CancellationToken.None);
            });
        }
    }

    public string Id => _provider.Info.Id;
    public string Name => _provider.Info.Name;
    public string Description => _provider.Info.Description;
    public string Author => "by " + _provider.Info.Author;
    public string Capabilities => string.Join(" · ", _provider.Info.Capabilities);
    public bool CanBeDisabled => _provider.Info.CanBeDisabled;
    public string? SetupHint => _provider.Info.SetupHint;
    public bool NeedsClientId => Id == "spotify";

    public bool IsAuthenticated => _provider is IAuthenticatedProvider;
    public bool IsSignedIn => _provider is IAuthenticatedProvider { IsSignedIn: true };

    public string AccountLine => _provider is IAuthenticatedProvider auth && auth.IsSignedIn
        ? $"Signed in{(string.IsNullOrWhiteSpace(auth.AccountName) ? string.Empty : " as " + auth.AccountName)}"
        : "Not signed in";

    /// <summary>A plain glyph per source, so a card is recognisable before its text is read.</summary>
    public string IconGlyph => Id switch
    {
        "local" => "",
        "youtube" => "",
        "spotify" => "",
        _ => ""
    };

    public Brush AccentBrush => new SolidColorBrush(Id switch
    {
        "local" => Color.FromRgb(0x3B, 0x82, 0xF6),
        "youtube" => Color.FromRgb(0xDC, 0x26, 0x26),
        "spotify" => Color.FromRgb(0x1D, 0xB9, 0x54),
        _ => Color.FromRgb(0x6B, 0x72, 0x80)
    });

    public bool SuppliesAudio => _provider.Info.Kinds.Contains(ProviderKind.Streaming);
    public bool SuppliesSearch => _provider.Info.Kinds.Contains(ProviderKind.Metadata);

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIsWarning))]
    private string _status = "Checking…";

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    public bool StatusIsWarning => Status is "Needs setup" or "Unavailable";

    public bool IsActiveForAudio => SuppliesAudio && _registry.ActiveId(ProviderKind.Streaming) == Id;
    public bool IsActiveForSearch => SuppliesSearch && _registry.ActiveId(ProviderKind.Metadata) == Id;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_registry.IsEnabled(Id) == value) return;
        _registry.SetEnabled(Id, value);
        _ = RefreshAsync(CancellationToken.None);
    }

    partial void OnClientIdChanged(string value)
    {
        var settings = _settings.GetSettings();
        string trimmed = value?.Trim() ?? string.Empty;
        if ((settings.Music.Integrations.SpotifyClientId ?? string.Empty) == trimmed) return;

        settings.Music.Integrations.SpotifyClientId = trimmed.Length == 0 ? null : trimmed;
        _settings.SaveSettings(settings);
        _ = RefreshAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void UseForAudio()
    {
        _registry.SetActive(ProviderKind.Streaming, Id);
        NotifyActive();
    }

    [RelayCommand]
    private void UseForSearch()
    {
        _registry.SetActive(ProviderKind.Metadata, Id);
        NotifyActive();
    }

    [RelayCommand]
    private async Task SignIn()
    {
        if (_provider is not IAuthenticatedProvider auth) return;

        IsBusy = true;
        Error = null;
        try
        {
            if (!await auth.SignInAsync(CancellationToken.None))
                Error = "Sign-in was not completed.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task SignOut()
    {
        if (_provider is not IAuthenticatedProvider auth) return;
        auth.SignOut();
        await RefreshAsync(CancellationToken.None);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        IsEnabled = _registry.IsEnabled(Id);

        // A source that is switched off is reported as such without being asked, so a disabled
        // provider never causes a network call just to draw its card.
        if (!IsEnabled)
        {
            Status = "Off";
        }
        else
        {
            try
            {
                Status = await _provider.GetStateAsync(ct) switch
                {
                    ProviderState.Ready => "Ready",
                    ProviderState.NeedsSetup => "Needs setup",
                    ProviderState.Disabled => "Off",
                    _ => "Unavailable"
                };
            }
            catch
            {
                Status = "Unavailable";
            }
        }

        NotifyActive();
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountLine));
    }

    private void NotifyActive()
    {
        OnPropertyChanged(nameof(IsActiveForAudio));
        OnPropertyChanged(nameof(IsActiveForSearch));
    }
}
