using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Services;

namespace AxePower.Tests;

/// <summary>
/// A <see cref="PowerPlanService"/> wired to fakes. The real one switches the machine's active
/// power plan and the display's refresh rate, which from a test run changes the developer's PC
/// (and every refresh-rate change reconfigures attached displays).
/// </summary>
internal static class TestPowerPlan
{
    public static PowerPlanService Create(LogService log, SettingsService settings) =>
        new(log, new FakeRefreshRate(), settings, new FakePowerSchemes());
}

internal sealed class FakePowerSchemes : IPowerSchemeApi
{
    public Guid? Active { get; set; } = PowerPlanService.HighPerformanceGuid;
    public List<Guid> Schemes { get; } = new() { PowerPlanService.BalancedGuid, PowerPlanService.PowerSaverGuid, PowerPlanService.HighPerformanceGuid };
    public Dictionary<Guid, string> Names { get; } = new()
    {
        [PowerPlanService.BalancedGuid] = "Balanced",
        [PowerPlanService.PowerSaverGuid] = "Power saver",
        [PowerPlanService.HighPerformanceGuid] = "High performance"
    };

    public bool NativeCallSucceeds { get; set; } = true;
    public bool NativeCallTakesEffect { get; set; } = true;
    public bool PowercfgWorks { get; set; }
    public bool PowercfgReportsSuccess { get; set; }

    /// <summary>Whether the hidden Ultimate Performance template can be copied, as on most desktops.</summary>
    public bool UltimateTemplateAvailable { get; set; } = true;
    public bool DuplicateWorks { get; set; } = true;

    /// <summary>(source, destination) for every successful copy.</summary>
    public List<(Guid Source, Guid Destination)> Duplicates { get; } = new();

    /// <summary>(scheme, setting) → (ac, dc) as last written.</summary>
    public Dictionary<(Guid Scheme, Guid Setting), (uint Ac, uint Dc)> Values { get; } = new();

    public Guid? GetActiveScheme() => Active;

    public bool SetActiveScheme(Guid scheme)
    {
        if (!NativeCallSucceeds) return false;
        if (NativeCallTakesEffect) Active = scheme;
        return true;
    }

    public List<Guid> EnumerateSchemes() => new(Schemes);

    public string? ReadFriendlyName(Guid scheme) => Names.TryGetValue(scheme, out var name) ? name : null;

    public bool DuplicateScheme(Guid source, Guid destination)
    {
        bool sourceExists = Schemes.Contains(source) ||
                            (source == PowerPlanService.UltimatePerformanceTemplateGuid && UltimateTemplateAvailable);
        if (!DuplicateWorks || !sourceExists || Schemes.Contains(destination)) return false;

        Schemes.Add(destination);
        Names[destination] = ReadFriendlyName(source) ?? "Ultimate Performance";
        Duplicates.Add((source, destination));
        return true;
    }

    public bool WriteFriendlyName(Guid scheme, string name)
    {
        if (!Schemes.Contains(scheme)) return false;
        Names[scheme] = name;
        return true;
    }

    public bool WriteDescription(Guid scheme, string description) => Schemes.Contains(scheme);

    public bool WriteValueIndex(Guid scheme, Guid subgroup, Guid setting, uint acValue, uint dcValue)
    {
        if (!Schemes.Contains(scheme)) return false;
        Values[(scheme, setting)] = (acValue, dcValue);
        return true;
    }

    public Task<bool> SetActiveSchemeWithPowercfgAsync(Guid scheme)
    {
        if (PowercfgWorks) Active = scheme;
        return Task.FromResult(PowercfgWorks || PowercfgReportsSuccess);
    }
}

internal sealed class FakeRefreshRate : IRefreshRateController
{
    public int SmartCalls { get; private set; }
    public bool? LastUseLowest { get; private set; }
    public List<int> FixedRates { get; } = new();

    public bool SetRefreshRate(int targetFrequency)
    {
        FixedRates.Add(targetFrequency);
        return true;
    }

    public void SetSmartRefreshRate(bool useLowest)
    {
        SmartCalls++;
        LastUseLowest = useLowest;
    }
}
