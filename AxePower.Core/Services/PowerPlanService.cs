using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Native;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services;

public class PowerPlanService
{
    private readonly LogService _logService;
    private readonly IRefreshRateController _displayService;
    private readonly SettingsService _settingsService;
    private readonly IPowerSchemeApi _schemes;

    // Standard Windows GUIDs
    public static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaverGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformanceTemplateGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>
    /// Raised after a mode has been applied <b>and</b> verified as the active scheme — never on a
    /// failed or unverified attempt. Every caller (Dashboard, tray, Auto Switch, app rules) goes
    /// through <see cref="ApplyModeAsync"/>, so this is the one place to observe mode changes.
    /// </summary>
    public event EventHandler<PowerMode>? ModeApplied;

    public PowerPlanService(LogService logService, DisplayService displayService, SettingsService settingsService)
        : this(logService, displayService, settingsService, new WindowsPowerSchemeApi())
    {
    }

    /// <summary>Test seam: lets the apply-and-verify logic run without touching real power plans.</summary>
    internal PowerPlanService(LogService logService, IRefreshRateController displayService, SettingsService settingsService, IPowerSchemeApi schemes)
    {
        _logService = logService;
        _displayService = displayService;
        _settingsService = settingsService;
        _schemes = schemes;
    }

    public Task<PowerMode> GetActivePowerModeAsync()
    {
        var activeGuid = _schemes.GetActiveScheme();
        if (activeGuid == null)
        {
            _logService.LogWarning("Could not determine active power scheme via native API. Assuming Smart/Balanced.");
            return Task.FromResult(PowerMode.Smart);
        }

        var guid = activeGuid.Value;

        if (guid == PowerSaverGuid)
            return Task.FromResult(PowerMode.Eco);

        if (guid == HighPerformanceGuid || guid == UltimatePerformanceTemplateGuid)
            return Task.FromResult(PowerMode.Beast);

        // Check if there is an Ultimate Performance scheme that is custom
        var name = _schemes.ReadFriendlyName(guid);
        if (name != null && name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PowerMode.Beast);

        if (name != null && name.Contains("Power saver", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PowerMode.Eco);

        return Task.FromResult(PowerMode.Smart);
    }

    private Guid? FindSchemeByKeyword(string keyword)
    {
        var schemes = _schemes.EnumerateSchemes();
        foreach (var scheme in schemes)
        {
            var name = _schemes.ReadFriendlyName(scheme);
            if (name != null && name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return scheme;
            }
        }
        return null;
    }

    public async Task<Guid?> EnsureUltimatePerformanceExistsAsync()
    {
        var existingGuid = FindSchemeByKeyword("Ultimate Performance");
        if (existingGuid != null)
        {
            return existingGuid;
        }

        _logService.LogInfo("Ultimate Performance plan not found natively. Attempting to duplicate using powercfg.");
        if (await _schemes.DuplicateSchemeAsync(UltimatePerformanceTemplateGuid))
        {
            _logService.LogInfo("Successfully ran duplicate scheme command.");
            // Re-enumerate to find it
            return FindSchemeByKeyword("Ultimate Performance") ?? HighPerformanceGuid;
        }

        _logService.LogWarning("Failed to create Ultimate Performance scheme. Falling back to High Performance.");
        return HighPerformanceGuid;
    }

    public async Task<bool> ApplyModeAsync(PowerMode mode)
    {
        Guid? targetGuid = mode switch
        {
            PowerMode.Eco => _schemes.EnumerateSchemes().Contains(PowerSaverGuid) ? PowerSaverGuid : FindSchemeByKeyword("Power saver"),
            PowerMode.Beast => await EnsureUltimatePerformanceExistsAsync(),
            PowerMode.GameMode => await EnsureUltimatePerformanceExistsAsync(),
            _ => _schemes.EnumerateSchemes().Contains(BalancedGuid) ? BalancedGuid : FindSchemeByKeyword("Balanced")
        };

        if (targetGuid == null)
        {
            // If the specific plan isn't found, try to fallback to Balanced.
            targetGuid = BalancedGuid;
            _logService.LogWarning($"Could not resolve GUID for mode {mode}. Falling back to Balanced.");
        }

        bool success = _schemes.SetActiveScheme(targetGuid.Value);

        // Verify application
        if (success)
        {
            var active = _schemes.GetActiveScheme();
            if (active == targetGuid.Value)
            {
                _logService.LogInfo($"Successfully applied and verified power mode: {mode} ({targetGuid.Value})");
                RaiseModeApplied(mode);
                return true;
            }
            else
            {
                _logService.LogError($"Failed to verify mode {mode}. Active scheme is {active}");
                success = false;
            }
        }
        else
        {
            _logService.LogError($"Failed to apply power mode: {mode} natively.");
        }

        // Fallback to powercfg if native API failed
        if (!success)
        {
            _logService.LogInfo("Falling back to powercfg.exe for mode application.");
            if (await _schemes.SetActiveSchemeWithPowercfgAsync(targetGuid.Value))
            {
                var active = _schemes.GetActiveScheme();
                if (active == targetGuid.Value)
                {
                    _logService.LogInfo($"Successfully applied and verified power mode {mode} via powercfg.");
                    success = true;
                }
            }
            if (!success)
            {
                _logService.LogError($"Failed to apply mode {mode} even with powercfg.");
            }
        }

        if (success)
        {
            var settings = _settingsService.GetSettings();
            if (settings.DisplayRefreshRate > 0)
            {
                _displayService.SetRefreshRate(settings.DisplayRefreshRate);
            }
            else
            {
                _displayService.SetSmartRefreshRate(mode == PowerMode.Eco);
            }

            RaiseModeApplied(mode);
        }

        return success;
    }

    /// <summary>
    /// A misbehaving subscriber must not turn a verified apply into a reported failure, so each
    /// handler runs isolated and its exception is only logged.
    /// </summary>
    private void RaiseModeApplied(PowerMode mode)
    {
        var handlers = ModeApplied;
        if (handlers == null) return;

        foreach (EventHandler<PowerMode> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, mode);
            }
            catch (Exception ex)
            {
                _logService.LogError($"A ModeApplied subscriber failed for mode {mode}.", ex);
            }
        }
    }
}

