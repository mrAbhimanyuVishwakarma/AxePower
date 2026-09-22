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
1. `PowerPlanService.ApplyModeAsync(Beast)`.
2. `KillSelectedNow()` kills the background apps selected in the list.
3. `OptimizeSelectedServices()` stops the selected services and sets them to **manual start** (`sc config <svc> start= demand`).

**Turning it off:** applies `Smart`.

> **Not reverted:** services changed in step 3 stay on manual start after Game Mode is turned off. Killed apps are not restarted either (only the rule-driven Game Booster in `AppMonitorService` restarts apps).

`SystemServiceOptimizer.OptimizableServices` includes Print Spooler, SysMain, Windows Search, Edge Update, WaaSMedicSvc, the Xbox Live services, VMware services and MongoDB. Only services that are present are listed.

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
