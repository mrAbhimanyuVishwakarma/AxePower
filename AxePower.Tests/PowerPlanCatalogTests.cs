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
    public async Task Beast_IsCopiedFromTheUltimateTemplate()
    {
        await CreateService().ApplyModeAsync(PowerMode.Beast);

        Assert.Equal(PowerPlanService.UltimatePerformanceTemplateGuid, SourceOf(PowerPlanCatalog.Beast));
        Assert.Equal(PowerPlanService.UltimatePerformanceTemplateGuid, SourceOf(PowerPlanCatalog.GameMode));
    }

    [Fact]
    public async Task Beast_IsCopiedFromHighPerformance_WhenTheUltimateTemplateIsBlocked()
    {
        _schemes.UltimateTemplateAvailable = false;

        await CreateService().ApplyModeAsync(PowerMode.GameMode);

        Assert.Equal(PowerPlanService.HighPerformanceGuid, SourceOf(PowerPlanCatalog.GameMode));
    }

    private Guid SourceOf(AxePowerPlan plan) => _schemes.Duplicates.Single(d => d.Destination == plan.Guid).Source;

    [Fact]
    public async Task Eco_OnAModernStandbyMachineWithOnlyBalanced_IsCopiedFromBalanced()
    {
        _schemes.Schemes.Remove(PowerPlanService.PowerSaverGuid);
        _schemes.Schemes.Remove(PowerPlanService.HighPerformanceGuid);

        Assert.True(await CreateService().ApplyModeAsync(PowerMode.Eco));

        Assert.Equal(PowerPlanService.BalancedGuid, SourceOf(PowerPlanCatalog.Eco));
        Assert.Equal(PowerPlanCatalog.Eco.Guid, _schemes.Active);
    }

    [Fact]
    public async Task ApplyingAgain_UsesTheExistingPlanAsIs_WithoutRecreatingOrRewritingIt()
    {
        var service = CreateService();
        await service.ApplyModeAsync(PowerMode.Eco);
        var eco = PowerPlanCatalog.Eco;
        var cap = eco.Settings.First(s => s.Label.StartsWith("CPU maximum"));
        _schemes.Names[eco.Guid] = "Renamed by user";
        _schemes.Values[(eco.Guid, cap.Setting)] = (100, 100);
        int created = _schemes.Duplicates.Count;

        await service.ApplyModeAsync(PowerMode.Eco);

        Assert.Equal(created, _schemes.Duplicates.Count);
        Assert.Equal("Renamed by user", _schemes.Names[eco.Guid]);
        Assert.Equal((100u, 100u), _schemes.Values[(eco.Guid, cap.Setting)]);
        Assert.Equal(eco.Guid, _schemes.Active);
    }

    [Fact]
    public async Task FirstApply_CreatesAllFourPlans_AndRemovesEveryNonStockPlan()
    {
        var ultimateCopy = Guid.NewGuid();
        var oemPlan = Guid.NewGuid();
        _schemes.Schemes.AddRange(new[] { ultimateCopy, oemPlan });
        _schemes.Names[ultimateCopy] = "Ultimate Performance";
        _schemes.Names[oemPlan] = "LOQ Gaming Mod";
        _schemes.Active = oemPlan;
        var settings = new SettingsService(_log, _settingsDirectory);

        Assert.True(await CreateService(settings).ApplyModeAsync(PowerMode.Smart));

        Assert.All(PowerPlanCatalog.All, p => Assert.Contains(p.Guid, _schemes.Schemes));
        Assert.Equivalent(new[] { ultimateCopy, oemPlan }, _schemes.Deleted);
        Assert.Contains(PowerPlanService.BalancedGuid, _schemes.Schemes);
        Assert.Contains(PowerPlanService.PowerSaverGuid, _schemes.Schemes);
        Assert.Contains(PowerPlanService.HighPerformanceGuid, _schemes.Schemes);
        Assert.True(settings.GetSettings().PowerPlansInitialized);
    }

    [Fact]
    public async Task Cleanup_RunsOnlyOncePerPc()
    {
        var settings = new SettingsService(_log, _settingsDirectory);
        var service = CreateService(settings);
        await service.ApplyModeAsync(PowerMode.Smart);

        var laterPlan = Guid.NewGuid();
        _schemes.Schemes.Add(laterPlan);
        await service.ApplyModeAsync(PowerMode.Beast);

        Assert.Contains(laterPlan, _schemes.Schemes);
    }

    [Fact]
    public async Task APlanDeletedLater_IsCreatedAgainWhenItsModeIsSelected()
    {
        var service = CreateService();
        await service.ApplyModeAsync(PowerMode.Smart);
        var eco = PowerPlanCatalog.Eco;
        _schemes.Schemes.Remove(eco.Guid);

        Assert.True(await service.ApplyModeAsync(PowerMode.Eco));

        Assert.Equal(eco.Guid, _schemes.Active);
        Assert.Equal("Eco", _schemes.Names[eco.Guid]);
        Assert.Equal(2, _schemes.Duplicates.Count(d => d.Destination == eco.Guid));
    }

    [Fact]
    public async Task EveryApply_RemovesForeignPlansWithAnAxePowerName_EvenAfterTheOneTimeCleanup()
    {
        var service = CreateService();
        await service.ApplyModeAsync(PowerMode.Smart);

        var copyOfEco = Guid.NewGuid();
        var otherBeast = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        _schemes.Schemes.AddRange(new[] { copyOfEco, otherBeast, unrelated });
        _schemes.Names[copyOfEco] = "Eco";
        _schemes.Names[otherBeast] = " beast ";
        _schemes.Names[unrelated] = "My plan";
        _schemes.Active = copyOfEco;

        await service.ApplyModeAsync(PowerMode.Eco);

        Assert.DoesNotContain(copyOfEco, _schemes.Schemes);
        Assert.DoesNotContain(otherBeast, _schemes.Schemes);
        Assert.Contains(unrelated, _schemes.Schemes);
        Assert.All(PowerPlanCatalog.All, p => Assert.Single(_schemes.Schemes, s => _schemes.Names[s].Trim().Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SimultaneousApplies_CreateThePlanOnce_AndAllSucceed()
    {
        var service = CreateService();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.ApplyModeAsync(PowerMode.Eco))));

        Assert.All(results, Assert.True);
        Assert.Single(_schemes.Duplicates, d => d.Destination == PowerPlanCatalog.Eco.Guid);
        Assert.Equal(PowerPlanCatalog.Eco.Guid, _schemes.Active);
    }

    [Fact]
    public async Task WhenAPlanCannotBeCreated_NothingIsDeleted_AndCleanupIsRetriedLater()
    {
        var other = Guid.NewGuid();
        _schemes.Schemes.Add(other);
        _schemes.DuplicateWorks = false;
        var settings = new SettingsService(_log, _settingsDirectory);

        await CreateService(settings).ApplyModeAsync(PowerMode.Smart);

        Assert.Empty(_schemes.Deleted);
        Assert.False(settings.GetSettings().PowerPlansInitialized);
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
