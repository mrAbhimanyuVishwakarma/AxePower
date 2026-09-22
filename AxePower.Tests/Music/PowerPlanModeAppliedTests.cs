using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests.Music;

/// <summary>
/// <see cref="PowerPlanService.ModeApplied"/> must fire only after a mode is applied and verified.
/// Everything runs against fakes: no real power plan or display setting is ever touched.
/// </summary>
public class PowerPlanModeAppliedTests
{
    private static readonly Guid UltimateGuid = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task VerifiedNativeApply_RaisesModeAppliedOnce()
    {
        var (service, schemes, _) = Create();
        var raised = Record(service);

        bool ok = await service.ApplyModeAsync(PowerMode.Eco);

        Assert.True(ok);
        Assert.Equal(PowerPlanService.PowerSaverGuid, schemes.Active);
        Assert.Equal(new[] { PowerMode.Eco }, raised);
    }

    [Fact]
    public async Task NativeCallSucceedsButVerificationFails_AndPowercfgFails_RaisesNothing()
    {
        var (service, schemes, _) = Create();
        schemes.NativeCallTakesEffect = false;
        schemes.PowercfgWorks = false;
        var raised = Record(service);

        bool ok = await service.ApplyModeAsync(PowerMode.Eco);

        Assert.False(ok);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task NativeFails_PowercfgFallbackVerifies_RaisesModeAppliedOnce()
    {
        var (service, schemes, display) = Create();
        schemes.NativeCallSucceeds = false;
        schemes.PowercfgWorks = true;
        var raised = Record(service);

        bool ok = await service.ApplyModeAsync(PowerMode.Smart);

        Assert.True(ok);
        Assert.Equal(PowerPlanService.BalancedGuid, schemes.Active);
        Assert.Equal(new[] { PowerMode.Smart }, raised);
        // Existing behaviour on this path: the refresh rate is synced once
        Assert.Equal(1, display.SmartCalls);
    }

    [Fact]
    public async Task PowercfgReportsSuccessButSchemeDidNotChange_RaisesNothing()
    {
        var (service, schemes, _) = Create();
        schemes.NativeCallSucceeds = false;
        schemes.PowercfgWorks = false;
        schemes.PowercfgReportsSuccess = true;
        var raised = Record(service);

        bool ok = await service.ApplyModeAsync(PowerMode.Smart);

        Assert.False(ok);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotTurnAVerifiedApplyIntoAFailure()
    {
        var (service, _, _) = Create();
        var later = new List<PowerMode>();
        service.ModeApplied += (_, _) => throw new InvalidOperationException("subscriber bug");
        service.ModeApplied += (_, mode) => later.Add(mode);

        bool ok = await service.ApplyModeAsync(PowerMode.Eco);

        Assert.True(ok);
        Assert.Equal(new[] { PowerMode.Eco }, later);
    }

    [Fact]
    public async Task BeastWithExistingUltimatePlan_ReusesIt_AndNeverDuplicatesAnother()
    {
        var (service, schemes, _) = Create();
        schemes.Schemes.Add(UltimateGuid);
        schemes.Names[UltimateGuid] = "Ultimate Performance";
        var raised = Record(service);

        bool ok = await service.ApplyModeAsync(PowerMode.Beast);

        Assert.True(ok);
        Assert.Equal(UltimateGuid, schemes.Active);
        Assert.Equal(0, schemes.DuplicateCalls);
        Assert.Equal(new[] { PowerMode.Beast }, raised);
    }

    private static (PowerPlanService Service, FakePowerSchemes Schemes, FakeRefreshRate Display) Create()
    {
        var schemes = new FakePowerSchemes();
        var display = new FakeRefreshRate();
        var log = new LogService();
        var service = new PowerPlanService(log, display, new SettingsService(log), schemes);
        return (service, schemes, display);
    }

    private static List<PowerMode> Record(PowerPlanService service)
    {
        var raised = new List<PowerMode>();
        service.ModeApplied += (_, mode) => raised.Add(mode);
        return raised;
    }
}
