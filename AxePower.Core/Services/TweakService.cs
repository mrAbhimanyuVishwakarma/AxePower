using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;
using AxePower.Core.Services.Operations;

namespace AxePower.Core.Services;

/// <summary>
/// System Optimizer's registry tweaks. Every apply and revert is an operation (see
/// <see cref="OperationRunner"/>): it can be previewed, it is recorded with a snapshot of each value
/// it touches, and reverting restores exactly what was there before, not a guessed default.
/// Tweaks applied before Prompt 24 have no snapshot; reverting those writes Windows' defaults,
/// and the preview says so.
/// </summary>
public class TweakService : ITweakService
{
    public const string SourceFeature = "System Optimizer › Tweaks";

    private const string ClassicMenuClsid = @"HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private readonly LogService _log;
    private readonly List<SystemTweakItem> _tweaks = new();
    private readonly Func<RegistryHive, RegistryKey> _root;
    private readonly Lazy<OperationRunner> _runner;

    /// <param name="runner">Where changes are executed and recorded. Null: the app's journal, created on first use.</param>
    /// <param name="root">Base key per hive. Tests map hives to a scratch key so they never touch the real registry.</param>
    public TweakService(LogService log, OperationRunner? runner = null, Func<RegistryHive, RegistryKey>? root = null)
    {
        _log = log;
        _root = root ?? (hive => hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser);
        _runner = new Lazy<OperationRunner>(() => runner ?? new OperationRunner(
            new OperationJournal(OperationJournal.DefaultDirectory, log), log, new IChangeHandler[] { new RegistryChangeHandler(_root) }));
        InitializeTweaksList();
    }

    public OperationJournal Journal => _runner.Value.Journal;

    public IReadOnlyList<SystemTweakItem> GetAllTweaks() => _tweaks.AsReadOnly();

    private void InitializeTweaksList()
    {
        _tweaks.Clear();

        _tweaks.Add(new SystemTweakItem
        {
            Id = "telemetry-disable",
            Title = "Disable Diagnostic Telemetry",
            Description = "Stops Windows background diagnostic telemetry and feedback reporting, freeing CPU cycles and network polling.",
            Category = TweakCategory.Privacy,
            Impact = TweakImpact.High,
            IsRecommended = true,
            RegistryKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
            ValueName = "AllowTelemetry"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "bing-search-disable",
            Title = "Disable Bing Web Search in Start Menu",
            Description = "Prevents the Windows Start Menu from querying Bing web search results, making local app searches instant.",
            Category = TweakCategory.Privacy,
            Impact = TweakImpact.High,
            IsRecommended = true,
            RegistryKey = @"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\Explorer",
            ValueName = "DisableSearchBoxSuggestions"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "copilot-disable",
            Title = "Disable Windows Copilot & AI Overheads",
            Description = "Disables Windows Copilot background integrations and reduces background taskbar memory usage.",
            Category = TweakCategory.Privacy,
            Impact = TweakImpact.Medium,
            IsRecommended = true,
            RegistryKey = @"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot",
            ValueName = "TurnOffWindowsCopilot"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "activity-feed-disable",
            Title = "Disable Activity Feed & Timeline",
            Description = "Stops Windows from recording activity history and sending timeline events to cloud servers.",
            Category = TweakCategory.Privacy,
            Impact = TweakImpact.Medium,
            IsRecommended = true,
            RegistryKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
            ValueName = "EnableActivityFeed"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "gamedvr-disable",
            Title = "Disable GameDVR Background Capture",
            Description = "Prevents Windows GameDVR from continuously encoding background video in RAM/SSD, eliminating frame-time stutters.",
            Category = TweakCategory.Gaming,
            Impact = TweakImpact.High,
            IsRecommended = true,
            RegistryKey = @"HKEY_CURRENT_USER\System\GameConfigStore",
            ValueName = "GameDVR_Enabled"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "mouse-accel-disable",
            Title = "Disable Mouse Acceleration (1:1 Raw Input)",
            Description = "Disables 'Enhance Pointer Precision' curve to deliver consistent, linear 1:1 mouse movement for gaming precision.",
            Category = TweakCategory.Gaming,
            Impact = TweakImpact.High,
            IsRecommended = true,
            RegistryKey = @"HKEY_CURRENT_USER\Control Panel\Mouse",
            ValueName = "MouseSpeed"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "classic-context-menu",
            Title = "Classic Windows 10 Full Context Menu",
            Description = "Restores the instant full right-click context menu in Windows 11 without requiring 'Show more options'.",
            Category = TweakCategory.Windows11,
            Impact = TweakImpact.High,
            IsRecommended = true,
            RegistryKey = @"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32",
            ValueName = "(Default)"
        });

        _tweaks.Add(new SystemTweakItem
        {
            Id = "sticky-keys-disable",
            Title = "Disable Sticky Keys 5x Shift Shortcut",
            Description = "Prevents the Sticky Keys accessibility dialog from interrupting games when pressing Shift rapidly.",
            Category = TweakCategory.Gaming,
            Impact = TweakImpact.Low,
            IsRecommended = true,
            RegistryKey = @"HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys",
            ValueName = "Flags"
        });
    }