/// <summary>The display calls <see cref="PowerPlanService"/> makes after a mode change.</summary>
internal interface IRefreshRateController
{
    bool SetRefreshRate(int targetFrequency);
    void SetSmartRefreshRate(bool useLowest);
}

/// <summary>
/// The Windows power-scheme calls <see cref="PowerPlanService"/> makes. powercfg is reached only
/// through the two fixed operations below, never with free-form arguments.
/// </summary>
internal interface IPowerSchemeApi
{
    Guid? GetActiveScheme();
    bool SetActiveScheme(Guid scheme);
    List<Guid> EnumerateSchemes();
    string? ReadFriendlyName(Guid scheme);
    Task<bool> DuplicateSchemeAsync(Guid template);
    Task<bool> SetActiveSchemeWithPowercfgAsync(Guid scheme);
}

internal sealed class WindowsPowerSchemeApi : IPowerSchemeApi
{
    public Guid? GetActiveScheme() => PowerInterop.GetActiveScheme();

    public bool SetActiveScheme(Guid scheme) => PowerInterop.SetActiveScheme(scheme);

    public List<Guid> EnumerateSchemes() => PowerInterop.EnumerateSchemes();

    public string? ReadFriendlyName(Guid scheme) => PowerInterop.ReadFriendlyName(scheme);

    public async Task<bool> DuplicateSchemeAsync(Guid template) =>
        (await CommandRunner.RunAsync("powercfg", $"-duplicatescheme {template}")).Success;

    public async Task<bool> SetActiveSchemeWithPowercfgAsync(Guid scheme) =>
        (await CommandRunner.RunAsync("powercfg", $"/setactive {scheme}")).Success;
}
