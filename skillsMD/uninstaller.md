---
name: uninstaller
description: Uninstaller Pro — registry/AppX discovery with uninstaller-kind detection (MSI, Burn, Inno, NSIS, InstallShield, Squirrel), one protection rule set, confirmed sequential batch that waits for the whole uninstaller process tree and verifies every removal, evidence-based leftover finders, fail-closed .reg backups, honest per-item cleanup results, history with restore.
---

# Uninstaller Pro

Uninstaller Pro removes installed apps with their own uninstallers, **verifies** each removal against the registry / Windows Installer / AppX, and only then offers leftovers it can tie to the removed app by **evidence**. It is an app-specific cleaner, not a registry cleaner. The design and its rationale are in [uninstaller.md](../uninstaller.md) (the fix plan); ideas and parts of the code are adapted from [Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) (Apache-2.0, see [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)).

## Where it lives

| Piece | File |
|---|---|
| View / VM | [UninstallerView.xaml](../AxePower.App/Views/UninstallerView.xaml), [UninstallerViewModel.cs](../AxePower.App/ViewModels/UninstallerViewModel.cs) (apps, batch, settings) and [UninstallerViewModel.Leftovers.cs](../AxePower.App/ViewModels/UninstallerViewModel.Leftovers.cs) (review, cleanup, history), rows in [InstalledApplicationViewModel.cs](../AxePower.App/ViewModels/InstalledApplicationViewModel.cs) and [UninstallerItemViewModels.cs](../AxePower.App/ViewModels/UninstallerItemViewModels.cs) |
| Dialogs | [UninstallerPrompts.cs](../AxePower.App/Services/UninstallerPrompts.cs) (`IUninstallerPrompts`, message boxes; tests use a fake) |
| Services | [AxePower.Core/Services/Uninstaller/](../AxePower.Core/Services/Uninstaller/), leftover finders in [Leftovers/](../AxePower.Core/Services/Uninstaller/Leftovers/) |
| Models | [AxePower.Core/Models/Uninstaller/](../AxePower.Core/Models/Uninstaller/) |
| DI registration | [App.xaml.cs](../AxePower.App/App.xaml.cs) ("Uninstaller Pro — Core Services"), mirrored in [DependencyInjectionTests.cs](../AxePower.Tests/Uninstaller/DependencyInjectionTests.cs) |

## Pipeline

```
DISCOVERY  ApplicationDiscoveryService
  HKLM (64-bit), HKLM\SOFTWARE\WOW6432Node, HKCU uninstall keys  +  Get-AppxPackage -PackageTypeFilter Main (JSON)
  per key: UninstallEntryValues → UninstallEntryClassifier
             UninstallCommandParser  (never resolves a relative path; bare names via System32 / Windows / PATH)
             UninstallerKindDetector (+ MsiQueryProductState)
  merge double registrations (ProductCode → UninstallString → InstallLocation+publisher → name+publisher+version);
  every merged key kept in RelatedKeyPaths
  ProtectionService flags protected apps (the only protection rule set)

CONFIRM  UninstallCommandBuilder → one UninstallPlan per app → IUninstallerPrompts.ConfirmBatch
         protected picks are skipped one by one, never abort the batch

BATCH  BatchUninstallService (strictly sequential)
  restore point (optional)
  per app: protection → pre-check (key still there? else AlreadyRemoved) → running app? (close / skip / continue)
           → executor by kind → ProcessTreeWatcher (whole tree) → ExitCodeInterpreter → InstallStateVerifier
           → leftover scan only for Removed / AlreadyRemoved / BrokenEntryRemoved, against a fresh registry snapshot
  session saved (also when stopped)

LEFTOVER REVIEW  LeftoverScannerService (finders + evidence) → review list → LeftoverCleanupService
  per item: protection re-check → needs admin? → backup (fail closed) → delete → verify → CleanupItemResult
  every item written to the session's manifest.json

HISTORY  UninstallHistoryService + BackupManifestStore; restore via RegistryBackupService.RestoreSessionAsync
```

## Uninstaller kinds and commands

`UninstallerKind` decides the executor and the command ([UninstallCommandBuilder.cs](../AxePower.Core/Services/Uninstaller/UninstallCommandBuilder.cs)). The key name alone never makes an entry MSI: a GUID key name only supplies the ProductCode when the entry says `WindowsInstaller=1`.

