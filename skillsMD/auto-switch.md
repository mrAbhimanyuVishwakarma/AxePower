---
name: auto-switch
description: Automatic power-mode switching — AC vs battery rules, low-battery hysteresis, switch delay, startup behaviour, and per-app (game) rules that override it via WMI process watchers and the Game Booster app killer.
---

# Auto Switch

Auto Switch picks the power mode on its own when the power source or battery level changes, or when a configured game or app starts or stops.

## Where it lives

| Piece | File |
|---|---|
| AC/DC/low-battery engine | [AutoSwitchService.cs](../AxePower.Core/Services/AutoSwitchService.cs) |
| Per-app rules | [AppMonitorService.cs](../AxePower.Core/Services/AppMonitorService.cs), model [AppRule.cs](../AxePower.Core/Models/AppRule.cs) |
| Game Booster kill/restart | [AppKillerService.cs](../AxePower.Core/Services/AppKillerService.cs) |
| Settings | [AppSettings.cs](../AxePower.Core/Models/AppSettings.cs) |
| UI for AC/DC modes | Settings page ([SettingsViewModel.cs](../AxePower.App/ViewModels/SettingsViewModel.cs)) |
| UI for app rules | Game Mode page ([GameModeViewModel.cs](../AxePower.App/ViewModels/GameModeViewModel.cs): `AddRule`, `RemoveRule`, `ClearAllRules`) |
| Started / stopped | [App.xaml.cs](../AxePower.App/App.xaml.cs): `Application_Startup` / `Application_Exit` |

> `AutoSwitchView.xaml` and `AutoSwitchViewModel` still exist, but the view is not registered in `MainWindow`, and `Navigate("AutoSwitch")` shows the Dashboard. Treat them as dead code until they are wired back in.

## Settings (defaults)

| Key | Default | Meaning |
|---|---|---|
| `autoSwitchEnabled` | `true` | Master switch |
| `pluggedInMode` | `Smart` | Mode on AC power |
| `unpluggedMode` | `Eco` | Mode on battery |
| `lowBatteryRuleEnabled` | `true` | Enable the low-battery override |
| `lowBatteryThresholdPercent` | `30` | Enter low-battery state at or below this |
| `lowBatteryMode` | `Eco` | Mode while in low-battery state |
| `switchDelaySeconds` | `5` | Debounce before applying |
| `startWithLastSelectedMode` | `false` | On startup, apply `lastSelectedPowerMode` instead of evaluating the rules |
| `showNotifications` | `true` | Toast when a switch happens |
| `appRulesEnabled` | `false` | Turn on the per-app WMI watchers |
| `appRules` | `[]` | `{ executableName, targetMode }` list |
| `gameBoosterEnabled` | `false` | Kill the `appsToClose` list while a rule app runs |

## AutoSwitchService
**Triggers:**
- `SystemEvents.PowerModeChanged` (plug/unplug).
- `SettingsService.OnSettingsChanged`.
- A 60 s fallback loop.

**Evaluation** (`EvaluatePowerStateAsync`):
1. Stop if paused (an app rule is active) or `AutoSwitchEnabled` is false.
2. Low-battery **hysteresis**: enter at ≤ threshold, leave only at ≥ threshold + 5 %.
3. Act only when the power source or the low-battery state has *changed* since the last evaluation.
4. `ExecuteTransitionAsync` cancels any pending transition, waits `SwitchDelaySeconds` (cancellable), then:
   - target = AC ? `PluggedInMode` : (low ? `LowBatteryMode` : `UnpluggedMode`)
   - applies it through `PowerPlanService.ApplyModeAsync` only if it is not already active.

Transitions are serialised with a `SemaphoreSlim`.

**Pause / Resume:** `Pause()` stops evaluation. `Resume()` re-enables it and evaluates immediately.

## Per-app rules (AppMonitorService)
- When `AppRulesEnabled` is on, the service subscribes to WMI `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace`. These watchers **need admin**.
- **On start of a rule executable** (case-insensitive match, one tracked app at a time):
  1. `AutoSwitchService.Pause()`.
  2. If Game Booster is on, `KillBackgroundApps(AppsToClose)`.
  3. Apply `rule.TargetMode` and show the toast "Game Mode: Switched to …".
- **On stop of the tracked app:** `AutoSwitchService.Resume()` re-evaluates the AC/DC rules, then `RestartBackgroundApps()` restarts whatever Game Booster killed.
- Rules are added from the Game Mode page (exe or `.lnk` picker; a shortcut is resolved to its target) with the default target `Beast`.

## Gotchas
- Mode detection reads the active plan, and GameMode shares Beast's plan (see [power-modes.md](power-modes.md)). A GameMode target therefore compares as "different" and is re-applied on each transition. This is harmless, but log noise.
- If AxePower is not elevated, the WMI watchers fail to start and the service logs an error. The app rules then silently do nothing.
- Unplugging and replugging within `SwitchDelaySeconds` cancels the first transition, which is intended.

## Tests
[AppSettingsTests.cs](../AxePower.Tests/AppSettingsTests.cs)
