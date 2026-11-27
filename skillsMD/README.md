# AxePower Feature Docs (skillsMD)

One file per AxePower feature. Each file explains what the feature does, where its code lives, how it works internally, what it changes on the system, what it needs (admin, internet, tools), and its known gaps. Each file starts with YAML front matter (`name`, `description`), so it can also be loaded as a skill or reference file for an AI coding agent.

Paths are relative to the repository. For the current branch and build, see [AGENTS.md](../AGENTS.md) and [WORK_LOG.md](../WORK_LOG.md). Dated plans describe their original scope. Check the feature guide and current source before following an implementation plan.

## Index

| Sidebar section | Feature | File |
|---|---|---|
| CONTROL | Power modes (Eco / Smart / Beast / Game) | [power-modes.md](power-modes.md) |
| CONTROL | Auto Switch (AC/battery/low-battery + per-app rules) | [auto-switch.md](auto-switch.md) |
| CONTROL | CPU & GPU telemetry (dashboard grid, temperature stack) | [cpu-gpu-telemetry.md](cpu-gpu-telemetry.md) |
| CONTROL | Game Mode (library, posters, boost, rules) | [game-mode.md](game-mode.md) |
| CONTROL | Controller Navigation (gamepad → mouse) | [controller-navigation.md](controller-navigation.md) |
| CONTROL | Battery (status, 24h history, Lenovo charge modes) | [battery.md](battery.md) |
| CONTROL | Music (player, title-bar player, media keys) | [music-player.md](music-player.md) |
| CONTROL | Music — build spec, phase by phase | [MUSIC_PLAYER_PLAN.md](MUSIC_PLAYER_PLAN.md) |
| CONTROL | Music — open issues and what is still unproven | [MUSIC_FIX_PLAN.md](MUSIC_FIX_PLAN.md) |
| CONNECTION | Phone Control (scrcpy, SpaceDesk, LAN Drop, Phone Link) | [phone-control.md](phone-control.md) |
| MAINTENANCE | System Optimizer (registry tweaks) | [system-optimizer.md](system-optimizer.md) |
| MAINTENANCE | System Doctor (SFC/DISM, cache cleaner, DNS) | [system-doctor.md](system-doctor.md) |
| UTILITIES | Terminal (shell + shared output bus) | [terminal.md](terminal.md) |
| MAINTENANCE | Windows Update (Windows, app, and device updates, plus Update Shield) | [update-pc.md](update-pc.md) |
| UTILITIES | Software Hub (winget app catalog) | [software-hub.md](software-hub.md) |
| MAINTENANCE | Uninstaller and BCUninstaller shortcut | [uninstaller.md](uninstaller.md) |
| UTILITIES | Tools → Timer & Routines: if-then routines plan | [ROUTINES_PLAN.md](ROUTINES_PLAN.md) |
| UTILITIES | MSI Afterburner companion | [msi-afterburner.md](msi-afterburner.md) |
| MAINTENANCE | Debloat (Win11Debloat) | [debloat.md](debloat.md) |
| FOOTER | Settings, elevation, startup, tray, themes | [settings-and-startup.md](settings-and-startup.md) |
| FOOTER | Account (optional sign-in, Profile page, Supabase) | [account.md](account.md) |
| FOOTER | Auto-updater (AxePower self-update) | [auto-updater.md](auto-updater.md) |

## Shared architecture (read once)
- **Layers:**
  - `AxePower.Core` holds services, models and native interop, with no WPF.
  - `AxePower.App` holds the WPF views, CommunityToolkit.Mvvm ViewModels and app-only services.
  - `AxePower.Tests` holds xUnit tests.
- **DI:** see `App.ConfigureServices()` for the actual service and ViewModel lifetimes ([App.xaml.cs](../AxePower.App/App.xaml.cs)).
- **Navigation:** `MainViewModel.Navigate("<Key>[:arg1[:arg2]]")` swaps `CurrentView`. The shell uses [ViewTemplates.xaml](../AxePower.App/Resources/ViewTemplates.xaml) to render each page.
- **User feedback:** `NotificationService.Show(msg)` shows the in-app toast. Long-running output goes to the Terminal ([terminal.md](terminal.md)).
- **Admin:** many features need elevation. See the elevation model in [settings-and-startup.md](settings-and-startup.md).
- **Data locations:**
  - `%LOCALAPPDATA%\AxePower\`: `settings.json`, `BatteryHistory\`, `tools\scrcpy\`, `Tools\`, `Scripts\`.
  - `%APPDATA%\AxePower\`: `AxePower.log`, `CoverArt\`, `UninstallerHistory\`.

## Keeping these current
When you change a feature, update its guide with the source paths, behavior, and current limitations.
