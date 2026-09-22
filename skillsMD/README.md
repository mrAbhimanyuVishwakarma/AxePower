# AxePower Feature Docs (skillsMD)

One file per AxePower feature. Each file explains what the feature does, where its code lives, how it works internally, what it changes on the system, what it needs (admin, internet, tools), and its known gaps. Each file starts with YAML front matter (`name`, `description`), so it can also be loaded as a skill or reference file for an AI coding agent.

Written from the code on branch `v1.8` (working tree, 2026-09-22). Paths are relative links into the repo.

## Index

| Sidebar section | Feature | File |
|---|---|---|
| CONTROL | Power modes (Eco / Smart / Beast / Game) | [power-modes.md](power-modes.md) |
| CONTROL | Auto Switch (AC/battery/low-battery + per-app rules) | [auto-switch.md](auto-switch.md) |
| CONTROL | CPU & GPU telemetry (dashboard grid, temperature stack) | [cpu-gpu-telemetry.md](cpu-gpu-telemetry.md) |
| CONTROL | Game Mode (library, posters, boost, rules) | [game-mode.md](game-mode.md) |
| CONTROL | Controller Navigation (gamepad → mouse) | [controller-navigation.md](controller-navigation.md) |
| CONTROL | Battery (status, 24h history, Lenovo charge modes) | [battery.md](battery.md) |
| CONNECTION | Phone Control (scrcpy, SpaceDesk, LAN Drop, Phone Link) | [phone-control.md](phone-control.md) |
| MAINTENANCE | System Optimizer (registry tweaks) | [system-optimizer.md](system-optimizer.md) |
| MAINTENANCE | System Doctor (SFC/DISM, cache cleaner, DNS) | [system-doctor.md](system-doctor.md) |
| MAINTENANCE | Terminal (shell + shared output bus) | [terminal.md](terminal.md) |
| UTILITIES | Update PC (drivers, BIOS, Windows Update Shield) | [update-pc.md](update-pc.md) |
| UTILITIES | Software Hub (winget app catalog) | [software-hub.md](software-hub.md) |
| UTILITIES | Uninstaller Pro | [uninstaller.md](uninstaller.md) |
| UTILITIES | MSI Afterburner companion | [msi-afterburner.md](msi-afterburner.md) |
| UTILITIES | Debloat (Win11Debloat) | [debloat.md](debloat.md) |
| FOOTER | Settings, elevation, startup, tray, themes | [settings-and-startup.md](settings-and-startup.md) |
| FOOTER | Auto-updater (AxePower self-update) | [auto-updater.md](auto-updater.md) |

## Shared architecture (read once)
- **Layers:**
  - `AxePower.Core` holds services, models and native interop, with no WPF.
  - `AxePower.App` holds the WPF views, CommunityToolkit.Mvvm ViewModels and app-only services.
  - `AxePower.Tests` holds xUnit tests.
- **DI:** every service is a singleton and every ViewModel is transient, registered in `App.ConfigureServices()` ([App.xaml.cs](../AxePower.App/App.xaml.cs)).
- **Navigation:** `MainViewModel.Navigate("<Key>[:arg1[:arg2]]")` swaps `CurrentView`. Views bind through `DataTemplate`s in [MainWindow.xaml](../AxePower.App/MainWindow.xaml).
- **User feedback:** `NotificationService.Show(msg)` shows the in-app toast. Long-running output goes to the Terminal ([terminal.md](terminal.md)).
- **Admin:** many features need elevation. See the elevation model in [settings-and-startup.md](settings-and-startup.md).
- **Data locations:**
  - `%LOCALAPPDATA%\AxePower\`: `settings.json`, `BatteryHistory\`, `tools\scrcpy\`, `Tools\`, `Scripts\`.
  - `%APPDATA%\AxePower\`: `AxePower.log`, `CoverArt\`, `UninstallerHistory\`.

## Known gaps found while writing these docs
These gaps are in the code today, and each one is also described in its feature file:
1. **Uninstaller:** registry backups are never written to disk, and there is no restore path ([uninstaller.md](uninstaller.md)).
2. **Battery:** health % is synthetic, because design capacity is set to full-charge capacity × 1.02 ([battery.md](battery.md)).
3. **Update PC:** the "Reset & Repair Cache" button binds `ResetUpdateCacheCommand`, which does not exist ([update-pc.md](update-pc.md)).
4. **Settings:** `StartMinimised` / `--background` are never honoured at launch ([settings-and-startup.md](settings-and-startup.md)).
5. **LAN Drop:** there is no authentication, so anyone on the LAN can upload, download, or lock the PC ([phone-control.md](phone-control.md)).
6. **Game Mode:** services set to manual start are not restored when Game Mode is turned off ([game-mode.md](game-mode.md)).
7. **Auto-updater:** it re-downloads the installer every hour until it is installed ([auto-updater.md](auto-updater.md)).
8. **README claims the code doesn't back up:**
   - SteamGridDB posters (art comes from Steam only).
   - Ubisoft and EA library scanning.
   - Legion Toolkit, Vantage, Armoury Crate and MSI Center launchers.
   - "Encrypted" settings (they are plain JSON).
   - A reachable `AutoSwitchView` (`Navigate("AutoSwitch")` shows the Dashboard).

## Keeping these current
When you change a feature, update its file in the same PR: the file table, the behaviour, and the "Known gaps" section. Remove a gap from this README once it is fixed.