| Kind | Detected by | Interactive (default) | Quiet (`PreferQuietUninstall`) |
|---|---|---|---|
| `Msi` | command runs msiexec, or `WindowsInstaller=1`; product registered | `msiexec /x {PC} /qb /norestart /l*v "<log>"` | same with `/qn` |
| `BurnBundle` | `…\Package Cache\{guid}\*.exe`, `BundleCachePath`, `BundleProviderKey` | `"<exe>" /uninstall` | `/uninstall /quiet /norestart` |
| `InnoSetup` | `unins###.exe` with its `.dat` | `"<exe>"` | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |
| `Nsis` | exe contains "Nullsoft" (first 16 MB, cached) | registry args | registry args + `/S` |
| `Squirrel` | `Update.exe --uninstall` | `--uninstall` | `--uninstall -s` |
| `InstallShield`, `PowerShell`, `Unknown` | … | registry command as-is | never gets switches |
| `StoreApp` | AppX discovery | `Remove-AppxPackage -Package '<pfn>'` | same |
| `BrokenEntry` | uninstaller (or rundll32 DLL) missing, or MSI product not registered | "Remove broken entry": `.reg` backup, then delete only the listing | same |

In quiet mode a vendor `QuietUninstallString` wins over generated switches (except MSI, which always uses the logged command). Executors: [Win32UninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/Win32UninstallExecutor.cs), [MsiUninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/MsiUninstallExecutor.cs) (retries 1618 twice after 30 s), [AppxUninstallExecutor.cs](../AxePower.Core/Services/Uninstaller/AppxUninstallExecutor.cs), [BrokenEntryExecutor.cs](../AxePower.Core/Services/Uninstaller/BrokenEntryExecutor.cs). Uninstallers start with `UseShellExecute` so their own manifest asks for UAC; a refused prompt (1223) is `Cancelled`.

## Waiting and verification

- [ProcessTreeWatcher.cs](../AxePower.Core/Services/Uninstaller/ProcessTreeWatcher.cs) polls Toolhelp snapshots every second and follows every descendant (parent PID ever watched + created after it), msiexec custom actions for MSI/Burn, and after a 2 s grace period stragglers in the install folder or %TEMP% (`Au_.exe`, `_iu*.tmp`). It never kills anything unless the user chooses **Stop uninstaller** (quiet uninstalls only) or answers the stall prompt (`QuietStallMinutes`).
- [ExitCodeInterpreter.cs](../AxePower.Core/Services/Uninstaller/ExitCodeInterpreter.cs) only chooses the wording. [InstallStateVerifier.cs](../AxePower.Core/Services/Uninstaller/InstallStateVerifier.cs) decides: the primary key must be gone (polled up to 10 s), a related key only counts while it still has a working uninstaller, an MSI product must report −1, a Store package must no longer be listed.
- Outcomes (`UninstallOutcome`): `Removed`, `AlreadyRemoved`, `RemovedPendingReboot`, `StillInstalled` (with "handed off" wording for launchers such as Steam, and **Remove broken entry** offered when the uninstaller has gone), `Cancelled`, `Failed`, `BrokenEntryRemoved`, `Skipped`, `NeedsAdmin`, `DryRun`. Only the first three and `BrokenEntryRemoved` count as success.

## Leftovers

[LeftoverScannerService.cs](../AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs) runs the finders for the scan mode, merges duplicates, applies protection, scores and pre-ticks.

