---
name: game-mode
description: Game Mode page — Steam/Epic/GOG/Xbox game library scanner, Steam-sourced poster art, manual Game Mode toggle (Beast plan + app kill + service stop), per-game power rules, background service throttling, standby-RAM purge, GPU control-panel launcher.
---

# Game Mode

The Game Mode page is the gaming hub: a scanned game library with poster art, a one-click manual Game Mode, per-game rules, and background-load controls.

## Where it lives

| Piece | File |
|---|---|
| View / VM | [GameModeView.xaml](../AxePower.App/Views/GameModeView.xaml), [GameModeViewModel.cs](../AxePower.App/ViewModels/GameModeViewModel.cs) |
| Game card | [GameCardViewModel.cs](../AxePower.App/ViewModels/GameCardViewModel.cs), [AspectRatioBox.cs](../AxePower.App/Controls/AspectRatioBox.cs) |
| Scanner window | [GameScannerWindow.xaml](../AxePower.App/Windows/GameScannerWindow.xaml) |
| Library scanning | [GameScannerService.cs](../AxePower.Core/Services/GameScannerService.cs), model [InstalledGameInfo.cs](../AxePower.Core/Models/InstalledGameInfo.cs) |
| Poster art | [GamePosterService.cs](../AxePower.App/Services/GamePosterService.cs) |
| Kill / restart apps | [AppKillerService.cs](../AxePower.Core/Services/AppKillerService.cs) |
| Stop services | [SystemServiceOptimizer.cs](../AxePower.Core/Services/SystemServiceOptimizer.cs) |
| Pause / restore services | [ServiceThrottlerService.cs](../AxePower.Core/Services/ServiceThrottlerService.cs) |
| Standby RAM purge | [MemoryOptimizerService.cs](../AxePower.Core/Services/MemoryOptimizerService.cs) |
| GPU control panel launcher | [GpuControlAppService.cs](../AxePower.Core/Services/GpuControlAppService.cs) |

Related features documented separately: [controller-navigation.md](controller-navigation.md), [msi-afterburner.md](msi-afterburner.md), [auto-switch.md](auto-switch.md) (per-app rules engine).

## Library scanner
`GameScannerService` has two scan paths:
- **`GetInstalledGamesAsync()`** reads launcher metadata:
  - **Steam:** finds the install path from the registry, reads `steamapps\libraryfolders.vdf` for secondary libraries, then parses the `appmanifest_*.acf` files.
  - **Epic:** `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`.
- **`ScanForGamesAsync()`** collects executables from game folders:
  - Steam libraries.
  - Program Files `Epic Games` and `GOG Galaxy\Games`.
  - On every drive root: `SteamLibrary\steamapps\common`, `Epic Games`, `GOG Games`, `XboxGames` (Game Pass), and `Games`.
- `IsSoftwareOrUtility(name)` filters out redistributables, tools and launchers so they don't appear as games.

> The README lists Ubisoft Connect and EA App as scanned. Neither is: their games appear only if they were installed under one of the folders above.

