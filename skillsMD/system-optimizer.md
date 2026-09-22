---
name: system-optimizer
description: System Optimizer — registry-based privacy, gaming and Windows 11 tweaks with live applied-state detection, apply/revert per tweak, "apply all recommended", "revert all", and a restore point before changes.
---

# System Optimizer

The Optimizer page is a curated list of reversible registry tweaks, grouped by category, each with an on/off toggle that shows its real state.

## Where it lives

| Piece | File |
|---|---|
| View / VM | [SystemOptimizerView.xaml](../AxePower.App/Views/SystemOptimizerView.xaml), [SystemOptimizerViewModel.cs](../AxePower.App/ViewModels/SystemOptimizerViewModel.cs) (per-row `TweakCardViewModel` + page VM) |
| Engine | [TweakService.cs](../AxePower.Core/Services/TweakService.cs) (interface [ITweakService.cs](../AxePower.Core/Services/ITweakService.cs)) |
| Model | [SystemTweakItem.cs](../AxePower.Core/Models/SystemTweakItem.cs): `TweakCategory`, `TweakImpact` |
| Restore point | [Uninstaller/SystemRestoreService.cs](../AxePower.Core/Services/Uninstaller/SystemRestoreService.cs) |

Sidebar: MAINTENANCE → **Optimizer** (`Navigate("Optimizer")`; `Utilities` also lands here).

## Tweak catalog
All tweaks are marked `IsRecommended = true`.

| Id | Category | Registry key → value |
|---|---|---|
| `telemetry-disable` | Privacy | `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection` → `AllowTelemetry` |
| `bing-search-disable` | Privacy | `HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer` → `DisableSearchBoxSuggestions` |
| `copilot-disable` | Privacy | `HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot` → `TurnOffWindowsCopilot` |
| `activity-feed-disable` | Privacy | `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` → `EnableActivityFeed` |
| `gamedvr-disable` | Gaming | `HKCU\System\GameConfigStore` → `GameDVR_Enabled` |
| `mouse-accel-disable` | Gaming | `HKCU\Control Panel\Mouse` → `MouseSpeed` (1:1 raw input) |
| `sticky-keys-disable` | Gaming | `HKCU\Control Panel\Accessibility\StickyKeys` → `Flags` |
| `classic-context-menu` | Windows11 | `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` → `(Default)` |

Category chip labels: "Privacy & Telemetry", "Gaming & Latency", "Windows 11 QoL".

## Operations (`ITweakService`)
- `RefreshAllTweakStatusesAsync()` reads each key and sets `IsApplied`. The UI always shows the real state, not a remembered one.
- `ApplyTweakAsync(id)` / `RevertTweakAsync(id)` operate on one tweak. The apply and revert values for each tweak are in `TweakService`.
- `ApplyAllRecommendedAsync()` applies every recommended tweak that isn't applied yet and returns the count.
- `RevertAllAsync()` reverts everything.
- The page VM adds `CreateRestorePointAsync` (recommended before bulk changes) and `SetCategory` for the chips.

## Adding a tweak
1. Add a `SystemTweakItem` to the list in `TweakService`, with a unique `Id`, `Title`, `Description`, `Category`, `Impact`, `RegistryKey` and `ValueName`.
2. Add the apply, revert and detect logic for that `Id` in the same service.
3. Add a case to [TweakServiceTests.cs](../AxePower.Tests/TweakServiceTests.cs).

## Requirements and effects
- Several tweaks also write HKLM, so they need admin: telemetry, activity feed, Copilot (both HKCU and HKLM policy) and GameDVR (`HKLM\...\GameDVR\AllowGameDVR=0` plus `GameDVR_FSEBehaviorMode=2`). The Bing tweak also sets `HKCU\...\Search\BingSearchEnabled=0`. A tweak counts as applied if *either* of its keys matches.
- Nothing broadcasts `WM_SETTINGCHANGE` or restarts Explorer. The mouse-acceleration change applies at the next sign-in, and the classic context menu after an Explorer restart.

## Related
The one-click third-party debloater is covered separately in [debloat.md](debloat.md).
