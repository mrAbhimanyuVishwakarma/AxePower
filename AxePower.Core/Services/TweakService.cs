using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

public class TweakService : ITweakService
{
    private readonly LogService _log;
    private readonly List<SystemTweakItem> _tweaks = new();

    public TweakService(LogService log)
    {
        _log = log;
        InitializeTweaksList();
    }

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
                catch (Exception ex)
                {
                    _log.LogWarning($"[TweakService] Failed to read status for tweak '{tweak.Id}': {ex.Message}");
                    tweak.IsApplied = false;
                }
            }
        });
    }

    public async Task<bool> ApplyTweakAsync(string tweakId)
    {
        return await Task.Run(() =>
        {
            var tweak = _tweaks.FirstOrDefault(t => t.Id.Equals(tweakId, StringComparison.OrdinalIgnoreCase));
            if (tweak == null) return false;

            try
            {
                bool success = ExecuteApply(tweak.Id);
                if (success)
                {
                    tweak.IsApplied = true;
                    _log.LogInfo($"[TweakService] Successfully applied tweak '{tweak.Title}'");
                }
                return success;
            }
            catch (Exception ex)
            {
                _log.LogError($"[TweakService] Error applying tweak '{tweak.Title}'", ex);
                return false;
            }
        });
    }

    public async Task<bool> RevertTweakAsync(string tweakId)
    {
        return await Task.Run(() =>
        {
            var tweak = _tweaks.FirstOrDefault(t => t.Id.Equals(tweakId, StringComparison.OrdinalIgnoreCase));
            if (tweak == null) return false;

            try
            {
                bool success = ExecuteRevert(tweak.Id);
                if (success)
                {
                    tweak.IsApplied = false;
                    _log.LogInfo($"[TweakService] Successfully reverted tweak '{tweak.Title}'");
                }
                return success;
            }
            catch (Exception ex)
            {
                _log.LogError($"[TweakService] Error reverting tweak '{tweak.Title}'", ex);
                return false;
            }
        });
    }

    public async Task<int> ApplyAllRecommendedAsync()
    {
        int count = 0;
        foreach (var tweak in _tweaks.Where(t => t.IsRecommended && !t.IsApplied))
        {
            if (await ApplyTweakAsync(tweak.Id))
            {
                count++;
            }
        }
        return count;
    }

    public async Task<int> RevertAllAsync()
    {
        int count = 0;
        foreach (var tweak in _tweaks.Where(t => t.IsApplied))
        {
            if (await RevertTweakAsync(tweak.Id))
            {
                count++;
            }
        }
        return count;
    }

    #region Internal Registry Mechanics

    private bool IsTweakApplied(string tweakId)
    {
        return tweakId.ToLowerInvariant() switch
        {
            "telemetry-disable" =>
                GetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == 0,

            "bing-search-disable" =>
                GetDword(Registry.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") == 1 ||
                GetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled") == 0,

            "copilot-disable" =>
                GetDword(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1 ||
                GetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot") == 1,

            "activity-feed-disable" =>
                GetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed") == 0,

            "gamedvr-disable" =>
                GetDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") == 0,

            "mouse-accel-disable" =>
                GetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed") == "0",

            "classic-context-menu" =>
                KeyExists(Registry.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"),

            "sticky-keys-disable" =>
                GetString(Registry.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags") == "506",

            _ => false
        };
    }

    private bool ExecuteApply(string tweakId)
    {
        switch (tweakId.ToLowerInvariant())
        {
            case "telemetry-disable":
                SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                return true;

            case "bing-search-disable":
                SetDword(Registry.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1);
                SetDword(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0);
                return true;

            case "copilot-disable":
                SetDword(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                return true;

            case "activity-feed-disable":
                SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0);
                return true;

            case "gamedvr-disable":
                SetDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0);
                SetDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2);
                SetDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0);
                return true;

            case "mouse-accel-disable":
                SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0");
                SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0");
                SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0");
                return true;

            case "classic-context-menu":
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"))
                {
                    key?.SetValue("", "");
                }
                return true;

            case "sticky-keys-disable":
                SetString(Registry.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506");
                return true;

            default:
                return false;
        }
    }

    private bool ExecuteRevert(string tweakId)
    {
        switch (tweakId.ToLowerInvariant())
        {
            case "telemetry-disable":
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");
                return true;

            case "bing-search-disable":
                DeleteValue(Registry.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");
                DeleteValue(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled");
                return true;

            case "copilot-disable":
                DeleteValue(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot");
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot");
                return true;

            case "activity-feed-disable":
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed");
                return true;

            case "gamedvr-disable":
                SetDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1);
                DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
                return true;

            case "mouse-accel-disable":
                SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "1");
                SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "6");
                SetString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "10");
                return true;

            case "classic-context-menu":
                // A missing key is fine (throwOnMissing: false); access denied reaches RevertTweakAsync,
                // which logs it and reports the revert as failed instead of done.
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false);
                return true;

            case "sticky-keys-disable":
                SetString(Registry.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "510");
                return true;

            default:
                return false;
        }
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

    private static void SetDword(RegistryKey root, string subKeyPath, string valueName, int value)
    {
        using var key = root.CreateSubKey(subKeyPath);
        key?.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static void SetString(RegistryKey root, string subKeyPath, string valueName, string value)
    {
        using var key = root.CreateSubKey(subKeyPath);
        key?.SetValue(valueName, value, RegistryValueKind.String);
    }

    private static void DeleteValue(RegistryKey root, string subKeyPath, string valueName)
    {
        // Not swallowed: if the value cannot be removed, the tweak was not reverted, and the caller
        // (Apply/RevertTweakAsync) logs it and reports failure.
        using var key = root.OpenSubKey(subKeyPath, true);
        key?.DeleteValue(valueName, false);
    }

    #endregion
}
