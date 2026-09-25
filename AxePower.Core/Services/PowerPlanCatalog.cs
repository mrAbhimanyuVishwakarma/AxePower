using System;
using System.Collections.Generic;
using System.Linq;
using AxePower.Core.Models;

namespace AxePower.Core.Services;

/// <summary>One power-plan setting written to both the AC and the battery (DC) side of a plan.</summary>
public sealed record PlanSetting(Guid Subgroup, Guid Setting, uint Ac, uint Dc, string Label);

/// <summary>
/// A Windows power plan AxePower owns. Each has a fixed GUID, so the active plan maps back to
/// exactly one mode (GameMode is no longer indistinguishable from Beast) and a user plan that
/// happens to share the name is never taken over.
/// </summary>
public sealed record AxePowerPlan(
    PowerMode Mode,
    Guid Guid,
    string Name,
    string Description,
    IReadOnlyList<Guid> Sources,
    IReadOnlyList<PlanSetting> Settings);

public static class PowerPlanCatalog
{
    // Subgroups and settings, as listed by `powercfg /qh`
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static readonly Guid PerfEnergyPreference = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
    private static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid PciExpressAspm = new("ee12f906-d277-404b-b6da-e5fa1a576df5");
    private static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid EnergySaverThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

    // Boost mode values: 0 disabled, 2 aggressive. ASPM: 0 off, 2 maximum power savings.
    private const uint BoostDisabled = 0;
    private const uint BoostAggressive = 2;
    private const uint AspmOff = 0;
    private const uint AspmMaxSavings = 2;

    public static readonly AxePowerPlan Eco = new(
        PowerMode.Eco,
        new Guid("c6e48611-9078-4644-ac4e-b1a64f82ef93"),
        "Eco",
        "AxePower Eco: longest battery life. CPU capped at 70% with no turbo; Energy Saver always on while on battery.",
        new[] { PowerPlanService.PowerSaverGuid, PowerPlanService.BalancedGuid },
        new[]
        {
            new PlanSetting(SubProcessor, ProcThrottleMin, 5, 5, "CPU minimum 5%"),
            new PlanSetting(SubProcessor, ProcThrottleMax, 70, 70, "CPU maximum 70%"),
            new PlanSetting(SubProcessor, PerfBoostMode, BoostDisabled, BoostDisabled, "Turbo off"),
            new PlanSetting(SubProcessor, PerfEnergyPreference, 80, 80, "Favour efficiency"),
            new PlanSetting(SubPciExpress, PciExpressAspm, AspmMaxSavings, AspmMaxSavings, "PCIe link power saving"),
            new PlanSetting(SubEnergySaver, EnergySaverThreshold, 100, 100, "Energy Saver on battery"),
        });

    /// <summary>A copy of Balanced; Smart's work is choosing between the plans, not tuning this one.</summary>
    public static readonly AxePowerPlan Smart = new(
        PowerMode.Smart,
        new Guid("e52a4182-aad1-4083-bc2b-a331d7960e14"),
        "Smart",
        "AxePower Smart: balanced everyday use.",
        new[] { PowerPlanService.BalancedGuid },
        Array.Empty<PlanSetting>());

    public static readonly AxePowerPlan Beast = new(
        PowerMode.Beast,
        new Guid("0510a366-0a10-4105-b341-ecc5553c7cca"),
        "Beast",
        "AxePower Beast: maximum performance. Full CPU clocks, aggressive turbo.",
        PerformanceSources,
        PerformanceSettings);

    public static readonly AxePowerPlan GameMode = new(
        PowerMode.GameMode,
        new Guid("073f1c31-01d8-4b9e-a1a1-c62a6f6fcccf"),
        "GameMode",
        "AxePower GameMode: maximum performance while a game is running.",
        PerformanceSources,
        PerformanceSettings);

    public static IReadOnlyList<AxePowerPlan> All { get; } = new[] { Eco, Smart, Beast, GameMode };

    public static AxePowerPlan For(PowerMode mode) => All.First(p => p.Mode == mode);

    public static AxePowerPlan? ForGuid(Guid guid) => All.FirstOrDefault(p => p.Guid == guid);

    private static IReadOnlyList<Guid> PerformanceSources => new[]
    {
        PowerPlanService.UltimatePerformanceTemplateGuid,
        PowerPlanService.HighPerformanceGuid,
        PowerPlanService.BalancedGuid,
    };

    private static IReadOnlyList<PlanSetting> PerformanceSettings => new[]
    {
        new PlanSetting(SubProcessor, ProcThrottleMin, 100, 100, "CPU minimum 100%"),
        new PlanSetting(SubProcessor, ProcThrottleMax, 100, 100, "CPU maximum 100%"),
        new PlanSetting(SubProcessor, PerfBoostMode, BoostAggressive, BoostAggressive, "Aggressive turbo"),
        new PlanSetting(SubProcessor, PerfEnergyPreference, 0, 0, "Favour performance"),
        new PlanSetting(SubPciExpress, PciExpressAspm, AspmOff, AspmOff, "PCIe link power saving off"),
        new PlanSetting(SubEnergySaver, EnergySaverThreshold, 0, 0, "Energy Saver never"),
    };
}
