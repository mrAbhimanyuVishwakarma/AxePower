---
name: power-modes
description: Eco / Smart / Beast / GameMode power modes — the four named Windows plans AxePower creates and tunes, the native-then-powercfg apply path, refresh-rate syncing, and where modes can be switched (dashboard, tray, title bar).
---

# Power Modes (Eco · Smart · Beast · Game)

Power modes are AxePower's core feature. Each mode maps to a Windows power plan and optionally to a display refresh rate.

## Where it lives

| Piece | File |
|---|---|
| Enum | [PowerMode.cs](../AxePower.Core/Models/PowerMode.cs): `Eco`, `Smart`, `Beast`, `GameMode` |
| Apply / read active plan | [PowerPlanService.cs](../AxePower.Core/Services/PowerPlanService.cs) |
| Native power API | [PowerInterop.cs](../AxePower.Core/Native/PowerInterop.cs) (`PowerGetActiveScheme`, `PowerSetActiveScheme`, enumerate) |
| Refresh rate | [DisplayService.cs](../AxePower.Core/Services/DisplayService.cs) |
| Dashboard buttons | [DashboardViewModel.cs](../AxePower.App/ViewModels/DashboardViewModel.cs): `SelectMode`, `ApplyModeAsync`, `ApplyModeDirectAsync` |
| Tray / global command | [MainViewModel.cs](../AxePower.App/ViewModels/MainViewModel.cs): `ApplyPowerModeAsync` (tray menu in [MainWindow.xaml](../AxePower.App/MainWindow.xaml)) |

## Mode → power plan

Each mode has **its own Windows power plan**, named exactly after the mode and visible in
Control Panel → Power Options. They are defined in
[PowerPlanCatalog.cs](../AxePower.Core/Services/PowerPlanCatalog.cs) with fixed GUIDs, so the
active plan maps back to exactly one mode and a user plan with the same name is never taken over.

| Mode | Plan name | GUID | Copied from (first that works) | Settings AxePower writes (AC and battery) |
|---|---|---|---|---|
| Eco | `Eco` | `c6e48611-…ef93` | Power saver, Balanced | CPU 5–70 %, turbo off, EPP 80, PCIe ASPM max savings, Energy Saver always on while on battery |
| Smart | `Smart` | `e52a4182-…0e14` | Balanced | none (Balanced defaults) |
| Beast | `Beast` | `0510a366-…7cca` | Ultimate template `e9a42b02-…`, High performance, Balanced | CPU 100–100 %, aggressive turbo, EPP 0, ASPM off, Energy Saver never |
| GameMode | `GameMode` | `073f1c31-…cccf` | as Beast | as Beast |

`EnsurePlan` creates a plan **only when it is missing**, with `PowerDuplicateScheme` (passing
AxePower's GUID as the destination), and writes its name, description and settings at that moment
only. An existing plan is switched to as it is, so edits made in Control Panel stay. A setting a CPU
does not support is logged and skipped. If a plan cannot be created at all, the stock plan is used
instead (Power saver for Eco, High performance for Beast/GameMode, otherwise Balanced).

`GetActivePowerModeAsync` maps AxePower's GUIDs to their modes. For any other plan it falls back
to the old guesses: Power saver → Eco, High/Ultimate → Beast, anything else → Smart.

### One-time cleanup (`InitializePlansOnce`)
After the first successful apply on a PC (normally the startup switch to Smart):
1. All four plans are created.
2. **Every other plan is deleted** except Windows' stock ones: Balanced, Power saver, High
   performance and the Ultimate Performance template GUID. Copies of Ultimate Performance, OEM
   plans (for example Lenovo's) and user plans all go. The active plan is skipped because Windows
   will not delete it.
3. `powerPlansInitialized` is set in settings.json, so this never runs again on that PC.

If any of the four plans cannot be created, nothing is deleted and the flag stays off, so it is
retried on the next apply. AxePower's own plans are not deleted on uninstall.

### No duplicates
- AxePower's plans have fixed GUIDs, and Windows refuses a second plan with the same GUID, so a
  plan can never be copied twice. A plan deleted later is created again the next time its mode is
  applied.
- **On every apply**, `RemoveSameNamedPlans` deletes any other plan named Eco, Smart, Beast or
  GameMode (case and surrounding spaces ignored), such as a Control Panel copy. Stock plans and
  the active plan are left alone.
- Creating, cleaning up and deleting plans run under one lock, because startup, Auto Switch and
  app rules can apply a mode at the same moment.

## Apply path (`PowerPlanService.ApplyModeAsync`)
1. `EnsurePlan` for the mode, as above.
2. Call `PowerInterop.SetActiveScheme`, then **verify** by reading the active scheme back.
3. If that fails, run `powercfg /setactive <guid>` and verify again.
4. On success, set the refresh rate, then raise `ModeApplied` once (the event AxePower Music listens to).

## Refresh rate
- `AppSettings.DisplayRefreshRate > 0` (a fixed rate picked in Settings) overrides everything.
- Otherwise: **Eco on battery → 60 Hz** (the panel's lowest if it has no 60 Hz). Every other
  case, including every mode on AC and any desktop, gets the panel's highest rate.
- `AutoSwitchService` calls `SyncRefreshRateAsync` on every power-status event, so plugging in or
  unplugging while in Eco changes the rate without a mode change.
- `SetSmartRefreshRate` does nothing when the display is already at the target, since each change
  blanks the screen briefly.
- `DisplayService.SetRefreshRate` calls both CCD (`SetDisplayConfig`) and GDI (`ChangeDisplaySettingsEx`), and reports success if either works.

## Startup
AxePower always applies **Smart** at startup (`AutoSwitchService.Start`). The old
`startWithLastSelectedMode` setting is no longer read.

The Dashboard's `ApplyModeAsync` also saves `LastSelectedPowerMode` and shows a toast.

## Entry points
- Dashboard mode cards: select a card, then **Apply**.
- Tray right-click menu: Eco / Smart / Beast / Game Mode (`ApplyPowerModeCommand`). Choosing Game Mode also navigates to the Game Mode page.
- Automatic triggers: [auto-switch.md](auto-switch.md) (AC/DC/low battery, per-app rules) and [game-mode.md](game-mode.md) (manual Game Mode toggle).

## Background app suspension (Dashboard)
`ToggleBackgroundApps` calls `AppKillerService.SuspendBackgroundApps()` / `ResumeBackgroundApps()`. It suspends, with `NtSuspendProcess`, every process that has a visible main window except a built-in exclusion list (explorer, taskmgr, devenv, Code, powershell, cmd, shell hosts, AxePower…).

> A suspended app looks frozen until it is resumed. Resume before exiting AxePower, because the suspended set is tracked only in memory.

## Related automation (not wired up)
[DynamicModeAutomationService.cs](../AxePower.Core/Services/DynamicModeAutomationService.cs) has an `OnModeChangedAsync` hook:
- Beast/Game: pause heavy services and purge standby RAM.
- Eco: restore the paused services.

Nothing calls `OnModeChangedAsync` at present. The throttle and RAM actions are reachable only as manual buttons on the Game Mode page.

## Tests
[PowerPlanCatalogTests.cs](../AxePower.Tests/PowerPlanCatalogTests.cs), [PowerPlanModeAppliedTests.cs](../AxePower.Tests/Music/PowerPlanModeAppliedTests.cs), [DynamicModeAutomationTests.cs](../AxePower.Tests/DynamicModeAutomationTests.cs), [AppSettingsTests.cs](../AxePower.Tests/AppSettingsTests.cs)
