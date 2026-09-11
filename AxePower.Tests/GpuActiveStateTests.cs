using AxePower.Core.Services;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// The dashboard once flipped between the discrete and integrated GPU every few seconds
/// because the active-GPU choice had no hysteresis. These lock that behaviour down.
/// </summary>
public class GpuActiveStateTests
{
    private static (bool Active, int Idle) Feed(bool active, int idle, params double[] samples)
    {
        foreach (var s in samples)
            (active, idle) = SystemTelemetryService.EvaluateDedicatedGpuState(active, idle, s);
        return (active, idle);
    }

    [Fact]
    public void RealLoad_EngagesDiscreteGpu()
    {
        var (active, _) = Feed(false, 0, 45);
        Assert.True(active);
    }

    [Fact]
    public void BriefDips_DoNotFlipBackToIntegrated()
    {
        // A game running: load bounces around but never idles for long
        var (active, _) = Feed(true, 0, 60, 8, 1, 55, 2, 40, 0, 70);
        Assert.True(active);
    }

    [Fact]
    public void DeadBandLoad_HoldsCurrentState()
    {
        // Between the on and off thresholds nothing should change either way
        var (stillActive, _) = Feed(true, 0, 5, 6, 7);
        Assert.True(stillActive);

        var (stillIdle, _) = Feed(false, 0, 5, 6, 7);
        Assert.False(stillIdle);
    }

    [Fact]
    public void SustainedIdle_FallsBackToIntegrated()
    {
        var (active, _) = Feed(true, 0, 0, 0, 0);
        Assert.False(active);
    }

    [Fact]
    public void IdleStreak_ResetsWhenLoadReturns()
    {
        // Two idle samples, then real load, then two more idle samples: must stay on the
        // discrete GPU because the streak never reached the required length.
        var (active, _) = Feed(true, 0, 0, 0, 50, 0, 0);
        Assert.True(active);
    }

    [Fact]
    public void OscillatingLoad_ProducesNoFlapping()
    {
        bool active = false;
        int idle = 0;
        int transitions = 0;

        // Alternating busy/idle samples — the pathological case that caused the flicker
        for (int i = 0; i < 20; i++)
        {
            bool previous = active;
            (active, idle) = SystemTelemetryService.EvaluateDedicatedGpuState(active, idle, i % 2 == 0 ? 40 : 0);
            if (active != previous) transitions++;
        }

        // Only the initial engage should occur, never a flip-flop
        Assert.Equal(1, transitions);
        Assert.True(active);
    }
}
