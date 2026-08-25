using System;
using AxePower.Core.Models;
using Xunit;

namespace AxePower.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultSettings_AreSane()
    {
        var settings = new AppSettings();

        Assert.True(settings.AutoSwitchEnabled);
        Assert.True(settings.ShowNotifications);
        Assert.Equal(PowerMode.Smart, settings.PluggedInMode);
        Assert.Equal(PowerMode.Eco, settings.UnpluggedMode);
        Assert.False(settings.StartWithLastSelectedMode);
        Assert.Equal(PowerMode.Beast, settings.LastSelectedPowerMode);
        Assert.Equal(30, settings.LowBatteryThresholdPercent);
        Assert.Equal(AppTheme.System, settings.Theme);
    }

    [Fact]
    public void Clone_ReturnsDeepCopy()
    {
        var original = new AppSettings
        {
            AutoSwitchEnabled = false,
            LowBatteryThresholdPercent = 50,
            SwitchDelaySeconds = 15,
            StartWithLastSelectedMode = true,
            LastSelectedPowerMode = PowerMode.Beast,
            FanModeOnBattery = "Eco (Quiet)"
        };

        var cloned = original.Clone();

        Assert.NotSame(original, cloned);
        Assert.Equal(original.AutoSwitchEnabled, cloned.AutoSwitchEnabled);
        Assert.Equal(original.LowBatteryThresholdPercent, cloned.LowBatteryThresholdPercent);
        Assert.Equal(original.SwitchDelaySeconds, cloned.SwitchDelaySeconds);
        Assert.True(cloned.StartWithLastSelectedMode);
        Assert.Equal(PowerMode.Beast, cloned.LastSelectedPowerMode);
        Assert.Equal("Eco (Quiet)", cloned.FanModeOnBattery);
    }
}
