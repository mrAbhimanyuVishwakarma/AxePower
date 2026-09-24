using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests;

/// <summary>
/// AxePower's four named plans and the refresh-rate rule. Runs against fakes only: no real
/// power plan is created and no display is touched.
/// </summary>
public class PowerPlanCatalogTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), $"axepower_plans_{Guid.NewGuid():N}");
    private readonly LogService _log = new();
    private readonly FakePowerSchemes _schemes = new();
    private readonly FakeRefreshRate _display = new();
    private bool _onBattery;

    private PowerPlanService CreateService(SettingsService? settings = null) =>
        new(_log, _display, settings ?? new SettingsService(_log, _settingsDirectory), _schemes, () => _onBattery);

    public void Dispose()
    {
        try { if (Directory.Exists(_settingsDirectory)) Directory.Delete(_settingsDirectory, true); } catch { }
    }

    [Fact]
    public void Catalog_HasFourPlansWithDistinctGuidsAndTheModeNames()
    {
        var plans = PowerPlanCatalog.All;

        Assert.Equal(new[] { "Eco", "Smart", "Beast", "GameMode" }, plans.Select(p => p.Name));
        Assert.Equal(4, plans.Select(p => p.Guid).Distinct().Count());
        Assert.All(plans, p => Assert.Equal(p.Name, p.Mode.ToString()));
    }

    [Theory]
    [InlineData(PowerMode.Eco)]
    [InlineData(PowerMode.Smart)]
    [InlineData(PowerMode.Beast)]
    [InlineData(PowerMode.GameMode)]
    public async Task Apply_CreatesTheNamedPlanWithItsSettings_AndActivatesIt(PowerMode mode)
    {
        var service = CreateService();
        var plan = PowerPlanCatalog.For(mode);

        Assert.True(await service.ApplyModeAsync(mode));

        Assert.Equal(plan.Guid, _schemes.Active);
        Assert.Equal(plan.Name, _schemes.Names[plan.Guid]);
        foreach (var setting in plan.Settings)
            Assert.Equal((setting.Ac, setting.Dc), _schemes.Values[(plan.Guid, setting.Setting)]);
    }

    [Fact]
    public async Task Beast_IsCopiedFromUltimate_OrHighPerformanceWhenTheTemplateIsBlocked()
    {
        var service = CreateService();
        await service.ApplyModeAsync(PowerMode.Beast);
        Assert.Equal(PowerPlanService.UltimatePerformanceTemplateGuid, _schemes.Duplicates.Single().Source);

        _schemes.UltimateTemplateAvailable = false;
        await service.ApplyModeAsync(PowerMode.GameMode);
        Assert.Equal(PowerPlanService.HighPerformanceGuid, _schemes.Duplicates.Last().Source);
    }

    [Fact]
    public async Task Eco_OnAModernStandbyMachineWithOnlyBalanced_IsCopiedFromBalanced()
    {
        _schemes.Schemes.Remove(PowerPlanService.PowerSaverGuid);
        _schemes.Schemes.Remove(PowerPlanService.HighPerformanceGuid);

        Assert.True(await CreateService().ApplyModeAsync(PowerMode.Eco));

        Assert.Equal(PowerPlanService.BalancedGuid, _schemes.Duplicates.Single().Source);
        Assert.Equal(PowerPlanCatalog.Eco.Guid, _schemes.Active);
    }

    [Fact]
    public async Task ApplyingAgain_ReusesThePlan_RestoresItsName_AndRewritesItsSettings()
    {
        var service = CreateService();
        await service.ApplyModeAsync(PowerMode.Eco);
        var eco = PowerPlanCatalog.Eco;
        var cap = eco.Settings.First(s => s.Label.StartsWith("CPU maximum"));
        _schemes.Names[eco.Guid] = "Renamed by user";
        _schemes.Values[(eco.Guid, cap.Setting)] = (100, 100);

        await service.ApplyModeAsync(PowerMode.Eco);

        Assert.Single(_schemes.Duplicates);
        Assert.Equal("Eco", _schemes.Names[eco.Guid]);
        Assert.Equal((cap.Ac, cap.Dc), _schemes.Values[(eco.Guid, cap.Setting)]);
    }

    [Fact]
    public async Task WhenPlansCannotBeCreated_FallsBackToTheStockPlan()
    {
        _schemes.DuplicateWorks = false;
        var service = CreateService();

        Assert.True(await service.ApplyModeAsync(PowerMode.Eco));
        Assert.Equal(PowerPlanService.PowerSaverGuid, _schemes.Active);

        Assert.True(await service.ApplyModeAsync(PowerMode.Beast));
        Assert.Equal(PowerPlanService.HighPerformanceGuid, _schemes.Active);
    }

    [Fact]
    public async Task GameMode_ReadsBackAsGameMode_NotBeast()
    {
        var service = CreateService();
        await service.ApplyModeAsync(PowerMode.GameMode);

        Assert.Equal(PowerMode.GameMode, await service.GetActivePowerModeAsync());

        await service.ApplyModeAsync(PowerMode.Beast);
        Assert.Equal(PowerMode.Beast, await service.GetActivePowerModeAsync());
    }

    [Fact]
    public async Task NativeApply_SetsTheRefreshRate()
    {
        // Regression: a successful native apply used to return before the refresh-rate code
        await CreateService().ApplyModeAsync(PowerMode.Beast);

        Assert.Equal(1, _display.SmartCalls);
    }

    [Theory]
    [InlineData(PowerMode.Eco, true, true)]
    [InlineData(PowerMode.Eco, false, false)]
    [InlineData(PowerMode.Smart, true, false)]
    [InlineData(PowerMode.Beast, true, false)]
    [InlineData(PowerMode.GameMode, true, false)]
    public async Task RefreshRate_IsLowestOnlyForEcoOnBattery(PowerMode mode, bool onBattery, bool expectLowest)
    {
        _onBattery = onBattery;

        await CreateService().ApplyModeAsync(mode);

        Assert.Equal(expectLowest, _display.LastUseLowest);
    }

    [Fact]
    public async Task SyncRefreshRate_FollowsThePowerSourceForTheActiveMode()
    {
        var service = CreateService();
        _onBattery = true;
        await service.ApplyModeAsync(PowerMode.Eco);
        Assert.True(_display.LastUseLowest);

        _onBattery = false;
        await service.SyncRefreshRateAsync();

        Assert.False(_display.LastUseLowest);
    }

    [Fact]
    public async Task FixedRateFromSettings_OverridesTheRule()
    {
        var settings = new SettingsService(_log, _settingsDirectory);
        var current = settings.GetSettings();
        current.DisplayRefreshRate = 120;
        settings.SaveSettings(current);
        _onBattery = true;

        await CreateService(settings).ApplyModeAsync(PowerMode.Eco);

        Assert.Equal(new[] { 120 }, _display.FixedRates);
        Assert.Equal(0, _display.SmartCalls);
    }
}