## Poster art
`GamePosterService.FetchCoverArtAsync(exeName, steamAppId)` tries, in order:
1. Steam's local `appcache\librarycache\{appId}_header.jpg`, which works offline and instantly.
2. The disk cache at `%APPDATA%\AxePower\CoverArt\`.
3. Steam CDN header or capsule images (several mirrors).
4. Steam store search (`store.steampowered.com/api/storesearch`) by name, then the matching CDN image.

The HTTP timeout is 5 s. Downloaded images are cached.

> The README mentions SteamGridDB. The code does not call SteamGridDB; all art comes from Steam.

## Manual Game Mode (`ToggleManualGameMode`)
**Turning it on** (`ApplyGameModeStateAsync(true)`):
1. `PowerPlanService.ApplyModeAsync(GameMode)`, the GameMode plan. It used to apply Beast.
2. `KillSelectedNow()` kills the background apps selected in the list.
3. `OptimizeSelectedServices()` stops the selected services and sets them to **manual start** (`sc config <svc> start= demand`).

**Turning it off:** applies `Smart`.

**Leaving Game Mode by picking another mode** ([GameModeGuard.cs](../AxePower.Core/Services/GameModeGuard.cs)):
- Any other mode applied anywhere turns the toggle off. The picked mode stays; Smart is not applied over it.
- **While a known game is running**, which means a library exe or an app rule:
  - A Dashboard or tray pick first asks "A game is running. Leave Game Mode anyway?". After 10 s it chooses **Stay in Game Mode** by itself.
  - Auto Switch does not switch at all; it logs "Keeping Game Mode".

> **Not reverted:** services changed in step 3 stay on manual start after Game Mode is turned off. Killed apps are not restarted either (only the rule-driven Game Booster in `AppMonitorService` restarts apps).

`SystemServiceOptimizer.OptimizableServices` includes Print Spooler, SysMain, Windows Search, Edge Update, WaaSMedicSvc, the Xbox Live services, VMware services and MongoDB. Only services that are present are listed.

## Dedicated GPU and display mode (Settings → Game Mode card)
**Use dedicated GPU in Game Mode** ([GameModeGpuService.cs](../AxePower.Core/Services/GameModeGpuService.cs)):
- Off by default. Turning it on shows a 10 s caution dialog ([CountdownConfirmWindow.xaml](../AxePower.App/Windows/CountdownConfirmWindow.xaml)). If there is no click within 10 s, it cancels and the toggle flips back. Turning it off needs no confirmation.
- It follows `PowerPlanService.ModeApplied`:
  - **Entering GameMode** writes `GpuPreference=2` (High performance) under `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`. The entries cover library games (Steam and Epic exe paths) and running processes that match an app rule or a library game.
  - **Leaving GameMode** restores each value exactly, including deleting values that did not exist before. A value the user changed in the meantime is left alone.
- What was changed is kept in `gpuPreferenceBackups` in settings.json. If AxePower crashes during Game Mode, the startup switch to Smart undoes it.
- Windows reads the preference when a process starts, so a game already running when Game Mode begins keeps its GPU until it is restarted.

**Display mode** ([GpuDisplayModeService.cs](../AxePower.Core/Services/GpuDisplayModeService.cs)) offers Optimus (Hybrid) or Dedicated GPU only, the same choice as "Manage Display Mode" in NVIDIA Control Panel.
- Lenovo only, through the GameZone WMI methods `IsSupportGSync`, `GetGSyncStatus` and `SetGSyncStatus` (1 = dGPU only). It needs admin, and the row appears only when the firmware reports support.
- A change shows the 10 s caution, then offers **Restart now** (`shutdown /r /t 5`) or **Later**. A "Restart pending" note stays until the restart.
- On other machines, the row shows an **Open GPU control panel** button instead (NVIDIA App, AMD, or Windows Graphics settings), because NVIDIA has no public API for apps to switch the mode.

## Service throttling and RAM (buttons on this page)
- **Toggle service throttling:** `ServiceThrottlerService` pauses or restores `SysMain`, `WSearch`, `DiagTrack`, `WerSvc` and `DoSvc`, and remembers which ones it stopped so it can restore exactly those.
- **Optimize RAM now:** `MemoryOptimizerService.PurgeStandbyMemoryAsync()` runs `EmptyWorkingSet` on every process it can open and reports the change in available RAM. Despite the method name, it does **not** clear the standby list. Trimmed pages mostly move to standby and fault back in, so the gain is short-lived.

## Per-game rules
`AddRule` opens a picker for an `.exe` or `.lnk` (a shortcut is resolved to its target through `WScript.Shell`) and adds an `AppRule { ExecutableName, TargetMode = Beast }` to `AppSettings.AppRules`. These rules take effect only when `AppRulesEnabled` is on. The engine is described in [auto-switch.md](auto-switch.md).

## GPU control companion
`GpuControlAppService.OpenGraphicsSettings()` opens the vendor's own panel:
- NVIDIA App, or GeForce Experience as a fallback.
- AMD Radeon Software (`-page games`).
- Everyone else, Intel included, gets Windows Graphics settings (`ms-settings:display-advancedgraphics`).

The vendor is detected from the GPU name, and `GetBrandColorHex()` colors the card to match.

> The README lists Legion Toolkit, Vantage, Armoury Crate and MSI Center as companions. `GpuControlAppService` does not launch any of those; it handles only GPU vendor apps.

## Requirements
Killing elevated processes, stopping services and running `sc config` all need admin.

## Tests
[DynamicModeAutomationTests.cs](../AxePower.Tests/DynamicModeAutomationTests.cs)