| Mode | Finders |
|---|---|
| Safe (explicit evidence only) | install/uninstaller folder, uninstall keys whose uninstaller is gone, Run/RunOnce values and Startup shortcuts, Start Menu and Desktop shortcuts (a Start Menu folder holding only the app's shortcuts is offered whole) |
| Moderate (default) | + folders named after the app (or under its publisher) in AppData, LocalLow, ProgramData, Program Files, Common Files; Software keys in HKCU/HKLM/WOW6432Node |
| Advanced | + App Paths, compatibility flags, MUI cache, COM `CLSID\{guid}` (HKLM only when elevated), scheduled tasks; services and firewall rules for information only |

- Names: [AppNameNormalizer.cs](../AxePower.Core/Services/Uninstaller/Leftovers/AppNameNormalizer.cs) (BCU's trimming, Sift4 matching; names of 4 characters or fewer never match).
- Evidence: [ConfidenceRecords.cs](../AxePower.Core/Services/Uninstaller/Leftovers/ConfidenceRecords.cs) (BCU's points, plus `TargetStillExists` −4). Score = clamp(50 + 10 × Σ, 0, 100).
- Pre-ticked only with `ExplicitConnection` or `IsUninstallerRegistryKey`, Σ ≥ 4, none of `DirectoryStillUsed` / `ProgramNameIsStillUsed` / `PublisherIsStillUsed` / `TargetStillExists`, not blocked, and deletable with the current rights. Never for broken entries or apps still installed (a scan from the list is informational: the app's own folder is blocked).

## Protection ([ProtectionService.cs](../AxePower.Core/Services/Uninstaller/ProtectionService.cs), [ProtectedPaths.cs](../AxePower.Core/Services/Uninstaller/ProtectedPaths.cs))

- Apps: AxePower itself, non-removable Store packages, `SystemComponent` entries, Windows / VC++ / .NET / DirectX name prefixes.
- Files: drive roots, special folders and every ancestor of them (Program Files, AppData, Documents, Start Menu, Temp, `C:\Users`, Package Cache…), whole trees for Windows, WindowsApps, the `Microsoft` folders, `dotnet`, `Windows*` under Program Files, `%LOCALAPPDATA%\Packages`; anything that is or contains another installed app's folder. Items inside the Start Menu Programs / Startup folders stay allowed, except Windows' own groups.
- Registry: only HKLM\SOFTWARE and HKCU\Software, never hive roots; inside `Microsoft` only single uninstall / App Paths entries and Run, RunOnce, AppCompat and MUI-cache **values**; inside `Classes` only `CLSID\{guid}` keys (with explicit evidence); Policies, Clients, ODBC, all Windows Installer data and the Uninstall root itself are blocked.
- Needs admin: HKLM, Program Files, ProgramData, the Windows folder, public Start Menu / Desktop, machine tasks. Locked in the review until **Restart as administrator** (the review is saved and can be resumed from History).

## Backups and history

- `%LOCALAPPDATA%\AxePower\UninstallerHistory\` (moved once from %APPDATA%): `sessions.json` (last 50, atomic, upserted by session id) and one folder per session with `NNN.reg` (`reg export … /reg:64`), `tasks\NNN.xml`, and `manifest.json` listing every item, its backup and its result. Typed value backups live in the manifest.
- A failed backup leaves the item in place (`SkippedBackupFailed`). `BackupRegistryChanges = false` only skips HKCU backups; files use the Recycle Bin (no silent fall-back to a permanent delete).
- History panel: outcomes per app, **Open log** (MSI), **Open backups**, **Restore registry backup** (`reg import` in reverse order, then values, then tasks), **Resume leftover review**.

## Settings

`AppSettings.Uninstaller` ([UninstallerSettings.cs](../AxePower.Core/Models/Uninstaller/UninstallerSettings.cs)), saved from the Settings panel: scan depth, prefer silent uninstall (off), restore point (on), back up HKCU registry (on), Recycle Bin (on), scan after uninstall (on), show protected / broken / system components / updates / blocked leftovers, stall minutes (10), dry run, excluded paths.

## Tests

[AxePower.Tests/Uninstaller/](../AxePower.Tests/Uninstaller/): parser, kind detector, classifier (driven by [Fixtures/arp-entries.json](../AxePower.Tests/Uninstaller/Fixtures/arp-entries.json), 152 real entries), exit codes and commands, batch outcomes with fakes, process-tree watcher (cmd/ping), protection, scoring and name matching, cleanup and backup round-trips under `HKCU\Software\AxePower.Tests\{guid}`, history, view-model safety, and a binding-error check of the view. No test uninstalls software; [LiveEchoMusicUninstallTest.cs](../AxePower.Tests/Uninstaller/LiveEchoMusicUninstallTest.cs) is skipped and only for manual runs.

## Not done yet

The optional extras in the plan's Phase 5: an elevated one-shot cleanup helper, WinGet via its COM API, NSIS UI automation, firewall-rule deletion, and Steam / Windows-feature / portable-app sources.