    public async Task RefreshAllTweakStatusesAsync()
    {
        await Task.Run(() =>
        {
            foreach (var tweak in _tweaks)
            {
                try
                {
                    tweak.IsApplied = IsTweakApplied(tweak.Id);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning($"[TweakService] Failed to read status for tweak '{tweak.Id}': {ex.Message}");
                    tweak.IsApplied = false;
                }
            }
        });
    }

    // -- Plans ----------------------------------------------------------------------

    public static string SubjectFor(string tweakId) => "tweak:" + tweakId.ToLowerInvariant();

    /// <summary>What applying the tweak would change, with today's values. Changes nothing.</summary>
    public OperationRecord PlanApply(string tweakId)
    {
        var tweak = Find(tweakId);
        return _runner.Value.Plan($"Apply tweak: {tweak.Title}", SourceFeature, SubjectFor(tweak.Id), ApplyRequests(tweak.Id));
    }

    /// <summary>
    /// What reverting would change. With a recorded apply, the rollback of it (exactly what was there
    /// before). Without one, Windows' defaults, and the plan says that it is not a restore.
    /// </summary>
    public OperationRecord PlanRevert(string tweakId)
    {
        var tweak = Find(tweakId);
        if (_runner.Value.Journal.LatestActive(SubjectFor(tweak.Id)) is { } applied && OperationRunner.CanRollBack(applied))
            return _runner.Value.PlanRollback(applied);

        return _runner.Value.Plan($"Restore Windows defaults: {tweak.Title}", SourceFeature, SubjectFor(tweak.Id), DefaultRequests(tweak.Id),
            notice: "AxePower has no record of what was set before this tweak was applied, so this writes Windows' defaults instead of restoring your earlier values.");
    }

    public IReadOnlyList<OperationRecord> PlanApplyRecommended() =>
        _tweaks.Where(t => t.IsRecommended && !t.IsApplied).Select(t => PlanApply(t.Id)).ToList();

    public IReadOnlyList<OperationRecord> PlanRevertAll() =>
        _tweaks.Where(t => t.IsApplied).Select(t => PlanRevert(t.Id)).ToList();

