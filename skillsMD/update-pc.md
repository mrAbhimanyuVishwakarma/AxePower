---
name: update-pc
description: Update PC hub — GPU driver updates, BIOS/firmware checks, the elevated 3-step Windows/driver/winget updater, "upgrade all" apps, Windows Update Shield policies, and one-click restore points.
---

# Update PC

The Update PC page covers driver, firmware and Windows updates. It does not have its own view: the sidebar's **Update PC** button calls `Navigate("UpdatePc")`, which opens the **Software Hub** view on its "Updaters" tab. The app catalog on the same view is documented in [software-hub.md](software-hub.md).

## Where it lives

| Piece | File |
|---|---|
| View (all three tabs) | [SoftwareHubView.xaml](../AxePower.App/Views/SoftwareHubView.xaml) |
| ViewModel | [SoftwareHubViewModel.cs](../AxePower.App/ViewModels/SoftwareHubViewModel.cs) |
| GPU detection and driver routing | [GraphicsDriverService.cs](../AxePower.Core/Services/GraphicsDriverService.cs) |
| BIOS / firmware info and scan | [FirmwareUpdateService.cs](../AxePower.Core/Services/FirmwareUpdateService.cs), model [FirmwareInfo.cs](../AxePower.Core/Models/FirmwareInfo.cs) |
| Elevated 3-step updater script | [DriverUpdateService.cs](../AxePower.Core/Services/DriverUpdateService.cs) |
| Windows Update policies | [WindowsUpdateShieldService.cs](../AxePower.Core/Services/WindowsUpdateShieldService.cs) |
| Restore point | [RestorePointService.cs](../AxePower.Core/Services/RestorePointService.cs) |
| "Upgrade all" | `UpgradeAllPackagesAsync` in [WinGetHubService.cs](../AxePower.Core/Services/WinGetHubService.cs) |

## Navigation and deep links

`MainViewModel.Navigate` splits the target on `:`, so a caller can pick the tab and category in one call:

- `UpdatePc` / `SoftwareHub` → Software Hub, current tab.
- `UpdatePc:Apps:Gaming` → Apps tab with the Gaming category selected.
- Tab keys (`SoftwareHubViewModel.ActiveTab`): `Updaters`, `Apps`, `Updates`. A code comment also lists `Console`, but the view has no tab button for it; output goes to the Terminal page instead.

The sidebar's Update PC button navigates. `MainViewModel.UpdateDrivers()` launches the elevated updater directly, and only the Help modal's "Run Update PC" button uses it.

## Tab: "🚀 System & Driver Updaters" (`Updaters`)

### Check all updates
`CheckAllUpdatesAsync` runs the firmware check, then refreshes the app-install statuses, and stamps "last updated" plus a nominal "next scheduled" date 7 days later. Nothing is actually scheduled; the second date is only a label.

