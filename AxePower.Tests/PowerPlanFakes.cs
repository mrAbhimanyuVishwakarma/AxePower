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
    public int DuplicateCalls { get; private set; }

    public Guid? GetActiveScheme() => Active;

    public bool SetActiveScheme(Guid scheme)
    {
        if (!NativeCallSucceeds) return false;
        if (NativeCallTakesEffect) Active = scheme;
        return true;
    }

    public List<Guid> EnumerateSchemes() => new(Schemes);

    public string? ReadFriendlyName(Guid scheme) => Names.TryGetValue(scheme, out var name) ? name : null;

    public Task<bool> DuplicateSchemeAsync(Guid template)
    {
        DuplicateCalls++;
        return Task.FromResult(false);
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

    public bool SetRefreshRate(int targetFrequency) => true;

    public void SetSmartRefreshRate(bool useLowest) => SmartCalls++;
}