    /// <summary>Runs an approved plan. The tweak's values only make sense together, so a failure undoes the rest.</summary>
    public async Task<OperationRecord> ExecuteAsync(OperationRecord plan)
    {
        var record = await _runner.Value.ExecuteAsync(plan, FailurePolicy.UndoCompleted).ConfigureAwait(false);
        var tweak = _tweaks.FirstOrDefault(t => SubjectFor(t.Id) == record.Subject);
        if (tweak is not null)
        {
            try { tweak.IsApplied = IsTweakApplied(tweak.Id); }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // Status unknown: the record still says what happened.
                tweak.IsApplied = false;
            }
        }
        return record;
    }

    // -- The older calls, now through operations (no preview) --------------------------

    public async Task<bool> ApplyTweakAsync(string tweakId) =>
        (await ExecuteAsync(PlanApply(tweakId)).ConfigureAwait(false)).Status == OperationStatus.Succeeded;

    public async Task<bool> RevertTweakAsync(string tweakId) =>
        (await ExecuteAsync(PlanRevert(tweakId)).ConfigureAwait(false)).Status == OperationStatus.Succeeded;

    public async Task<int> ApplyAllRecommendedAsync()
    {
        int count = 0;
        foreach (var plan in PlanApplyRecommended())
        {
            if ((await ExecuteAsync(plan).ConfigureAwait(false)).Status == OperationStatus.Succeeded) count++;
        }
        return count;
    }

    public async Task<int> RevertAllAsync()
    {
        int count = 0;
        foreach (var plan in PlanRevertAll())
        {
            if ((await ExecuteAsync(plan).ConfigureAwait(false)).Status == OperationStatus.Succeeded) count++;
        }
        return count;
    }

    private SystemTweakItem Find(string tweakId) =>
        _tweaks.FirstOrDefault(t => t.Id.Equals(tweakId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown tweak '{tweakId}'.", nameof(tweakId));

    // -- What each tweak changes ---------------------------------------------------------

    /// <summary>The exact registry changes that apply a tweak.</summary>
    public static IReadOnlyList<ChangeRequest> ApplyRequests(string tweakId) => tweakId.ToLowerInvariant() switch
    {
        "telemetry-disable" => new[] { RegistryChangeHandler.SetDword(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0) },
        "bing-search-disable" => new[]
        {
            RegistryChangeHandler.SetDword(@"HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
            RegistryChangeHandler.SetDword(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
        },
        "copilot-disable" => new[]
        {
            RegistryChangeHandler.SetDword(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
            RegistryChangeHandler.SetDword(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
        },
        "activity-feed-disable" => new[] { RegistryChangeHandler.SetDword(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0) },
        "gamedvr-disable" => new[]
        {
            RegistryChangeHandler.SetDword(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0),
            RegistryChangeHandler.SetDword(@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2),
            RegistryChangeHandler.SetDword(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0),
        },
        "mouse-accel-disable" => new[]
        {
            RegistryChangeHandler.SetString(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0"),
            RegistryChangeHandler.SetString(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0"),
            RegistryChangeHandler.SetString(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0"),
        },
        "classic-context-menu" => new[]
        {
            RegistryChangeHandler.EnsureKey(ClassicMenuClsid + @"\InprocServer32"),
            RegistryChangeHandler.SetString(ClassicMenuClsid + @"\InprocServer32", "", ""),
        },
        "sticky-keys-disable" => new[] { RegistryChangeHandler.SetString(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506") },
        _ => throw new ArgumentException($"Unknown tweak '{tweakId}'.", nameof(tweakId)),
    };

    /// <summary>Windows' defaults, used only when there is no recorded apply to roll back.</summary>
    public static IReadOnlyList<ChangeRequest> DefaultRequests(string tweakId) => tweakId.ToLowerInvariant() switch
    {
        "telemetry-disable" => new[] { RegistryChangeHandler.DeleteValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") },
        "bing-search-disable" => new[]
        {
            RegistryChangeHandler.DeleteValue(@"HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"),
            RegistryChangeHandler.DeleteValue(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled"),
        },
        "copilot-disable" => new[]
        {
            RegistryChangeHandler.DeleteValue(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot"),
            RegistryChangeHandler.DeleteValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot"),
        },
        "activity-feed-disable" => new[] { RegistryChangeHandler.DeleteValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed") },
        "gamedvr-disable" => new[]
        {
            RegistryChangeHandler.SetDword(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 1),
            RegistryChangeHandler.DeleteValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR"),
        },
        "mouse-accel-disable" => new[]
        {
            RegistryChangeHandler.SetString(@"HKCU\Control Panel\Mouse", "MouseSpeed", "1"),
            RegistryChangeHandler.SetString(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "6"),
            RegistryChangeHandler.SetString(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "10"),
        },
        "classic-context-menu" => new[] { RegistryChangeHandler.RemoveKey(ClassicMenuClsid) },
        "sticky-keys-disable" => new[] { RegistryChangeHandler.SetString(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "510") },
        _ => throw new ArgumentException($"Unknown tweak '{tweakId}'.", nameof(tweakId)),
    };

    // -- Reading status --------------------------------------------------------------

    private bool IsTweakApplied(string tweakId)
    {
        var machine = _root(RegistryHive.LocalMachine);
        var user = _root(RegistryHive.CurrentUser);
        return tweakId.ToLowerInvariant() switch
        {
            "telemetry-disable" =>
                GetDword(machine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0,

            "bing-search-disable" =>
                GetDword(user, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1 ||
                GetDword(user, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled") == 0,

            "copilot-disable" =>
                GetDword(user, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1 ||
                GetDword(machine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1,

            "activity-feed-disable" =>
                GetDword(machine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed") == 0,

            "gamedvr-disable" =>
                GetDword(user, @"System\GameConfigStore", "GameDVR_Enabled") == 0,

            "mouse-accel-disable" =>
                GetString(user, @"Control Panel\Mouse", "MouseSpeed") == "0",

            "classic-context-menu" =>
                KeyExists(user, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"),

            "sticky-keys-disable" =>
                GetString(user, @"Control Panel\Accessibility\StickyKeys", "Flags") == "506",

            _ => false
        };
    }

    private static int? GetDword(RegistryKey root, string subKeyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            if (key == null) return null;
            var val = key.GetValue(valueName);
            if (val is int intVal) return intVal;
            if (val is string strVal && int.TryParse(strVal, out int parsed)) return parsed;
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable counts as "not set", which shows the tweak as not applied.
            return null;
        }
    }

    private static string? GetString(RegistryKey root, string subKeyPath, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            return key?.GetValue(valueName)?.ToString();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable counts as "not set", which shows the tweak as not applied.
            return null;
        }
    }

    private static bool KeyExists(RegistryKey root, string subKeyPath)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            return key != null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable counts as absent, which shows the tweak as not applied.
            return false;
        }
    }
}
