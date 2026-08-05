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
        Assert.Equal(PowerMode.Beast, settings.PluggedInMode);
        Assert.Equal(PowerMode.Eco, settings.UnpluggedMode);
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
            SwitchDelaySeconds = 15
        };

        var cloned = original.Clone();

        Assert.NotSame(original, cloned);
        Assert.Equal(original.AutoSwitchEnabled, cloned.AutoSwitchEnabled);
        Assert.Equal(original.LowBatteryThresholdPercent, cloned.LowBatteryThresholdPercent);
        Assert.Equal(original.SwitchDelaySeconds, cloned.SwitchDelaySeconds);
    }
}
