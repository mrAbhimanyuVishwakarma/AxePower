using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

/// <summary>
/// Game Mode is not left behind a game's back. While the GameMode plan is active and a known
/// game is running, a manual pick of another mode asks first, and automatic switches (Auto
/// Switch, later Smart) do not happen at all.
///
/// "Known game" means an executable from the library scan (Steam/Epic manifests) or an app rule.
/// </summary>
public class GameModeGuard
{
    private static readonly TimeSpan LibraryCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly PowerPlanService _powerPlan;
    private readonly SettingsService _settings;
    private readonly Func<Task<IEnumerable<string>>> _libraryExeNames;
    private readonly Func<IEnumerable<string>> _runningProcessNames;

    private HashSet<string>? _libraryCache;
    private DateTime _libraryCachedAt;

    public GameModeGuard(PowerPlanService powerPlan, SettingsService settings, GameScannerService scanner)
        : this(powerPlan, settings,
            async () => (await scanner.GetInstalledGamesAsync())
                .Select(g => !string.IsNullOrEmpty(g.ExecutablePath) ? g.ExecutablePath : g.ExecutableName),
            RunningProcessNames)
    {
    }

    internal GameModeGuard(PowerPlanService powerPlan, SettingsService settings,
        Func<Task<IEnumerable<string>>> libraryExeNames, Func<IEnumerable<string>> runningProcessNames)
    {
        _powerPlan = powerPlan;
        _settings = settings;
        _libraryExeNames = libraryExeNames;
        _runningProcessNames = runningProcessNames;
    }

    /// <summary>
    /// The running game that is holding Game Mode, when switching to <paramref name="target"/>
    /// would end Game Mode under it; otherwise null.
    /// </summary>
    public async Task<string?> GetBlockingGameAsync(PowerMode target)
    {
        if (target == PowerMode.GameMode) return null;
        if (await _powerPlan.GetActivePowerModeAsync() != PowerMode.GameMode) return null;
        return await FindRunningGameAsync();
    }

    public async Task<string?> FindRunningGameAsync()
    {
        var known = new HashSet<string>(await LibraryAsync(), StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _settings.GetSettings().AppRules)
        {
            var name = ExeName(rule.ExecutableName);
            if (name != null) known.Add(name);
        }

        if (known.Count == 0) return null;
        return _runningProcessNames().FirstOrDefault(known.Contains);
    }

    private async Task<IEnumerable<string>> LibraryAsync()
    {
        if (_libraryCache != null && DateTime.UtcNow - _libraryCachedAt < LibraryCacheLifetime)
            return _libraryCache;

        try
        {
            _libraryCache = new HashSet<string>((await _libraryExeNames()).Select(ExeName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // The game library could not be read (the delegate wraps several stores). The guard
            // then treats no running app as a game, the cautious choice; retried after the cache expires.
            _libraryCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        _libraryCachedAt = DateTime.UtcNow;
        return _libraryCache;
    }

    private static string? ExeName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        try { return Path.GetFileNameWithoutExtension(pathOrName.Trim()); }
        catch (ArgumentException) { return null; /* not a path: ignored */ }
    }

    private static IEnumerable<string> RunningProcessNames()
    {
        foreach (var process in Process.GetProcesses())
        {
            string name;
            try { name = process.ProcessName; }
            catch (InvalidOperationException) { continue; /* exited while listed */ }
            finally { process.Dispose(); }
            yield return name;
        }
    }
}
