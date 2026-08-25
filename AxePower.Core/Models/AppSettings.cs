using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models;

public class AppSettings
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("autoSwitchEnabled")]
    public bool AutoSwitchEnabled { get; set; } = true;

    [JsonPropertyName("applyExpectedModeOnStartup")]
    public bool ApplyExpectedModeOnStartup { get; set; } = true;

    [JsonPropertyName("pluggedInMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PowerMode PluggedInMode { get; set; } = PowerMode.Smart;

    [JsonPropertyName("unpluggedMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PowerMode UnpluggedMode { get; set; } = PowerMode.Eco;

    [JsonPropertyName("startWithLastSelectedMode")]
    public bool StartWithLastSelectedMode { get; set; } = false;

    [JsonPropertyName("lastSelectedPowerMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PowerMode LastSelectedPowerMode { get; set; } = PowerMode.Beast;

    [JsonPropertyName("fanModeOnBattery")]
    public string FanModeOnBattery { get; set; } = "Smart";

    [JsonPropertyName("lowBatteryRuleEnabled")]
    public bool LowBatteryRuleEnabled { get; set; } = true;

    [JsonPropertyName("lowBatteryThresholdPercent")]
    public int LowBatteryThresholdPercent { get; set; } = 30;

    [JsonPropertyName("lowBatteryMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PowerMode LowBatteryMode { get; set; } = PowerMode.Eco;

    [JsonPropertyName("switchDelaySeconds")]
    public int SwitchDelaySeconds { get; set; } = 5;

    [JsonPropertyName("showNotifications")]
    public bool ShowNotifications { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = false;

    [JsonPropertyName("startMinimised")]
    public bool StartMinimised { get; set; } = false;

    [JsonPropertyName("closeBehaviour")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CloseBehaviour CloseBehaviour { get; set; } = CloseBehaviour.MinimiseToTray;

    [JsonPropertyName("theme")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    [JsonPropertyName("reduceMotion")]
    public bool ReduceMotion { get; set; } = false;

    [JsonPropertyName("suspendBackgroundAppsOnBattery")]
    public bool SuspendBackgroundAppsOnBattery { get; set; } = true;

    [JsonPropertyName("appRulesEnabled")]
    public bool AppRulesEnabled { get; set; } = false;

    [JsonPropertyName("appRules")]
    public List<AppRule> AppRules { get; set; } = new();

    [JsonPropertyName("gameBoosterEnabled")]
    public bool GameBoosterEnabled { get; set; } = false;

    [JsonPropertyName("displayRefreshRate")]
    public int DisplayRefreshRate { get; set; } = 0; // 0 = Auto (synchronized with power mode), > 0 = manual override in Hz

    [JsonPropertyName("temperatureUnit")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    [JsonPropertyName("appsToClose")]
    public List<string> AppsToClose { get; set; } = new();

    public AppSettings Clone()
    {
        return new AppSettings
        {
            SchemaVersion = this.SchemaVersion,
            AutoSwitchEnabled = this.AutoSwitchEnabled,
            ApplyExpectedModeOnStartup = this.ApplyExpectedModeOnStartup,
            PluggedInMode = this.PluggedInMode,
            UnpluggedMode = this.UnpluggedMode,
            StartWithLastSelectedMode = this.StartWithLastSelectedMode,
            LastSelectedPowerMode = this.LastSelectedPowerMode,
            FanModeOnBattery = this.FanModeOnBattery,
            LowBatteryRuleEnabled = this.LowBatteryRuleEnabled,
            LowBatteryThresholdPercent = this.LowBatteryThresholdPercent,
            LowBatteryMode = this.LowBatteryMode,
            SwitchDelaySeconds = this.SwitchDelaySeconds,
            ShowNotifications = this.ShowNotifications,
            StartWithWindows = this.StartWithWindows,
            StartMinimised = this.StartMinimised,
            CloseBehaviour = this.CloseBehaviour,
            Theme = this.Theme,
            ReduceMotion = this.ReduceMotion,
            SuspendBackgroundAppsOnBattery = this.SuspendBackgroundAppsOnBattery,
            AppRulesEnabled = this.AppRulesEnabled,
            AppRules = new List<AppRule>(this.AppRules),
            GameBoosterEnabled = this.GameBoosterEnabled,
            DisplayRefreshRate = this.DisplayRefreshRate,
            TemperatureUnit = this.TemperatureUnit,
            AppsToClose = new List<string>(this.AppsToClose)
        };
    }
}
