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
    private readonly Func<bool> _isOnBattery;

    // Creating, cleaning up and deleting plans run one caller at a time: startup, Auto Switch and
    // app rules can all apply a mode at once, and two of them creating the same plan would
    // otherwise see each other's copy as a failure.
    private readonly object _planLock = new();

    // Standard Windows GUIDs. AxePower's own plans are copied from these (see PowerPlanCatalog).
    public static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaverGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformanceTemplateGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Eco on battery drops to this rate when the panel offers it.</summary>
    public const int EcoBatteryRefreshRate = 60;

    /// <summary>
    /// Raised after a mode has been applied <b>and</b> verified as the active scheme — never on a
    /// failed or unverified attempt. Every caller (Dashboard, tray, Auto Switch, app rules) goes
    /// through <see cref="ApplyModeAsync"/>, so this is the one place to observe mode changes.
    /// </summary>
    public event EventHandler<PowerMode>? ModeApplied;

    public PowerPlanService(LogService logService, DisplayService displayService, SettingsService settingsService, BatteryService batteryService)
        : this(logService, displayService, settingsService, new WindowsPowerSchemeApi(), () =>
        {
            var status = batteryService.GetCurrentStatus();
            return status.HasBattery && !status.IsPluggedIn;
        })
    {
    }

    /// <summary>Test seam: lets the apply-and-verify logic run without touching real power plans.</summary>
    internal PowerPlanService(LogService logService, IRefreshRateController displayService, SettingsService settingsService, IPowerSchemeApi schemes, Func<bool>? isOnBattery = null)
    {
        _logService = logService;
        _displayService = displayService;
        _settingsService = settingsService;
        _schemes = schemes;
        _isOnBattery = isOnBattery ?? (() => false);
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

        var owned = PowerPlanCatalog.ForGuid(guid);
        if (owned != null)
            return Task.FromResult(owned.Mode);

        // Not one of AxePower's plans: the user (or an older AxePower) picked a Windows plan
        if (guid == PowerSaverGuid)
            return Task.FromResult(PowerMode.Eco);

        if (guid == HighPerformanceGuid || guid == UltimatePerformanceTemplateGuid)
            return Task.FromResult(PowerMode.Beast);

        var name = _schemes.ReadFriendlyName(guid);
        if (name != null && name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PowerMode.Beast);

        if (name != null && name.Contains("Power saver", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(PowerMode.Eco);

        return Task.FromResult(PowerMode.Smart);
    }

    /// <summary>
    /// Makes sure the mode's own plan exists in Windows. A missing plan is created once, and only
    /// then are its name and settings written; an existing plan is used exactly as it is, so
    /// changes made to it in Control Panel stay.
    /// </summary>
    public Guid? EnsurePlan(AxePowerPlan plan)
    {
        if (_schemes.EnumerateSchemes().Contains(plan.Guid))
            return plan.Guid;

        bool created = false;
        foreach (var source in plan.Sources)
        {
            if (_schemes.DuplicateScheme(source, plan.Guid))
            {
                _logService.LogInfo($"Created power plan '{plan.Name}' ({plan.Guid}) from {source}.");
                created = true;
                break;
            }
        }

        // The copy is refused when the GUID already exists, e.g. created from outside AxePower
        // in the meantime. The plan is there either way, so use it without touching it.
        if (!created && _schemes.EnumerateSchemes().Contains(plan.Guid))
            return plan.Guid;

        if (!_schemes.EnumerateSchemes().Contains(plan.Guid))
        {
            _logService.LogError($"Could not create power plan '{plan.Name}' from any source plan.");
            return null;
        }

        if (!_schemes.WriteFriendlyName(plan.Guid, plan.Name))
            _logService.LogWarning($"Could not name power plan {plan.Guid} '{plan.Name}'.");
        _schemes.WriteDescription(plan.Guid, plan.Description);

        foreach (var setting in plan.Settings)
        {
            // Some settings do not exist on every CPU or Windows build; the plan still works without them
            if (!_schemes.WriteValueIndex(plan.Guid, setting.Subgroup, setting.Setting, setting.Ac, setting.Dc))
                _logService.LogWarning($"Power plan '{plan.Name}': could not write '{setting.Label}'.");
        }

        return plan.Guid;
    }

    /// <summary>Plans that are never deleted: Windows' own stock plans and AxePower's four.</summary>
    public static bool IsKeptPlan(Guid scheme) =>
        scheme == BalancedGuid || scheme == PowerSaverGuid || scheme == HighPerformanceGuid ||
        scheme == UltimatePerformanceTemplateGuid || PowerPlanCatalog.ForGuid(scheme) != null;

    /// <summary>
    /// Once per PC: creates all four plans, then deletes every other plan except Windows' stock
    /// ones (copies of Ultimate Performance, OEM and user plans alike). Nothing is deleted
    /// unless all four exist, so the PC is never left without AxePower's plans to switch to.
    /// The active plan is skipped because Windows refuses to delete it.
    /// </summary>
    public void InitializePlansOnce()
    {
        var settings = _settingsService.GetSettings();
        if (settings.PowerPlansInitialized)
            return;

        if (PowerPlanCatalog.All.Any(plan => EnsurePlan(plan) == null))
        {
            _logService.LogWarning("Not all AxePower power plans could be created; other plans were left in place.");
            return;
        }

        var active = _schemes.GetActiveScheme();
        foreach (var scheme in _schemes.EnumerateSchemes().Where(s => !IsKeptPlan(s) && s != active))
        {
            var name = _schemes.ReadFriendlyName(scheme);
            if (_schemes.DeleteScheme(scheme))
                _logService.LogInfo($"Removed power plan '{name}' ({scheme}).");
            else
                _logService.LogWarning($"Could not remove power plan '{name}' ({scheme}).");
        }

        settings.PowerPlansInitialized = true;
        _settingsService.SaveSettings(settings);
    }

    /// <summary>
    /// Deletes any plan that is not AxePower's own but carries one of its names (a Control Panel
    /// copy of "Eco", another app's "Beast"), so each name appears exactly once. Stock Windows
    /// plans and the active plan are never touched.
    /// </summary>
    public void RemoveSameNamedPlans()
    {
        var active = _schemes.GetActiveScheme();
        foreach (var scheme in _schemes.EnumerateSchemes().Where(s => !IsKeptPlan(s) && s != active))
        {
            var name = _schemes.ReadFriendlyName(scheme)?.Trim();
            if (name == null || !PowerPlanCatalog.All.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (_schemes.DeleteScheme(scheme))
                _logService.LogInfo($"Removed duplicate power plan '{name}' ({scheme}).");
            else
                _logService.LogWarning($"Could not remove duplicate power plan '{name}' ({scheme}).");
        }
    }

    /// <summary>Which of AxePower's four plans exist right now. Read-only, for the self-test.</summary>
    public IReadOnlyList<(AxePowerPlan Plan, bool Present)> GetPlanPresence()
    {
        var schemes = _schemes.EnumerateSchemes();
        return PowerPlanCatalog.All.Select(p => (p, schemes.Contains(p.Guid))).ToList();
    }

    public async Task<bool> ApplyModeAsync(PowerMode mode)
    {
        var plan = PowerPlanCatalog.For(mode);
        Guid? targetGuid;
        lock (_planLock)
        {
            targetGuid = EnsurePlan(plan);
        }

        if (targetGuid == null)
        {
            // Creating plans failed (policy, corrupt power store): use the stock plan instead
            targetGuid = FallbackStockPlan(mode);
            _logService.LogWarning($"Using stock plan {targetGuid} for mode {mode}.");
        }

        bool success = false;
        if (_schemes.SetActiveScheme(targetGuid.Value))
        {
            var active = _schemes.GetActiveScheme();
            success = active == targetGuid.Value;
            if (!success)
                _logService.LogError($"Failed to verify mode {mode}. Active scheme is {active}");
        }
        else
        {
            _logService.LogError($"Failed to apply power mode: {mode} natively.");
        }

        if (!success)
        {
            _logService.LogInfo("Falling back to powercfg.exe for mode application.");
            if (await _schemes.SetActiveSchemeWithPowercfgAsync(targetGuid.Value))
            {
                success = _schemes.GetActiveScheme() == targetGuid.Value;
            }
        }

        if (!success)
        {
            _logService.LogError($"Failed to apply mode {mode} even with powercfg.");
            return false;
        }

        _logService.LogInfo($"Applied and verified power mode: {mode} ({targetGuid.Value})");
        try
        {
            // After the switch, so the plan being left is no longer active and can be removed
            lock (_planLock)
            {
                InitializePlansOnce();
                RemoveSameNamedPlans();
            }
        }
        catch (Exception ex)
        {
            _logService.LogError("Power plan cleanup failed.", ex);
        }
        ApplyRefreshRate(mode);
        RaiseModeApplied(mode);
        return true;
    }

    /// <summary>
    /// Re-applies the refresh-rate rule for the active mode. Called when the power source changes,
    /// since Eco's rate depends on whether the machine is on battery.
    /// </summary>
    public async Task SyncRefreshRateAsync()
    {
        ApplyRefreshRate(await GetActivePowerModeAsync());
    }

    /// <summary>
    /// Eco on battery runs at 60 Hz; everything else, and anything on AC (a desktop always is),
    /// runs at the panel's highest rate. A fixed rate chosen in Settings overrides both.
    /// </summary>
    private void ApplyRefreshRate(PowerMode mode)
    {
        try
        {
            var settings = _settingsService.GetSettings();
            if (settings.DisplayRefreshRate > 0)
            {
                _displayService.SetRefreshRate(settings.DisplayRefreshRate);
                return;
            }

            _displayService.SetSmartRefreshRate(useLowest: mode == PowerMode.Eco && _isOnBattery());
        }
        catch (Exception ex)
        {
            _logService.LogError($"Could not set the refresh rate for mode {mode}.", ex);
        }
    }

    private Guid FallbackStockPlan(PowerMode mode)
    {
        var schemes = _schemes.EnumerateSchemes();
        return mode switch
        {
            PowerMode.Eco when schemes.Contains(PowerSaverGuid) => PowerSaverGuid,
            PowerMode.Beast or PowerMode.GameMode when schemes.Contains(HighPerformanceGuid) => HighPerformanceGuid,
            _ => BalancedGuid
        };
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
/// through the fixed operations below, never with free-form arguments.
/// </summary>
internal interface IPowerSchemeApi
{
    Guid? GetActiveScheme();
    bool SetActiveScheme(Guid scheme);
    List<Guid> EnumerateSchemes();
    string? ReadFriendlyName(Guid scheme);
    bool DuplicateScheme(Guid source, Guid destination);
    bool DeleteScheme(Guid scheme);
    bool WriteFriendlyName(Guid scheme, string name);
    bool WriteDescription(Guid scheme, string description);
    bool WriteValueIndex(Guid scheme, Guid subgroup, Guid setting, uint acValue, uint dcValue);
    Task<bool> SetActiveSchemeWithPowercfgAsync(Guid scheme);
}

internal sealed class WindowsPowerSchemeApi : IPowerSchemeApi
{
    public Guid? GetActiveScheme() => PowerInterop.GetActiveScheme();

    public bool SetActiveScheme(Guid scheme) => PowerInterop.SetActiveScheme(scheme);

    public List<Guid> EnumerateSchemes() => PowerInterop.EnumerateSchemes();

    public string? ReadFriendlyName(Guid scheme) => PowerInterop.ReadFriendlyName(scheme);

    public bool DuplicateScheme(Guid source, Guid destination) => PowerInterop.DuplicateScheme(source, destination);

    public bool DeleteScheme(Guid scheme) => PowerInterop.DeleteScheme(scheme);

    public bool WriteFriendlyName(Guid scheme, string name) => PowerInterop.WriteFriendlyName(scheme, name);

    public bool WriteDescription(Guid scheme, string description) => PowerInterop.WriteDescription(scheme, description);

    public bool WriteValueIndex(Guid scheme, Guid subgroup, Guid setting, uint acValue, uint dcValue) =>
        PowerInterop.WriteValueIndex(scheme, subgroup, setting, acValue, dcValue);

    public async Task<bool> SetActiveSchemeWithPowercfgAsync(Guid scheme) =>
        (await CommandRunner.RunAsync("powercfg", $"/setactive {scheme}")).Success;
}
