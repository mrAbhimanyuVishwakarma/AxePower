using System;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Native;
using AxePower.Core.Helpers;

namespace AxePower.Core.Services;

public class PowerPlanService
{
    private readonly LogService _logService;
    private readonly DisplayService _displayService;
    private readonly SettingsService _settingsService;
    
    // Standard Windows GUIDs
    public static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaverGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformanceTemplateGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public PowerPlanService(LogService logService, DisplayService displayService, SettingsService settingsService)
    {
        _logService = logService;
        _displayService = displayService;
        _settingsService = settingsService;
    }

    public Task<PowerMode> GetActivePowerModeAsync()
    {
        var activeGuid = PowerInterop.GetActiveScheme();
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
        var name = PowerInterop.ReadFriendlyName(guid);
        if (name != null && name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PowerMode.Beast);
            
        if (name != null && name.Contains("Power saver", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PowerMode.Eco);

        return Task.FromResult(PowerMode.Smart);
    }

    private Guid? FindSchemeByKeyword(string keyword)
    {
        var schemes = PowerInterop.EnumerateSchemes();
        foreach (var scheme in schemes)
        {
            var name = PowerInterop.ReadFriendlyName(scheme);
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
        var createResult = await CommandRunner.RunAsync("powercfg", $"-duplicatescheme {UltimatePerformanceTemplateGuid}");
        if (createResult.Success)
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
            PowerMode.Eco => PowerInterop.EnumerateSchemes().Contains(PowerSaverGuid) ? PowerSaverGuid : FindSchemeByKeyword("Power saver"),
            PowerMode.Beast => await EnsureUltimatePerformanceExistsAsync(),
            PowerMode.GameMode => await EnsureUltimatePerformanceExistsAsync(),
            _ => PowerInterop.EnumerateSchemes().Contains(BalancedGuid) ? BalancedGuid : FindSchemeByKeyword("Balanced")
        };

        if (targetGuid == null)
        {
            // If the specific plan isn't found, try to fallback to Balanced.
            targetGuid = BalancedGuid;
            _logService.LogWarning($"Could not resolve GUID for mode {mode}. Falling back to Balanced.");
        }

        bool success = PowerInterop.SetActiveScheme(targetGuid.Value);
        
        // Verify application
        if (success)
        {
            var active = PowerInterop.GetActiveScheme();
            if (active == targetGuid.Value)
            {
                _logService.LogInfo($"Successfully applied and verified power mode: {mode} ({targetGuid.Value})");
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
            var res = await CommandRunner.RunAsync("powercfg", $"/setactive {targetGuid.Value}");
            if (res.Success)
            {
                var active = PowerInterop.GetActiveScheme();
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
        }

        return success;
    }
}
