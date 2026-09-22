---
name: power-modes
description: Eco / Smart / Beast / Game power modes — how each maps to a Windows power plan GUID, the native-then-powercfg apply path, refresh-rate syncing, and where modes can be switched (dashboard, tray, title bar).
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

| Mode | Plan | GUID |
|---|---|---|
| Eco | Power saver | `a1841308-3541-4fab-bc81-f71556f20b4a` (or any plan named "Power saver") |
| Smart | Balanced | `381b4222-f694-41f0-9685-ff5bb260df2e` (or any plan named "Balanced") |
| Beast | Ultimate Performance | Found by name. If missing, duplicated from template `e9a42b02-d5df-448d-aa00-03f14749eb61` with `powercfg -duplicatescheme`. Falls back to High Performance `8c5e7fda-…`. |
| GameMode | Same plan as Beast | as above |

If no plan resolves, the service falls back to Balanced.

`GetActivePowerModeAsync` maps back from the plan: Power saver → Eco, High/Ultimate → Beast, anything else → Smart. **GameMode cannot be told apart from Beast** when reading the active plan.

## Apply path (`PowerPlanService.ApplyModeAsync`)
1. Resolve the target GUID as in the table.
2. Call `PowerInterop.SetActiveScheme`, then **verify** by reading the active scheme back.
3. If that fails, run `powercfg /setactive <guid>` and verify again.
4. On success, set the display refresh rate:
   - `AppSettings.DisplayRefreshRate > 0`: that fixed rate.
   - `0` (Auto): `SetSmartRefreshRate(useLowest: mode == Eco)`. Eco picks the lowest supported rate but never below 60 Hz when 60 is available; every other mode picks the highest rate.
   - `DisplayService.SetRefreshRate` calls both CCD (`SetDisplayConfig`) and GDI (`ChangeDisplaySettingsEx`), and reports success if either works.

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
[DynamicModeAutomationTests.cs](../AxePower.Tests/DynamicModeAutomationTests.cs), [AppSettingsTests.cs](../AxePower.Tests/AppSettingsTests.cs)
