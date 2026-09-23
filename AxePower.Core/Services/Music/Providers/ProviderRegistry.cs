using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Models;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

/// <summary>
/// The list of music sources and the user's choice of which one does what. Everything it knows
/// is derived from the registered providers plus <see cref="ProviderSettings"/>, so a provider
/// that is removed from a build simply stops appearing — stored ids are never trusted blindly.
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    /// <summary>Always enabled: without it local files could not play.</summary>
    public const string LocalProviderId = "local";

    private readonly IReadOnlyList<IMusicProvider> _providers;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    public ProviderRegistry(IEnumerable<IMusicProvider> providers, SettingsService settings, LogService log)
    {
        _providers = providers.ToList();
        _settings = settings;
        _log = log;
        _settings.OnSettingsChanged += OnSettingsChanged;
    }

    /// <summary>Raised when a provider is enabled, disabled, or made active for a kind.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<IMusicProvider> All => _providers;

    public IEnumerable<IMusicProvider> OfKind(ProviderKind kind) =>
        _providers.Where(p => p.Info.Kinds.Contains(kind));

    public IMusicProvider? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : _providers.FirstOrDefault(p => p.Info.Id == id);

    public bool IsEnabled(string id) =>
        id == LocalProviderId || Current.Enabled.Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string id, bool enabled)
    {
        if (id == LocalProviderId) return;            // the local provider is not optional
        if (ById(id) is null) return;

        var settings = _settings.GetSettings();
        var providers = settings.Music.Providers;
        bool already = providers.Enabled.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (already == enabled) return;

        if (enabled) providers.Enabled.Add(id);
        else providers.Enabled.RemoveAll(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase));

        // A provider that was answering for a kind must hand that job back when it is switched off.
        if (!enabled)
        {
            if (providers.ActiveStreaming == id) providers.ActiveStreaming = FallbackFor(ProviderKind.Streaming, id);
            if (providers.ActiveMetadata == id) providers.ActiveMetadata = FallbackFor(ProviderKind.Metadata, id);
        }

        _settings.SaveSettings(settings);
        _log.LogInfo($"[Music] Source '{id}' {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>The enabled provider that answers a kind, or null when none can.</summary>
    public T? Active<T>(ProviderKind kind) where T : class, IMusicProvider
    {
        string? id = kind switch
        {
            ProviderKind.Streaming => Current.ActiveStreaming,
            ProviderKind.Metadata => Current.ActiveMetadata,
            _ => null
        };

        if (ById(id) is T chosen && IsEnabled(chosen.Info.Id) && chosen.Info.Kinds.Contains(kind))
            return chosen;

        // The stored id is gone, disabled or no longer covers this kind: fall back to the first
        // enabled provider that does, rather than leaving the feature dead.
        return OfKind(kind).OfType<T>().FirstOrDefault(p => IsEnabled(p.Info.Id) && CanStandIn(p, kind));
    }

    /// <summary>
    /// Whether a provider is a sensible automatic replacement. The local-file provider is a
    /// streaming provider, but it can only play paths this PC already has, so it must never be
    /// picked as the source that fetches a track from the internet.
    /// </summary>
    private static bool CanStandIn(IMusicProvider provider, ProviderKind kind) =>
        kind != ProviderKind.Streaming || provider is not IStreamingProvider { SupportsLocalFiles: true };

    public string? ActiveId(ProviderKind kind) => kind switch
    {
        ProviderKind.Streaming => Active<IStreamingProvider>(kind)?.Info.Id,
        ProviderKind.Metadata => Active<IMetadataProvider>(kind)?.Info.Id,
        _ => null
    };

    public void SetActive(ProviderKind kind, string id)
    {
        if (ById(id) is not { } provider || !provider.Info.Kinds.Contains(kind)) return;

        var settings = _settings.GetSettings();
        var providers = settings.Music.Providers;

        switch (kind)
        {
            case ProviderKind.Streaming when providers.ActiveStreaming != id:
                providers.ActiveStreaming = id;
                break;
            case ProviderKind.Metadata when providers.ActiveMetadata != id:
                providers.ActiveMetadata = id;
                // A metadata provider that carries no audio of its own pins the streaming
                // source it was designed against, so search results stay playable.
                if (provider.Info.PairedStreamingProviderId is { } paired && ById(paired) is not null)
                {
                    if (!providers.Enabled.Contains(paired, StringComparer.OrdinalIgnoreCase))
                        providers.Enabled.Add(paired);
                    providers.ActiveStreaming = paired;
                }
                break;
            default:
                return;
        }

        if (!providers.Enabled.Contains(id, StringComparer.OrdinalIgnoreCase) && id != LocalProviderId)
            providers.Enabled.Add(id);

        _settings.SaveSettings(settings);
    }

    public ProviderSettings Current => _settings.GetSettings().Music.Providers ?? new ProviderSettings();

    public TimeSpan StreamLifetime => TimeSpan.FromMinutes(Math.Clamp(Current.StreamExpiryMinutes, 1, 24 * 60));

    public int ResolutionRetries => Math.Clamp(Current.StreamResolutionRetries, 1, 10);

    private string FallbackFor(ProviderKind kind, string excludingId) =>
        OfKind(kind).FirstOrDefault(p => p.Info.Id != excludingId && IsEnabled(p.Info.Id) && CanStandIn(p, kind))?.Info.Id
        ?? string.Empty;

    private void OnSettingsChanged(object? sender, AppSettings settings) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _settings.OnSettingsChanged -= OnSettingsChanged;
}
