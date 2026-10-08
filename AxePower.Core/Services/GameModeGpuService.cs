using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;
using Microsoft.Win32;

namespace AxePower.Core.Services;

/// <summary>
/// "Use dedicated GPU in Game Mode": while Game Mode is on, every known game is given Windows'
/// "High performance" GPU preference (Settings → Display → Graphics). When Game Mode ends, each
/// entry goes back to exactly what it was before, including "no entry at all".
///
/// Windows reads the preference when a program starts, so a game that was already running when
/// Game Mode began keeps the GPU it launched on until it is restarted.
///
/// What AxePower changed is kept in settings.json (<see cref="AppSettings.GpuPreferenceBackups"/>),
/// so if AxePower is closed or crashes during Game Mode, the next mode change (the startup switch
/// to Smart) puts everything back.
/// </summary>
public class GameModeGpuService
{
    private readonly SettingsService _settingsService;
    private readonly LogService _log;
    private readonly IGpuPreferenceStore _store;
    private readonly Func<Task<IEnumerable<string>>> _gamePaths;
    private readonly object _lock = new();

    public GameModeGpuService(SettingsService settingsService, LogService log, GameScannerService scanner)
        : this(settingsService, log, new RegistryGpuPreferenceStore(), () => CollectGamePathsAsync(settingsService, scanner))
    {
    }

    internal GameModeGpuService(SettingsService settingsService, LogService log, IGpuPreferenceStore store, Func<Task<IEnumerable<string>>> gamePaths)
    {
        _settingsService = settingsService;
        _log = log;
        _store = store;
        _gamePaths = gamePaths;
    }

    /// <summary>Follows every verified mode change: pin on entering Game Mode, restore on leaving it.</summary>
    public void Attach(PowerPlanService powerPlan)
    {
        powerPlan.ModeApplied += (_, mode) => _ = OnModeAppliedAsync(mode);
    }

    public bool IsGameModeActive { get; private set; }

    public async Task OnModeAppliedAsync(PowerMode mode)
    {
        IsGameModeActive = mode == PowerMode.GameMode;
        try
        {
            if (IsGameModeActive && _settingsService.GetSettings().GameModeUseDedicatedGpu)
                await PinGamesAsync();
            else
                Restore();
        }
        catch (Exception ex)
        {
            _log.LogError("Game Mode GPU preference update failed.", ex);
        }
    }

    /// <summary>Called after the Settings toggle changes, so it takes effect in a running Game Mode.</summary>
    public Task OnSettingChangedAsync() => OnModeAppliedAsync(IsGameModeActive ? PowerMode.GameMode : PowerMode.Smart);

    public async Task<int> PinGamesAsync()
    {
        var paths = (await _gamePaths())
            .Select(TryFullPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_lock)
        {
            var settings = _settingsService.GetSettings();
            var backups = settings.GpuPreferenceBackups;
            int pinned = 0;

            foreach (var path in paths)
            {
                if (backups.Keys.Any(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var original = _store.Get(path);
                if (!_store.Set(path, UserGpuPreference.WithHighPerformance(original)))
                {
                    _log.LogWarning($"Could not set the dedicated GPU for {path}.");
                    continue;
                }

                backups[path] = original;
                pinned++;
            }

            if (pinned > 0)
            {
                _settingsService.SaveSettings(settings);
                _log.LogInfo($"Game Mode: {pinned} game(s) set to the dedicated GPU.");
            }
            return pinned;
        }
    }

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException or System.Security.SecurityException) { return null; /* not a usable path: skipped */ }
    }

    /// <summary>
    /// Puts back every preference AxePower changed. An entry the user has since changed in
    /// Windows Settings is theirs now and is left alone.
    /// </summary>
    public int Restore()
    {
        lock (_lock)
        {
            var settings = _settingsService.GetSettings();
            var backups = settings.GpuPreferenceBackups;
            if (backups.Count == 0) return 0;

            int restored = 0;
            foreach (var (path, original) in backups.ToList())
            {
                var current = _store.Get(path);
                if (current == UserGpuPreference.WithHighPerformance(original))
                {
                    bool ok = original == null ? _store.Delete(path) : _store.Set(path, original);
                    if (ok) restored++;
                    else
                    {
                        _log.LogWarning($"Could not restore the GPU preference for {path}.");
                        continue;
                    }
                }
                backups.Remove(path);
            }

            _settingsService.SaveSettings(settings);
            _log.LogInfo($"Game Mode ended: restored the GPU preference of {restored} game(s).");
            return restored;
        }
    }

    /// <summary>
    /// Library games (Steam and Epic manifests carry the exe path) plus any running process whose
    /// executable is an app rule or a library game, which also covers rule games installed
    /// somewhere the scanner does not look.
    /// </summary>
    private static async Task<IEnumerable<string>> CollectGamePathsAsync(SettingsService settingsService, GameScannerService scanner)
    {
        var paths = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in await scanner.GetInstalledGamesAsync())
        {
            if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) continue;
            paths.Add(game.ExecutablePath);
            names.Add(Path.GetFileNameWithoutExtension(game.ExecutablePath));
        }

        foreach (var rule in settingsService.GetSettings().AppRules)
            names.Add(Path.GetFileNameWithoutExtension(rule.ExecutableName));

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!names.Contains(process.ProcessName)) continue;
                var file = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file)) paths.Add(file);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Protected or already exited
            }
            finally
            {
                process.Dispose();
            }
        }

        return paths;
    }
}

/// <summary>
/// The value format of HKCU\Software\Microsoft\DirectX\UserGpuPreferences: "Key=Value;" pairs, of
/// which GpuPreference is 0 (let Windows decide), 1 (power saving) or 2 (high performance).
/// Other keys Windows keeps there, such as SwapEffectUpgradeEnable, are preserved.
/// </summary>
public static class UserGpuPreference
{
    public const string HighPerformance = "2";

    public static string WithHighPerformance(string? existing)
    {
        var pairs = Parse(existing);
        pairs["GpuPreference"] = HighPerformance;
        return string.Concat(pairs.Select(p => $"{p.Key}={p.Value};"));
    }

    private static Dictionary<string, string> Parse(string? value)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return pairs;

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) pairs[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return pairs;
    }
}

/// <summary>Per-executable GPU preferences, keyed by full exe path.</summary>
internal interface IGpuPreferenceStore
{
    string? Get(string exePath);
    bool Set(string exePath, string value);
    bool Delete(string exePath);
}

internal sealed class RegistryGpuPreferenceStore : IGpuPreferenceStore
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public string? Get(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(exePath) as string;
    }

    public bool Set(string exePath, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(exePath, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        {
            // Reported to the caller, which logs the game it could not change or restore.
            return false;
        }
    }

    public bool Delete(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(exePath, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or UnauthorizedAccessException)
        {
            // Reported to the caller, which logs the game it could not change or restore.
            return false;
        }
    }
}
