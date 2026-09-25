using System.Collections.Generic;
using System.Text.Json.Serialization;
using AxePower.Core.Models.Music;
using AxePower.Core.Models.Uninstaller;
using AxePower.Core.Models.ShareScreen;

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

    /// <summary>Set once AxePower has created its four plans and removed every other non-stock plan on this PC.</summary>
    [JsonPropertyName("powerPlansInitialized")]
    public bool PowerPlansInitialized { get; set; } = false;

    /// <summary>While Game Mode is on, give every known game Windows' "High performance" GPU.</summary>
    [JsonPropertyName("gameModeUseDedicatedGpu")]
    public bool GameModeUseDedicatedGpu { get; set; } = false;

    /// <summary>
    /// exe path → the UserGpuPreferences value it had before Game Mode changed it (null: none).
    /// Only entries AxePower changed are listed, and they are put back when Game Mode ends.
    /// </summary>
    [JsonPropertyName("gpuPreferenceBackups")]
    public Dictionary<string, string?> GpuPreferenceBackups { get; set; } = new();

    [JsonPropertyName("lastSelectedPowerMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PowerMode LastSelectedPowerMode { get; set; } = PowerMode.Beast;

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

    [JsonPropertyName("notifyOnModeSwitch")]
    public bool NotifyOnModeSwitch { get; set; } = true;

    [JsonPropertyName("lowBatteryAlertSound")]
    public bool LowBatteryAlertSound { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = true;

    [JsonPropertyName("startMinimised")]
    public bool StartMinimised { get; set; } = true;

    /// <summary>
    /// Run AxePower elevated. On by default: the hardware sensors (CPU package temperature via
    /// MSR), the tweak/debloat actions and the terminal all need administrator rights, and an
    /// upgrade from a settings file written before this key existed lands on the default too.
    /// The manifest stays asInvoker — elevation is acquired by relaunching, so the toggle can
    /// still be turned off, and the startup task uses HighestAvailable so boot needs no prompt.
    /// </summary>
    [JsonPropertyName("runAsAdministrator")]
    public bool RunAsAdministrator { get; set; } = true;

    [JsonPropertyName("closeBehaviour")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CloseBehaviour CloseBehaviour { get; set; } = CloseBehaviour.MinimiseToTray;

    [JsonPropertyName("theme")]
    [JsonConverter(typeof(AppThemeJsonConverter))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// The chosen skin's id (skins\&lt;id&gt;\skin.json). Null follows <see cref="Theme"/>: System
    /// switches between the light and dark skins with Windows, and Dark/Light/Glass pick those.
    /// </summary>
    [JsonPropertyName("skinId")]
    public string? SkinId { get; set; }

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

    /// <summary>Service names ticked in Game Mode's System Services list (stopped by Optimize Services and Game Mode).</summary>
    [JsonPropertyName("servicesToOptimize")]
    public List<string> ServicesToOptimize { get; set; } = new();

    [JsonPropertyName("conservationModeEnabled")]
    public bool ConservationModeEnabled { get; set; } = false;

    [JsonPropertyName("rapidChargeEnabled")]
    public bool RapidChargeEnabled { get; set; } = false;

    [JsonPropertyName("overnightBatteryChargingEnabled")]
    public bool OvernightBatteryChargingEnabled { get; set; } = false;

    [JsonPropertyName("fnLockEnabled")]
    public bool FnLockEnabled { get; set; } = false;

    [JsonPropertyName("touchpadEnabled")]
    public bool TouchpadEnabled { get; set; } = true;

    [JsonPropertyName("autoCheckUpdates")]
    public bool AutoCheckUpdates { get; set; } = true;

    [JsonPropertyName("autoDownloadUpdates")]
    public bool AutoDownloadUpdates { get; set; } = true;

    [JsonPropertyName("notifyOnNewRelease")]
    public bool NotifyOnNewRelease { get; set; } = true;

    /// <summary>Sidebar group state, remembered across launches. Both groups start expanded.</summary>
    [JsonPropertyName("sidebarMaintenanceExpanded")]
    public bool SidebarMaintenanceExpanded { get; set; } = true;

    [JsonPropertyName("sidebarUtilitiesExpanded")]
    public bool SidebarUtilitiesExpanded { get; set; } = true;

    /// <summary>The user's last collapse/expand click. A narrow window collapses the sidebar on its
    /// own; this is what it returns to when the window is wide again.</summary>
    [JsonPropertyName("sidebarCollapsed")]
    public bool SidebarCollapsed { get; set; } = false;

    /// <summary>AxePower Music preferences. A settings file without this key loads the defaults.</summary>
    [JsonPropertyName("music")]
    public MusicSettings Music { get; set; } = new();

    /// <summary>Uninstaller Pro preferences. A settings file without this key loads the defaults.</summary>
    [JsonPropertyName("uninstaller")]
    public UninstallerSettings Uninstaller { get; set; } = new();

    /// <summary>Share Screen preferences. A settings file without this key loads the defaults.</summary>
    [JsonPropertyName("shareScreen")]
    public ShareScreenSettings ShareScreen { get; set; } = new();

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
            PowerPlansInitialized = this.PowerPlansInitialized,
            GameModeUseDedicatedGpu = this.GameModeUseDedicatedGpu,
            GpuPreferenceBackups = new Dictionary<string, string?>(this.GpuPreferenceBackups ?? new(), StringComparer.OrdinalIgnoreCase),
            LowBatteryRuleEnabled = this.LowBatteryRuleEnabled,
            LowBatteryThresholdPercent = this.LowBatteryThresholdPercent,
            LowBatteryMode = this.LowBatteryMode,
            SwitchDelaySeconds = this.SwitchDelaySeconds,
            ShowNotifications = this.ShowNotifications,
            NotifyOnModeSwitch = this.NotifyOnModeSwitch,
            LowBatteryAlertSound = this.LowBatteryAlertSound,
            StartWithWindows = this.StartWithWindows,
            StartMinimised = this.StartMinimised,
            RunAsAdministrator = this.RunAsAdministrator,
            CloseBehaviour = this.CloseBehaviour,
            Theme = this.Theme,
            SkinId = this.SkinId,
            ReduceMotion = this.ReduceMotion,
            SuspendBackgroundAppsOnBattery = this.SuspendBackgroundAppsOnBattery,
            AppRulesEnabled = this.AppRulesEnabled,
            AppRules = new List<AppRule>(this.AppRules),
            GameBoosterEnabled = this.GameBoosterEnabled,
            DisplayRefreshRate = this.DisplayRefreshRate,
            TemperatureUnit = this.TemperatureUnit,
            AppsToClose = new List<string>(this.AppsToClose),
            ServicesToOptimize = new List<string>(this.ServicesToOptimize ?? new()),
            ConservationModeEnabled = this.ConservationModeEnabled,
            RapidChargeEnabled = this.RapidChargeEnabled,
            OvernightBatteryChargingEnabled = this.OvernightBatteryChargingEnabled,
            FnLockEnabled = this.FnLockEnabled,
            TouchpadEnabled = this.TouchpadEnabled,
            AutoCheckUpdates = this.AutoCheckUpdates,
            AutoDownloadUpdates = this.AutoDownloadUpdates,
            NotifyOnNewRelease = this.NotifyOnNewRelease,
            SidebarMaintenanceExpanded = this.SidebarMaintenanceExpanded,
            SidebarUtilitiesExpanded = this.SidebarUtilitiesExpanded,
            SidebarCollapsed = this.SidebarCollapsed,
            // An explicit "music": null in the file must not leave callers with a null section
            Music = (this.Music ?? new MusicSettings()).Clone(),
            Uninstaller = (this.Uninstaller ?? new UninstallerSettings()).Clone(),
            ShareScreen = (this.ShareScreen ?? new ShareScreenSettings()).Clone()
        };
    }
}
