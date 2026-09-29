using System;
using System.IO;
using AxePower.Core.Models;
using AxePower.Core.Services;

namespace AxePower.Tests;

public sealed class NotificationSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "AxePowerNotificationTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void MasterSwitch_BlocksEveryNotificationAtTheSharedService()
    {
        var settings = new SettingsService(new LogService(), _dir);
        var notifications = new NotificationService(settings);
        var seen = new List<string>();
        notifications.OnShowNotification += (_, message) => seen.Add(message);

        notifications.Show("visible");

        var changed = settings.GetSettings();
        changed.ShowNotifications = false;
        settings.SaveSettings(changed);
        notifications.Show("hidden");

        Assert.Equal(new[] { "visible" }, seen);
    }

    [Fact]
    public void NotificationPreferences_SurviveSaveAndReload()
    {
        var settings = new SettingsService(new LogService(), _dir);
        var changed = settings.GetSettings();
        changed.ShowNotifications = false;
        changed.NotifyOnModeSwitch = false;
        changed.LowBatteryAlertSound = false;
        settings.SaveSettings(changed);

        var reloaded = new SettingsService(new LogService(), _dir);
        reloaded.LoadSettings();
        var actual = reloaded.GetSettings();

        Assert.False(actual.ShowNotifications);
        Assert.False(actual.NotifyOnModeSwitch);
        Assert.False(actual.LowBatteryAlertSound);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AutomaticModeNotification_NeedsBothNotificationSwitches(
        bool showNotifications, bool notifyOnModeSwitch, bool expected)
    {
        var settings = new AppSettings
        {
            ShowNotifications = showNotifications,
            NotifyOnModeSwitch = notifyOnModeSwitch
        };

        Assert.Equal(expected, AutoSwitchService.ShouldNotifyModeSwitch(settings));
    }

    [Theory]
    [InlineData(true, false, true, false, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, true, true, false)]
    public void LowBatteryChime_PlaysOnceWhenCrossingTheThresholdOnBattery(
        bool enabled, bool wasLow, bool isLow, bool isPluggedIn, bool expected)
    {
        var settings = new AppSettings { LowBatteryAlertSound = enabled };

        Assert.Equal(expected,
            AutoSwitchService.ShouldPlayLowBatteryAlert(settings, wasLow, isLow, isPluggedIn));
    }

    [Fact]
    public void LowBatteryChime_DoesNotDependOnAutomaticModeSwitching()
    {
        var settings = new AppSettings
        {
            AutoSwitchEnabled = false,
            LowBatteryAlertSound = true
        };

        Assert.True(AutoSwitchService.ShouldPlayLowBatteryAlert(
            settings, wasLowBattery: false, isLowBattery: true, isPluggedIn: false));
    }
}
