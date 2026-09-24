---
name: settings-and-startup
description: App shell & settings — settings.json persistence, Run-as-administrator elevation model (asInvoker + runas relaunch + HighestAvailable logon task), start with Windows, single-instance pipe, close-to-tray and tray menu, themes (System/Dark/Light/Glass), logs.
---

# Settings, Elevation & App Startup

This covers the Settings page and the shell behaviour every other feature depends on: where settings live, how AxePower gets admin rights without a UAC prompt at every boot, single-instance handling, the tray and themes.

## Where it lives

| Piece | File |
|---|---|
| Settings model | [AppSettings.cs](../AxePower.Core/Models/AppSettings.cs) |
| Persistence | [SettingsService.cs](../AxePower.Core/Services/SettingsService.cs) |
| Settings page | [SettingsView.xaml](../AxePower.App/Views/SettingsView.xaml), [SettingsViewModel.cs](../AxePower.App/ViewModels/SettingsViewModel.cs) |
| Elevation | [ElevationService.cs](../AxePower.Core/Services/ElevationService.cs), [app.manifest](../AxePower.App/app.manifest) |
| Start with Windows | [StartupService.cs](../AxePower.Core/Services/StartupService.cs) |
| Boot sequence, DI, single instance | [App.xaml.cs](../AxePower.App/App.xaml.cs) |
| Window, tray, close | [MainWindow.xaml](../AxePower.App/MainWindow.xaml), [MainWindow.xaml.cs](../AxePower.App/MainWindow.xaml.cs) |
| Themes | [ThemeService.cs](../AxePower.App/Services/ThemeService.cs), [AppTheme.cs](../AxePower.Core/Models/AppTheme.cs) |
| Screen / sleep timeouts | [TimeoutService.cs](../AxePower.Core/Services/TimeoutService.cs) |
| Log | [LogService.cs](../AxePower.Core/Services/LogService.cs), [LogsViewModel.cs](../AxePower.App/ViewModels/LogsViewModel.cs) |

## Settings persistence
- **File:** `%LOCALAPPDATA%\AxePower\settings.json`, plain indented JSON with enums stored as strings. The README calls it "encrypted"; it is not.
- **Save:** write `settings.temp.json`, then `File.Replace(temp, settings, settings.backup.json)`, which is atomic and keeps the previous file as a backup.
- **Load:** reads `settings.json` and falls back to `settings.backup.json`, then to defaults.
- `SettingsService.OnSettingsChanged` fires after every save. `AutoSwitchService`, `AppMonitorService` and `StartupService` listen to it.
- Adding a setting: add a `[JsonPropertyName]` property with a default in `AppSettings` **and** copy it in `AppSettings.Clone()`. A missing key in an old file falls back to the default.

## Elevation model
The goal is to run elevated by default, keep that switchable, and avoid a UAC prompt at boot.

1. The manifest is `asInvoker`. Using `requireAdministrator` would make the toggle impossible to switch off and would prompt on every launch.
2. At startup (`App.TryElevateAndExit`), if `RunAsAdministrator` is on, the process isn't elevated, and `--elevation-attempted` isn't in the arguments, AxePower relaunches itself with the `runas` verb (plus `--force`) and exits.
   - If the user refuses UAC, AxePower keeps running unelevated.
   - The sentinel argument stops a relaunch loop when Group Policy silently denies elevation.
3. The logon task is registered at `HighestAvailable` when `RunAsAdministrator` is on, so the boot launch is elevated with **no prompt**.
4. Runtime escape hatches:
   - The title-bar **Relaunch with Admin** button (`MainViewModel.RelaunchAsAdminCommand`).
   - Settings → **Restart elevated** (`SettingsViewModel.RestartElevated`), shown only when elevation is enabled but not active.

**What needs admin:** CPU temperature sensors, HKLM tweaks, Windows Update policies, restore points, System Doctor repairs, service changes, the WMI process watchers for app rules, and an elevated Terminal.

## Start with Windows
`StartupService` registers the scheduled task **"AxePower Startup"** from XML through `schtasks /Create /XML`:
- a logon trigger;
- a `RunLevel` of HighestAvailable or LeastPrivilege, depending on the elevation setting;
- `ExecutionTimeLimit=PT0S`, so there is no 72-hour kill;
- the argument `--background`.

Turning off `StartWithWindows` deletes the task. The task is re-registered on every settings save, so the run level always follows the elevation toggle.

## Single instance
- Uses the named pipe `AxePower.Desktop.Singleton.Pipe`. A second launch signals the first one, which restores and foregrounds its window, then exits.
- If the existing process has **no** window handle (an orphaned headless instance), the new launch kills it and carries on.
- `--force` skips the check. The elevated relaunch uses it so it doesn't mistake the closing parent for a live instance.
- `--self-test` runs `RunSelfTest()`, which prints DisplayService, Uninstaller discovery and MainViewModel/MainWindow resolution to the console, then exits.

## Window, tray, close
- `CloseBehaviour`:
  - `MinimiseToTray` (default): hide the window and keep running in the tray.
  - `AskEveryTime`: a Yes/No dialog.
  - `ExitAxePower`: exit.
- A Windows logoff or shutdown always closes the app (`SessionEnding` → `AllowClose()`).
- **Tray menu** (Hardcodet TaskbarIcon): Eco / Smart / Beast / Game Mode, Dashboard, Battery, Phone Control, Second Monitor, Maintenance, Utilities, Update PC, Settings, Exit.
- Telemetry sampling pauses while the window is hidden (see [cpu-gpu-telemetry.md](cpu-gpu-telemetry.md)).

## Themes
- `AppTheme`: `System`, `Dark`, `Light`, `Glass`. The legacy value `"Transparent"` is read as `Glass`.
- **Glass** uses the Windows 11 DWM backdrop (`DWMWA_SYSTEMBACKDROP_TYPE` = Acrylic) on build ≥ 22000, and the `SetWindowCompositionAttribute` acrylic blur on Windows 10.
- `ThemeService.ThemeChanged` notifies listeners, and the tray icon swaps between light and dark variants to match the taskbar.

## Other Settings page items
- Default AC and battery modes, and fan mode on battery.
- ("Start with last selected mode" is gone: AxePower always starts in Smart.)
- Notification toggles.
- Display refresh rate: "Auto (Smart)" or a fixed Hz value (see [power-modes.md](power-modes.md)).
- Temperature unit (°C / °F).
- Monitor and standby timeouts for AC and battery, read and written with `powercfg` through `TimeoutService`.
- Touchpad toggle.
- Update preferences (see [auto-updater.md](auto-updater.md)).

## Logs
`%APPDATA%\AxePower\AxePower.log`. Unhandled UI and AppDomain exceptions are logged and swallowed, so the app doesn't crash (`DispatcherUnhandledException` sets `Handled = true`).

## Known gaps
- **`StartMinimised` is not honoured.** Neither the setting nor the task's `--background` argument is read anywhere in `App.xaml.cs`, and the main window is always shown at launch.

## Tests
[AppSettingsTests.cs](../AxePower.Tests/AppSettingsTests.cs)
