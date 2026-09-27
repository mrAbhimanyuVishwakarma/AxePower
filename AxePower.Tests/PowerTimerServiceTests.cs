using System;
using System.Collections.Generic;
using AxePower.Core.Services;

namespace AxePower.Tests;

public class PowerTimerServiceTests
{
    private DateTime _now = new(2026, 9, 25, 20, 0, 0, DateTimeKind.Utc);
    private readonly FakeActions _actions = new();

    private PowerTimerService Service() => new(_actions, () => _now, log: null, startClock: false);

    [Fact]
    public void RunsTheActionOnceTimeIsUp_AndNotBefore()
    {
        using var timer = Service();
        timer.Start(PowerTimerAction.Restart, TimeSpan.FromMinutes(30));

        _now += TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59);
        timer.Tick();
        Assert.Empty(_actions.Ran);
        Assert.True(timer.IsRunning);

        _now += TimeSpan.FromSeconds(1);
        timer.Tick();
        timer.Tick();
        Assert.Equal(new[] { PowerTimerAction.Restart }, _actions.Ran);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void WarnsOnceAtTwoMinutes_AndTurnsToWarningState()
    {
        using var timer = Service();
        int warnings = 0;
        timer.WarningReached += (_, _) => warnings++;
        timer.Start(PowerTimerAction.Shutdown, TimeSpan.FromMinutes(10));

        _now += TimeSpan.FromMinutes(7);
        timer.Tick();
        Assert.Equal(0, warnings);
        Assert.False(timer.IsInWarning);

        _now += TimeSpan.FromMinutes(1);
        timer.Tick();
        timer.Tick();
        Assert.Equal(1, warnings);
        Assert.True(timer.IsInWarning);
    }

    [Fact]
    public void Cancel_StopsIt()
    {
        using var timer = Service();
        timer.Start(PowerTimerAction.Sleep, TimeSpan.FromMinutes(5));
        timer.Cancel();

        _now += TimeSpan.FromHours(1);
        timer.Tick();

        Assert.Empty(_actions.Ran);
        Assert.Equal(TimeSpan.Zero, timer.Remaining);
    }

    [Fact]
    public void ExtendingOutOfTheWarning_RearmsIt()
    {
        using var timer = Service();
        int warnings = 0;
        timer.WarningReached += (_, _) => warnings++;
        timer.Start(PowerTimerAction.Shutdown, TimeSpan.FromMinutes(5));

        _now += TimeSpan.FromMinutes(4);
        timer.Tick();
        timer.Extend(TimeSpan.FromMinutes(10));
        Assert.False(timer.IsInWarning);

        _now += TimeSpan.FromMinutes(9);
        timer.Tick();
        Assert.Equal(2, warnings);
    }

    [Fact]
    public void AShortTimer_HasNoSeparateWarning_ButIsRedFromTheStart()
    {
        using var timer = Service();
        int warnings = 0;
        timer.WarningReached += (_, _) => warnings++;

        timer.Start(PowerTimerAction.Shutdown, TimeSpan.FromMinutes(1));
        timer.Tick();

        Assert.Equal(0, warnings);
        Assert.True(timer.IsInWarning);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 30)]
    [InlineData(60 * 48, 60 * 24)]
    public void DelayIsClampedToOneMinuteToADay(int askedMinutes, int expectedMinutes)
    {
        using var timer = Service();
        timer.Start(PowerTimerAction.Sleep, TimeSpan.FromMinutes(askedMinutes));
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), timer.Remaining);
    }

    private sealed class FakeActions : IPowerActions
    {
        public List<PowerTimerAction> Ran { get; } = new();
        public void Run(PowerTimerAction action) => Ran.Add(action);
    }
}