### Graphics drivers
- `GraphicsDriverService.DetectAdapters()` enumerates real adapters and skips virtual ones (spacedesk, RDP, Basic Display). It identifies the vendor from the PnP vendor ID (`VEN_10DE` NVIDIA, `VEN_1002/1022` AMD, `VEN_8086` Intel) and falls back to the name.
- VRAM comes from the display-class registry key `qwMemorySize`, not `Win32_VideoController.AdapterRAM`, which is 32-bit and wraps on cards with 4 GB or more.
- The NVIDIA driver version is shown in marketing form (`32.0.15.6636` → `566.36`).
- **Install/Update** (`InstallOrUpdateAsync`):
  - NVIDIA opens the Microsoft Store page for NVIDIA App (`XP8CLZL93F5Z4P`). The companion button opens NVIDIA Control Panel (`9NF8H0H7WMLT`).
  - Intel opens the Store page for Intel Graphics Command Center (`9PLFNLNT3G5G`).
  - AMD has no Store app. The service scrapes `amd.com/en/support/download/drivers.html` for the current `drivers.amd.com/...exe` URL, downloads it to `%TEMP%`, checks for a real PE executable (AMD's CDN returns an HTML 200 for unknown paths), then runs it.
  - If any of these steps fails, it opens the vendor's official page.
- **NVIDIA driver picker** (`OpenNvidiaPicker` / `InstallSelectedNvidiaDriverAsync`): a curated list from `GetCuratedNvidiaDrivers()` with desktop and notebook URLs, used for installs or rollbacks. The notebook variant is preselected when the adapter name contains "Laptop".

### BIOS & firmware
- `GetFirmwareInfoAsync` reads `Win32_BIOS`, `Win32_BaseBoard`, `Win32_ComputerSystemProduct`, Secure Boot / UEFI state from the registry, and firmware PnP devices. Results are cached for 5 minutes.
- `CheckForFirmwareUpdatesAsync` searches Windows Update over COM (`Microsoft.Update.Session`) for pending updates whose titles match firmware/BIOS/UEFI patterns.
- `OpenVendorBiosPortal` opens the OEM support page: Lenovo and Dell pages are pre-filled with the serial number, and ASUS, HP, Acer, MSI, Gigabyte and ASRock go to their support pages. Other vendors get a Google search.
- `TriggerInteractiveScan` runs `usoclient.exe StartInteractiveScan`.

### Elevated 3-step updater
`DriverUpdateService.LaunchDriverAndSystemUpdate()` writes `%LOCALAPPDATA%\AxePower\Scripts\UpdateDrivers.ps1` and runs it minimized through PowerShell with `Verb = "runas"`:
1. Windows Update over the COM API: search, download and install every pending software update. Falls back to `usoclient StartInteractiveScan`.
2. Detects GPUs, then runs `winget upgrade` on the matching vendor apps (NVIDIA App/GFE, AMD Software, Intel DSA/Arc Control).
3. `winget upgrade --all --include-unknown --silent`.

If a window titled "AxePower - Driver & System Update" already exists, the service focuses it instead of starting a second copy. Results: `StartedNew`, `FocusedExisting`, `CancelledByUser` (UAC error 1223), `Failed`.

### Upgrade all apps
`UpgradeAllAppsAsync` switches to the Terminal page and streams `winget upgrade --all --include-unknown` output there (5-minute timeout).

## Tab: "🛡️ Windows Update Shield" (`Updates`)

| Toggle | Registry (HKLM) | Values |
|---|---|---|
| Security updates only | `SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` | `DeferFeatureUpdates=1`, `DeferFeatureUpdatesPeriodInDays=365`, `BranchReadinessLevel=16` |
| Block forced reboots | `...\WindowsUpdate\AU` | `NoAutoRebootWithLoggedOnUsers=1`, `AUOptions=3` |

Turning a toggle off deletes the values it set. One exception: turning off "Block forced reboots" leaves `AUOptions=3` in place. Both toggles need admin rights to write HKLM.

**Create System Restore Point** (`CreateRestorePointAsync`) refuses unless the process is elevated. The PowerShell script it runs:
1. Enables System Restore on the system drive.
2. Sets `SystemRestorePointCreationFrequency=0`, removing the 24-hour throttle.
3. Runs `Checkpoint-Computer -RestorePointType MODIFY_SETTINGS`.

Output streams to the Terminal.

## Output
Every step calls `AppendLog`, which keeps a local buffer (trimmed at 20 KB) and mirrors each line to `TerminalService.AppendExternalLine`. The Terminal page is the single place users see the output. See [terminal.md](terminal.md).

## Requirements
- Administrator rights for the Shield toggles, the restore point and the 3-step updater (which prompts UAC itself).
- `winget` for the upgrade steps, and the Microsoft Store for NVIDIA and Intel driver installs.
- Internet access for the AMD page scrape, Windows Update and winget.

## Known gaps
- **"Reset & Repair Cache" does nothing.** `SoftwareHubView.xaml` binds `ResetUpdateCacheCommand`, but `SoftwareHubViewModel` has no such command. The logic exists as `WindowsUpdateShieldService.ResetWindowsUpdateCacheAsync`, which stops wuauserv/bits/cryptSvc/msiserver and clears `SoftwareDistribution\Download`, but no code calls it.
- `DriverUpdateService.LaunchDriverAndSystemUpdate` installs **every** pending Windows Update without confirmation, including optional driver updates.
- The AMD installer URL is scraped from HTML, so an AMD page redesign breaks it (it then falls back to opening the page).

## Tests
[SoftwareHubTests.cs](../AxePower.Tests/SoftwareHubTests.cs)
