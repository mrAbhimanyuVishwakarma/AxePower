---
name: uninstaller
description: Uninstaller Pro — multi-source app discovery (registry/MSI/AppX/MSIX/winget/orphaned), protected-app rules, sequential batch uninstall with per-type executors, restore point, confidence-scored leftover scan and cleanup.
---

# Uninstaller Pro

Uninstaller Pro finds every installed app, removes the selected ones one at a time with the right native uninstaller, then scans for leftovers that probably belong to each removed app. It is an **app-specific** cleaner, not a generic registry cleaner: every leftover must be traceable to the app just removed.

## Where it lives

| Piece | File |
|---|---|
| View / VM | [UninstallerView.xaml](../AxePower.App/Views/UninstallerView.xaml), [UninstallerViewModel.cs](../AxePower.App/ViewModels/UninstallerViewModel.cs), row VM [InstalledApplicationViewModel.cs](../AxePower.App/ViewModels/InstalledApplicationViewModel.cs) |
| Services | [AxePower.Core/Services/Uninstaller/](../AxePower.Core/Services/Uninstaller/) |
| Models | [AxePower.Core/Models/Uninstaller/](../AxePower.Core/Models/Uninstaller/) |
| DI registration | [App.xaml.cs](../AxePower.App/App.xaml.cs) ("Uninstaller Pro — Core Services") |

## Pipeline

```
ApplicationDiscoveryService.DiscoverApplicationsAsync
   registry (HKLM, HKLM\WOW6432Node, HKCU Uninstall keys)
 + Get-AppxPackage (AppX / MSIX)
 + winget list
 → dedupe across sources, mark Orphaned when the uninstaller is missing
 → ProtectionService flags protected apps (hidden unless ShowProtectedApplications)

UninstallSelected → BatchUninstallService.ExecuteBatchAsync   (strictly sequential)
   1. SystemRestoreService.TryCreateRestorePointAsync  (if CreateSystemRestorePoint && !DryRun; failure is non-fatal)
   2. for each app:
        ProcessDetectionService   → logs a warning if the app is running (does not block)
        UninstallExecutorFactory  → pick an executor (Orphaned → none)
        executor.ExecuteAsync
        LeftoverScannerService.ScanAsync  (if AutoScanLeftovers and (success or orphaned))
   3. UninstallHistoryService.SaveSessionAsync

Leftover review → CleanSelectedLeftoversAsync
   registry → RegistryBackupService.BackupAsync, then delete
   file / folder → FileCleanupService (Recycle Bin when MoveToRecycleBin)
```

A failure on one app is recorded and the queue keeps going.

## Executors (`UninstallExecutorFactory`)

| `ApplicationType` | Executor | Command |
|---|---|---|
| `Win32` | [Win32UninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/Win32UninstallExecutor.cs) | `QuietUninstallString` if present, otherwise `UninstallString`, run interactively. It never appends `/S` or `/SILENT` to arbitrary executables. |
| `MSI` | [MsiUninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/MsiUninstallExecutor.cs) | `msiexec /x {ProductCode} /qb /norestart /l*v "<log>"` |
| `AppX` / `MSIX` | [AppxUninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/AppxUninstallExecutor.cs) | `Remove-AppxPackage -Package '<PackageFullName>'` |
| `WinGet` | [WingetUninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/WingetUninstallExecutor.cs) | `winget uninstall …` |
| `Orphaned` | none | Goes straight to the leftover scan. |

A `Win32` entry with a valid MSI `ProductCode` is promoted to the MSI executor. Every executor honours `DryRunMode`, which only logs "Would execute: …".

## Leftover scanning
- **Scan depth** (`ScanMode`):
  - `Safe`: exact known locations only.
  - `Moderate` (default): adds publisher subtrees and AppData/ProgramData name matches.
  - `Advanced`: adds CLSIDs, GUIDs, shell integration, services and startup entries.
- **Scoring** ([LeftoverConfidenceService.cs](../AxePower.Core/Services/Uninstaller/LeftoverConfidenceService.cs)) is additive, 0–100. Labels: ≥95 Very High, ≥80 High, ≥60 Medium, ≥40 Low, otherwise Very Low.
- A **protected path** always wins and is never deletable. A **shared component** (for example common runtimes) is flagged and never preselected.
- **Preselection:** only items with score ≥ 80 that are neither protected nor shared are checked by default.
- User exclusions: `UninstallerSettings.ExcludedRegistryPaths` and `ExcludedFilePaths`.

## Settings (`UninstallerSettings`)
`DefaultScanMode=Moderate`, `CreateSystemRestorePoint=true`, `BackupRegistryChanges=true`, `MoveToRecycleBin=true`, `AutoScanLeftovers=true`, `ShowProtectedApplications=false`, `ShowOrphanedApplications=true`, `AllowAdvancedScan=true`, `DryRunMode=false`.

## List filters
`SelectFilter` accepts: `Win32`, `MSI`, `Store Apps` (AppX + MSIX), `WinGet`, `Orphaned`, plus the default all-apps view.

## Storage
- Session history: `%APPDATA%\AxePower\UninstallerHistory\sessions.json`
- Registry backup (intended): `%APPDATA%\AxePower\UninstallerHistory\{sessionId}\registry-backup.json`

## Known gaps (verified in code)
- **Registry backups are not actually persisted.** `RegistryBackupService.BackupAsync` only builds an in-memory `RegistryBackupItem`. For whole keys (`valueName == null`) it captures no data at all. Nothing calls `SaveSessionBackupsAsync` or `RestoreSessionAsync`, and `CleanSelectedLeftoversAsync` passes a fresh `Guid` as the session ID for every item. In practice, deleted registry keys cannot be restored today. The in-app help text ("automatic registry snapshots are preserved") overstates this.
- Leftover types other than registry, file and folder (for example startup entries) are counted as "cleaned" without any action being taken.

## Tests
[AxePower.Tests/Uninstaller/](../AxePower.Tests/Uninstaller/)
