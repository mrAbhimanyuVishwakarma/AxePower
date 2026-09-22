# Uninstaller Pro: Fix & Completion Plan 🧹

> **Status:** Plan, not started · **Written:** 2026-09-22 · **Branch at time of audit:** `v1.8`
> **Current behaviour (as built):** [skillsMD/uninstaller.md](skillsMD/uninstaller.md)
> **Main reference:** [Bulk Crap Uninstaller (BCU)](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller), Apache-2.0
> **Repo rules this plan follows:** [system-safety.md](.agents/plugins/axepower-wpf/rules/system-safety.md), [testing-and-quality.md](.agents/plugins/axepower-wpf/rules/testing-and-quality.md)

This is the working plan for fixing and finishing Uninstaller Pro. It covers:
- every defect found in the audit, with file and line references;
- what we take from BCU and where to find it in BCU's source;
- the target design;
- a phased task list with acceptance criteria;
- the test plan and a manual test matrix.

**How to use it:** work through [section 7](#7-work-plan) in order, ticking the checkboxes. Every defect `D#` in [section 4](#4-defects) names the task that fixes it, and every task names the defects it closes. When a phase ships, update [skillsMD/uninstaller.md](skillsMD/uninstaller.md) so it describes the new behaviour.

---

## Contents

1. [Summary](#1-summary)
2. [Ground rules](#2-ground-rules)
3. [What already works (keep it)](#3-what-already-works-keep-it)
4. [Defects](#4-defects)
5. [What we take from BCU](#5-what-we-take-from-bcu)
6. [Target design](#6-target-design)
7. [Work plan](#7-work-plan)
8. [Automated tests](#8-automated-tests)
9. [Manual test matrix](#9-manual-test-matrix)
10. [Open decisions](#10-open-decisions)
- [Appendix A: Uninstall registry values](#appendix-a-uninstall-registry-values)
- [Appendix B: BCU source map](#appendix-b-bcu-source-map)
- [Appendix C: Scripts to reproduce and capture fixtures](#appendix-c-scripts)
- [Appendix D: Glossary](#appendix-d-glossary)

---

## 1. Summary

Uninstaller Pro looks complete, but its core flow is wrong for most apps:

- **Most MSI apps are never uninstalled.** A relative-path `File.Exists("MsiExec.exe")` check marks them "Orphaned". Orphans skip the real uninstaller and are reported as **success**. The leftover review then pre-ticks the app's own uninstall registry key. On the audit PC this affected **74 of 152** uninstall entries, including RustDesk and ONLYOFFICE.
- **Entries whose registry key name is a GUID are treated as MSI**, even when they are ordinary exe uninstallers. These get sent to `msiexec /x`, which fails. On the audit PC: **11 entries**, including Google Drive and Samsung USB Driver.
- **We stop waiting too early and never verify.** NSIS uninstallers hand off to a copy in %TEMP% and exit at once. We count that exit as success and start the leftover scan while the uninstall wizard is still open. Nothing checks whether the app is actually gone.
- **Safety features don't work.**
  - Registry "backups" save no data.
  - Cleanup counts failed and skipped items as cleaned.
  - Nothing stops a bad `InstallLocation` from offering `C:\Program Files` or a drive root for deletion.

The fix follows BCU's proven model:
1. Classify each uninstaller by what it really is.
2. Wait for the uninstaller's whole process tree to exit.
3. **Verify** the result against the registry, and report only what is true.
4. Find leftovers by evidence rather than guessing names, with hard safety rails.

---

## 2. Ground rules

Every task must respect these rules. Most come from the repo rules; the rest are lessons from BCU.

1. **No false success.** An app counts as removed only when verification says so (its registry key is gone, its MSI product is no longer registered, or its AppX package is no longer listed). Exit codes only decide the *wording* of a failure. A skipped item is never counted as cleaned.
2. **Standard user by default.** AxePower keeps its `asInvoker` manifest. Uninstallers elevate themselves. Work that needs admin (HKLM keys, machine folders) is locked until the user chooses **Restart as administrator** (the existing `ElevationService.TryRelaunchElevated`), or later goes through an optional one-shot elevated helper ([P5.1](#phase-5-optional-extras)).
3. **Back up before deleting, and fail closed.** If a backup fails, the item is **not** deleted.
4. **App-specific only.** Every leftover must carry evidence tying it to the removed app. Items without evidence are never pre-ticked. Items that another installed app still uses are never selectable.
5. **Never touch the Windows Installer database, services, system folders or shared runtimes.**
6. **No arbitrary command execution.** Run the resolved uninstaller exe directly with its registry arguments, never through `cmd /c`. Our own helper commands (`reg.exe`, `msiexec.exe`, `powershell.exe`) are allow-listed, and their arguments are built with `ArgumentList`.
7. **Tests use fakes.** No automated test may uninstall software or change system state outside a disposable `HKCU\Software\AxePower.Tests\…` key or a temp folder.
8. **Attribution.** Code ported from BCU keeps an Apache-2.0 attribution header and is listed in `THIRD_PARTY_NOTICES.md` ([P0.2](#phase-0-prep)).

---

## 3. What already works (keep it)

- A sequential batch in which one app's failure doesn't stop the queue ([BatchUninstallService.cs](AxePower.Core/Services/Uninstaller/BatchUninstallService.cs)).
- An optional System Restore point before the batch ([SystemRestoreService.cs](AxePower.Core/Services/Uninstaller/SystemRestoreService.cs)).
- Dry-run mode in every executor.
- Recycle Bin deletion for files and folders ([FileCleanupService.cs](AxePower.Core/Services/Uninstaller/FileCleanupService.cs)).
- A verbose MSI log per uninstall in `%LOCALAPPDATA%\AxePower\UninstallLogs`.
- Non-removable AppX packages are respected. AppX and WinGet arguments are passed through `ArgumentList` after validation.
- Registry deletes are refused for shallow paths (fewer than two segments).
- User exclusion lists (`ExcludedRegistryPaths`, `ExcludedFilePaths`).
- The deduplication priority order (ProductCode → PackageFullName → UninstallString → InstallLocation → name+publisher+version).

---

## 4. Defects

Severity: 🔴 wrong or destructive result · 🟠 misleading or unsafe · 🟡 quality.

| ID | Sev | Defect | Fixed by |
|---|---|---|---|
| D1 | 🔴 | MSI entries are mislabelled Orphaned; the uninstaller is skipped, success is reported, and the app's own uninstall key is pre-ticked | P1.1, P1.2, P1.3, P1.7 |
| D2 | 🔴 | GUID key name ⇒ MSI; exe and bundle uninstallers get `msiexec /x` | P1.2, P1.6 |
| D3 | 🔴 | Waits only for the launched process; NSIS temp copies cause false success and scans during the wizard | P1.5 |
| D4 | 🔴 | No post-uninstall verification; exit code 0 means success; a refused UAC prompt shows as a failure | P1.4, P1.7 |
| D5 | 🟠 | Registry backup saves no data; restoring values is broken | P1.9 |
| D6 | 🟠 | Cleanup counts failed and skipped items as "cleaned" | P1.9 |
| D7 | 🟠 | Unsafe folder candidates; path protection too narrow | P1.8 |
| D8 | 🟠 | Scanner probes the Windows Installer database (`Installer\UserData`) | P1.8 |
| D9 | 🟡 | Leftover search uses raw names (with versions or "Inc."), so it misses most leftovers | P3.* |
| D10 | 🟠 | Refresh and Scan cancel a running batch (shared CTS); there is no Cancel | P1.10 |
| D11 | 🟠 | The details-panel **Uninstall** acts on checked rows, not the viewed app | P1.10 |
| D12 | 🟠 | No confirmation; one protected pick aborts the whole batch | P1.10 |
| D13 | 🟡 | The Settings button has no panel; settings are never saved | P2.4 |
| D14 | 🟡 | Discovery noise: fragile WinGet parsing, AppX `\r` bug, hidden components and updates listed, two different protection rules | P1.3, P2.5 |
| D15 | 🟡 | Running-app detection fails on elevated or other-bitness processes; running apps are never closed or asked about | P1.5, P1.10 |
| D16 | 🟡 | In-app help promises registry snapshots that don't exist | P1.10 |
| D17 | 🟠 | A test uninstalls a real app during `dotnet test` | P0.1 |

### D1: MSI entries mislabelled "Orphaned" 🔴
- **Where:**
  - [ApplicationDiscoveryService.cs:149-167](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L149-L167)
  - `ExtractExecutablePath` at [L545-569](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L545-L569)
  - Orphans skip the uninstaller in [BatchUninstallService.cs:93-106](AxePower.Core/Services/Uninstaller/BatchUninstallService.cs#L93-L106)
- **What happens:**
  1. For `MsiExec.exe /X{GUID}`, `ExtractExecutablePath` returns the bare `MsiExec.exe`.
  2. `File.Exists("MsiExec.exe")` resolves against the **current working directory**. That is normally AxePower's install folder, so the check fails. The name check only accepts exactly `msiexec`, not `MsiExec.exe`, so the entry becomes `Orphaned`.
  3. For Orphaned apps the batch runs no uninstaller and records `Success = true` (`UninstallMethod = "ForcedOrphan"`).
  4. The Moderate scan then adds `HKLM\…\Uninstall\{ProductCode}` ([LeftoverScannerService.cs:122-130](AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs#L122-L130)). It scores 40 + 40 = 80 ([LeftoverConfidenceService.cs:72-79](AxePower.Core/Services/Uninstaller/LeftoverConfidenceService.cs#L72-L79), [L132-141](AxePower.Core/Services/Uninstaller/LeftoverConfidenceService.cs#L132-L141)), so it is **pre-ticked**.
  5. If AxePower is elevated, **Clean** deletes it. The app disappears from Windows while staying fully installed and MSI-registered.
- **Why it's flaky:** the result depends on the working directory. When AxePower starts elevated from the logon task, the working directory can be `System32` and the bug disappears.
- **Evidence (audit PC):** 74 of 152 entries use unquoted `MsiExec.exe /X…`, including RustDesk and ONLYOFFICE 9.4.0. A tell-tale sign in the UI: the **MSI** filter chip is almost empty and **Orphaned** is full.

### D2: GUID key name treated as MSI 🔴
- **Where:** [ApplicationDiscoveryService.cs:144](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L144) and [L169-171](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L169-L171) (key name copied into `ProductCode`). [UninstallExecutorFactory.cs:33-35](AxePower.Core/Services/Uninstaller/UninstallExecutorFactory.cs#L33-L35) then promotes any Win32 entry that has a GUID `ProductCode` to MSI.
- **What happens:** InstallShield, WiX Burn bundles and plain exe uninstallers whose key name is a GUID run `msiexec /x {GUID}`. That returns 1605 (unknown product) instead of running the real uninstaller.
- **Evidence (audit PC):** 11 entries, including Google Drive (`…\uninstall.exe`), Samsung USB Driver, and the VC++ and .NET Desktop Runtime bundles (`"C:\ProgramData\Package Cache\{…}\*.exe" /uninstall`).

### D3: Waiting only for the launched process 🔴
- **Where:** [Win32UninstallExecutor.cs:66-79](AxePower.Core/Services/Uninstaller/Win32UninstallExecutor.cs#L66-L79)
- **What happens:**
  1. NSIS uninstallers copy themselves to `%TEMP%` (`Au_.exe` / `Un_A.exe`), start the copy and exit with code 0 at once.
  2. We wait only for that first process, so we report success within about a second.
  3. The batch then scans leftovers while the wizard is still open. The install folder and registry keys are still there, so they are offered as leftovers.
  4. If the user cancels the wizard, the result stays "success".

  BCU handles this by watching the uninstaller's entire process tree (see [§5](#5-what-we-take-from-bcu)).

### D4: No verification 🔴
- **Where:** all executors; [BatchUninstallService.cs:135](AxePower.Core/Services/Uninstaller/BatchUninstallService.cs#L135)
- **What happens:**
  - Success means exit code 0, or 3010/1641.
  - Nothing re-checks the registry key, the MSI product state or the AppX package.
  - `InstalledApplication` doesn't even store its registry key path.
  - A refused UAC prompt (Win32 error 1223) falls into the generic `catch` and is shown as a failure rather than "Cancelled".

### D5: Registry backup is a no-op 🟠
- **Where:** [RegistryBackupService.cs:40-58](AxePower.Core/Services/Uninstaller/RegistryBackupService.cs#L40-L58), [L95-130](AxePower.Core/Services/Uninstaller/RegistryBackupService.cs#L95-L130), caller [UninstallerViewModel.cs:349-360](AxePower.App/ViewModels/UninstallerViewModel.cs#L349-L360)
- **What happens:**
  - For key deletes (`valueName == null`), `BackupAsync` records only the key name, not its contents.
  - It is called with a new `Guid` session ID for every item, and its return value is thrown away.
  - `SaveSessionBackupsAsync` is never called.
  - `RestoreSessionAsync` would recreate empty keys.
  - Value restore deserializes into `JsonElement` ([L116](AxePower.Core/Services/Uninstaller/RegistryBackupService.cs#L116)). `SetValue` throws on that for anything except strings.

### D6: Dishonest cleanup counts 🟠
- **Where:** [UninstallerViewModel.cs:359-378](AxePower.App/ViewModels/UninstallerViewModel.cs#L359-L378), [L502-542](AxePower.App/ViewModels/UninstallerViewModel.cs#L502-L542)
- **What happens:**
  - `DeleteRegistryKey` swallows every failure (HKLM without admin, protected path, shallow path), yet the caller always does `cleaned++`.
  - `StartupEntry` and other types hit `default: cleaned++` with no action taken.
  - This breaks the repo rule "Never use fake success reports".

### D7: Unsafe folder candidates 🟠
- **Where:** `IsPathProtected` [ProtectionService.cs:98-117](AxePower.Core/Services/Uninstaller/ProtectionService.cs#L98-L117); InstallLocation always added [LeftoverScannerService.cs:227-230](AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs#L227-L230); `LocalLow\{Publisher}` [L381-382](AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs#L381-L382)
- **What happens:**
  - Protection covers only Windows, System32, WindowsApps and `Program Files\Windows NT`.
  - An app with a bad `InstallLocation` (`C:\Program Files`, `C:\`, `%LOCALAPPDATA%`) gets that folder offered, at 50+ confidence, one tick away from deletion.
  - Folders that contain another installed app aren't blocked.
  - The Advanced scan offers the entire `LocalLow\{Publisher}` folder, which other apps from the same publisher share.

### D8: Windows Installer database probe 🟠
- **Where:** [LeftoverScannerService.cs:96-100](AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs#L96-L100)
- **What happens:**
  - `Installer\UserData` has SIDs as its direct children, and products are stored under *packed* GUIDs, so this search never matches.
  - It is also the one area we must never delete from. Remove it.

### D9: Name-only leftover search 🟡
- **Where:** [LeftoverScannerService.cs:103-118](AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs#L103-L118), [L236-262](AxePower.Core/Services/Uninstaller/LeftoverScannerService.cs#L236-L262)
- **What happens:**
  - Paths are built from the raw `DisplayName` and `Publisher` (for example `HKCU\SOFTWARE\Google LLC\Google Chrome 128.0`), so they rarely exist.
  - There is no fuzzy matching.
  - There is no search for items that *reference* the install folder (shortcuts, Run keys, scheduled tasks, COM, App Paths…).
  - There are no penalties for folders still used by other apps.

### D10: Shared cancellation 🟠
- **Where:** [UninstallerViewModel.cs:136-137](AxePower.App/ViewModels/UninstallerViewModel.cs#L136-L137), [L253-254](AxePower.App/ViewModels/UninstallerViewModel.cs#L253-L254), [L302-303](AxePower.App/ViewModels/UninstallerViewModel.cs#L302-L303)
- **What happens:**
  - Discovery, the batch and leftover scans all share `_discoveryCts`. Clicking **Refresh** or **Scan Leftovers** during an uninstall cancels the batch. The uninstaller keeps running unobserved and the session is never saved.
  - There is no Cancel button.

### D11: Details-panel button acts on the wrong apps 🟠
- **Where:** [UninstallerView.xaml:570-577](AxePower.App/Views/UninstallerView.xaml#L570-L577)
- **What happens:** The panel's **Uninstall** is bound to `UninstallSelectedCommand`, which uninstalls the *checked* rows. With nothing checked it says "No applications selected". With other rows checked, it uninstalls *those* instead of the app being viewed.

### D12: No confirmation, all-or-nothing protection 🟠
- **Where:** [UninstallerViewModel.cs:219-241](AxePower.App/ViewModels/UninstallerViewModel.cs#L219-L241)
- **What happens:**
  - The batch starts on click, with no summary of what will run.
  - Discovery and `ProtectionService` use different protection rules ([ApplicationDiscoveryService.cs:496-512](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L496-L512) vs [ProtectionService.cs:51-73](AxePower.Core/Services/Uninstaller/ProtectionService.cs#L51-L73)). A checked app that discovery considered unprotected can therefore abort the whole batch with a toast.

### D13: Settings not reachable or persisted 🟡
- **What happens:**
  - `ToggleSettings` flips `ShowSettingsPanel` ([UninstallerViewModel.cs:404-408](AxePower.App/ViewModels/UninstallerViewModel.cs#L404-L408)), but no XAML binds it.
  - `_settings = new()` ([L33](AxePower.App/ViewModels/UninstallerViewModel.cs#L33)) lives in a transient VM ([App.xaml.cs:273](AxePower.App/App.xaml.cs#L273)), so any change is lost.

### D14: Discovery noise 🟡
- **WinGet** ([L283-392](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L283-L392)):
  - parses fixed-width text by the English headers `Id` / `Version` / `Source`, so it breaks on localised Windows;
  - uses a 5-second timeout, while a cold `winget list` is often slower;
  - truncated names (`…`) don't deduplicate, which creates duplicate rows;
  - lists nothing the registry and AppX don't already provide.
- **AppX** ([L224-247](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L224-L247)):
  - Lines are split on `\n`, so the last CSV field keeps its `\r`. `SignatureKind is "System" or "Inbox"` therefore never matches.
  - The 7-second timeout is tight.
  - Names are guessed from package IDs ("Whats App Desktop").
- **Hidden entries** ([L133-135](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L133-L135)):
  - `SystemComponent=1` entries with a GUID key name are included.
  - `ParentKeyName` and `ReleaseType` (updates, hotfixes, child components) are ignored.
- **Protection:** two different rule sets (see D12).

### D15: Process detection 🟡
- **Where:** [ProcessDetectionService.cs:27](AxePower.Core/Services/Uninstaller/ProcessDetectionService.cs#L27), [L69](AxePower.Core/Services/Uninstaller/ProcessDetectionService.cs#L69); [BatchUninstallService.cs:82-84](AxePower.Core/Services/Uninstaller/BatchUninstallService.cs#L82-L84)
- **What happens:**
  - `Process.MainModule` throws for elevated and other-bitness processes, so running apps are missed.
  - Detection only logs a warning. `TryCloseApplicationAsync` exists but is never called.

### D16: Help text overstates backups 🟡
- **Where:** [MainViewModel.cs:620](AxePower.App/ViewModels/MainViewModel.cs#L620)
- **What happens:** The help says "Automatic registry snapshots are preserved…", which isn't true until P1.9 ships.

### D17: Live test uninstalls real software 🟠
- **Where:** [LiveEchoMusicUninstallTest.cs](AxePower.Tests/Uninstaller/LiveEchoMusicUninstallTest.cs)
- **What happens:** It runs `DryRunMode = false` against a real installed app on every `dotnet test`, which violates the "tests use fakes" rule.

---

## 5. What we take from BCU

[Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) by Marcin Szeniak (Klocman) is Apache-2.0 licensed and the best open-source uninstaller for Windows. Its engine is `source/UninstallTools`. The paths below are relative to `source/`. A full file map is in [Appendix B](#appendix-b-bcu-source-map).

| Concept | BCU does | We will |
|---|---|---|
| **Uninstaller kind** | `UninstallerTypeAdder.GetUninstallerType` checks, in order: msiexec or `\Package Cache\{…}\*.exe` → MSI; `sdbinst`; `InstallShield Installation Information\{` → InstallShield; `powershell`/`.ps1`; `unins###.exe` with a `.dat` next to it → Inno Setup; file contains `"Nullsoft"` → NSIS | Port it as `UninstallerKindDetector`, adding Burn (Package Cache / `BundleCachePath`) and Squirrel (`Update.exe --uninstall`) ([§6.4](#64-uninstaller-kind-detection)) |
| **MSI detection** | `PathPointsToMsiExec`: command contains `msiexec ` or `msiexec.exe`, or ends in `.msi` | Same, plus `WindowsInstaller=1`, plus `MsiQueryProductState` to confirm the product is registered |
| **Quiet commands** | Inno: `"<exe>" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`; MSI: `msiexec /x{PC} /qn /norestart`; NSIS: driven by BCU's own UI-automation helper (`UninstallerAutomatizer`) | Same for Inno and MSI; Burn `/uninstall /quiet /norestart`; NSIS `/S` (automation deferred to P5); opt-in only |
| **Waiting** | `BulkUninstallEntry`: every second, add child processes of all watched processes (plus msiexec's children for MSI); when none are left, also look for processes running from `InstallLocation` before finishing | `ProcessTreeWatcher` ([§6.6](#66-running-an-uninstaller)) |
| **Exit codes** | MSI 1602 = cancelled; NSIS 1/2 = cancelled; NSIS 1627 = OK; `0xC000013A` = terminated | Exit-code table in [§6.6](#66-running-an-uninstaller) |
| **Verification** | Before running, skips the app if `RegKeyStillExists()` is false. Status is decided by whether the key is still there. | Verify every outcome ([§6.7](#67-verification--outcomes)) |
| **Registry filtering** | `RegistryFactory.GetIsUpdate`: `ParentKeyName` set, or `ReleaseType` contains Update/Hotfix; `NoRemove`; `SystemComponent` hidden by default | Same ([§6.5](#65-discovery-rules)) |
| **Name trimming** | `DisplayNameTrimmed` = `StringTools.StripStringFromVersionNumber` (drop a trailing `(…)`, trailing digits and `.,-_`); `PublisherTrimmed` drops `(R)` and trailing `corp`, `corporation`, `limited`, `inc`, `incorporated` | Port and extend ([§6.9](#69-leftover-scanner)) |
| **Fuzzy matching** | `ConfidenceGenerators.MatchStringToProductName` using `Sift4.SimplestDistance` | Port |
| **Leftover confidence** | Signed "confidence records" per item (table in [§6.9](#69-leftover-scanner)), including penalties for folders or publishers still used by other apps, and similar-named apps | Port the records and map them to our 0–100 score |
| **Leftover finders** | `Junk/Finders/Drive` (install location, common folders, uninstaller location, WER, prefetch), `Junk/Finders/Registry` (software keys, uninstall keys, COM, AppCompat, firewall, event log, tracing, UserAssist…), `Junk/Finders/Misc` (shortcuts, startup) | Port the useful ones in stages ([P3.3](#phase-3-leftover-scanner-rebuild)) |

**Licensing:** Apache-2.0 allows copying and changing the code as long as we (a) keep a copy of the license, (b) carry BCU's `NOTICE`, and (c) mark changed files. Ported files get this header:

```csharp
// Portions adapted from Bulk Crap Uninstaller (https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)
// Copyright (c) 2017 Marcin Szeniak. Licensed under the Apache License, Version 2.0. Modified for AxePower.
```

**Other projects:**
- **UniGetUI** ([marticliment/UniGetUI](https://github.com/marticliment/UniGetUI), MIT) is the reference for talking to winget through its COM API instead of parsing console text. Use it only if WinGet integration comes back ([P5.2](#phase-5-optional-extras)).
- Revo, Geek Uninstaller and HiBit are closed source and not usable as references.

---

## 6. Target design

### 6.1 Pipeline

```
DISCOVERY  (ApplicationDiscoveryService)
  registry: HKLM 64-bit, HKLM WOW6432Node, HKCU        AppX: Get-AppxPackage (JSON)
    └─ per entry: UninstallCommandParser → UninstallerKindDetector → MSI state check
    └─ filter: SystemComponent / updates (ParentKeyName, ReleaseType) / NoRemove
    └─ ProtectionService (the ONLY protection rule set)

PLAN + CONFIRM  (VM + UninstallCommandBuilder)
  exact command per app (interactive or quiet) → confirmation dialog → running-app prompt

BATCH  (BatchUninstallService, strictly sequential)
  restore point (optional)
  for each app:
    1. pre-check     registry key still exists?  no → AlreadyRemoved
    2. execute       executor by UninstallerKind
    3. wait          ProcessTreeWatcher (whole tree; msiexec children; InstallLocation stragglers)
    4. interpret     exit-code table → provisional state
    5. verify        InstallStateVerifier (registry key / MSI state / AppX) → UninstallOutcome
    6. leftovers     only for Removed / AlreadyRemoved / BrokenEntryRemoved,
                     scanned against a FRESH list of the other installed apps
  save session

LEFTOVER REVIEW  (VM + LeftoverCleanupService)
  per item: protection re-check → needs-admin check → backup (fail closed) → delete
  → honest per-item result → session updated

HISTORY  (P4)
  sessions, outcomes, logs, registry restore (reg import)
```

### 6.2 Model changes

**`InstalledApplication`** ([InstalledApplication.cs](AxePower.Core/Models/Uninstaller/InstalledApplication.cs)): add

```csharp
/// <summary>Uninstall key in the 64-bit view, WOW6432Node spelled out,
/// e.g. HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}.</summary>
public string RegistryKeyPath { get; set; } = string.Empty;
/// <summary>Other uninstall keys merged into this entry (double registrations, see §6.5 rule 11).</summary>
public List<string> RelatedKeyPaths { get; set; } = new();
public UninstallerKind UninstallerKind { get; set; } = UninstallerKind.Unknown;
/// <summary>Resolved full path of the uninstaller executable (empty when not resolvable).</summary>
public string UninstallerPath { get; set; } = string.Empty;
public bool IsUpdate { get; set; }                  // ParentKeyName or ReleaseType Update/Hotfix
public string ParentKeyName { get; set; } = string.Empty;
public int MsiProductState { get; set; } = int.MinValue; // raw MsiQueryProductState, MinValue = not checked
```

- **Registry paths:** store all registry paths in the **64-bit view**, with `WOW6432Node` written explicitly for 32-bit entries. Every consumer (verification, `reg export /reg:64`, deletion) then opens `RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)` and nothing depends on process bitness.
- **`ApplicationType`** stays, but only drives the UI filter chips. It is derived from the kind:
  - `Msi` → `MSI`
  - `StoreApp` → `AppX`
  - `BrokenEntry` → `Orphaned` (shown as **"Broken"** in the UI)
  - everything else → `Win32`
  - `WinGet` and `MSIX` are no longer produced (see [decision 1](#10-open-decisions)).

**New enums** (in `AxePower.Core/Models/Uninstaller/`):

```csharp
public enum UninstallerKind
{
    Unknown,        // exe we can't identify, run as-is
    Msi,            // msiexec /x {ProductCode}
    BurnBundle,     // WiX bundle in Package Cache: <exe> /uninstall
    InnoSetup,      // unins###.exe + .dat
    Nsis,           // exe contains "Nullsoft"
    InstallShield,  // InstallShield Installation Information\{…}
    Squirrel,       // Update.exe --uninstall
    PowerShell,     // powershell.exe / .ps1
    StoreApp,       // AppX / MSIX
    BrokenEntry,    // uninstaller missing or MSI product not registered
}

public enum UninstallOutcome
{
    Removed,              // verified gone
    AlreadyRemoved,       // key already missing before we ran, or MSI 1605 + verified gone
    RemovedPendingReboot, // 3010/1641 and verification can't confirm until restart
    StillInstalled,       // uninstaller finished but the app is still registered
    Cancelled,            // user cancelled the uninstaller or refused UAC
    Failed,               // error; see ErrorMessage
    BrokenEntryRemoved,   // broken entry deleted (after backup) at the user's request
    Skipped,              // user chose Skip (e.g. app running)
    NeedsAdmin,           // action needs elevation and we're not elevated
    DryRun,
}
```

**`UninstallResult`** ([UninstallResult.cs](AxePower.Core/Models/Uninstaller/UninstallResult.cs)):
- Add `Outcome`, `UninstallerKind`, `WasQuiet` and `LogPath`.
- Make `Success` a computed property: `Outcome is Removed or AlreadyRemoved or RemovedPendingReboot or BrokenEntryRemoved`.
- `UninstallSession.SuccessCount` / `FailedCount` / `SkippedCount` follow from that. Add `CancelledCount` and `StillInstalledCount`.

**`LeftoverItem`** ([LeftoverItem.cs](AxePower.Core/Models/Uninstaller/LeftoverItem.cs)):
- Add item types `Shortcut`, `ScheduledTask`, `FirewallRule` (informational until P5), and `ServiceInfo` (informational, never deletable).
- Add `List<ConfidenceRecord> Evidence`, `bool RequiresElevation`, `string? BlockedReason`, and `string ValueName` (already present).

**New `CleanupItemResult`:** `Deleted`, `AlreadyGone`, `Failed(reason)`, `SkippedNeedsAdmin`, `SkippedBackupFailed`, `Blocked(reason)`. `CleanupResult` ([CleanupResult.cs](AxePower.Core/Models/Uninstaller/CleanupResult.cs)) sums these.

### 6.3 Command parsing

New `UninstallCommandParser` (Core, static, pure apart from file-existence checks through an injectable `IFileSystemProbe`):

```csharp
public sealed record UninstallCommand(string Raw, string FileName, string Arguments, bool FileExists);

public static bool TryParse(string raw, IFileSystemProbe fs, out UninstallCommand command);
```

Algorithm:
1. Trim, then `Environment.ExpandEnvironmentVariables`.
2. **Quoted:** the file name runs up to the closing quote; the rest is the arguments.
3. **Unquoted:** split on spaces and try growing prefixes (`C:\Program`, `C:\Program Files\…\uninstall.exe`, …). Take the **first prefix that is an existing file**, also trying the prefix with `.exe` appended. This handles unquoted paths with spaces, such as Google Drive's `C:\Program Files\Google\Drive File Stream\131.0.2.0\uninstall.exe`.
4. **Bare names** (no directory, e.g. `MsiExec.exe`, `msiexec`, `RunDll32.exe`): resolve through `Environment.SystemDirectory`, then the Windows folder, then `PATH`, appending `.exe` when there's no extension. **Never** call `File.Exists` on a relative path.
5. **`rundll32`:** also resolve the DLL (the first argument up to the comma). A missing DLL means a broken entry.
6. If nothing resolves: `FileName` is the best guess (the first `.exe`-terminated prefix, or the first token) and `FileExists = false`.

Used by discovery (broken-entry check and kind detection), `UninstallCommandBuilder`, and `Win32UninstallExecutor`. It replaces both `ExtractExecutablePath` and `Win32UninstallExecutor.ParseCommand`.

### 6.4 Uninstaller kind detection

New `UninstallerKindDetector` (a port of BCU's `GetUninstallerType`, extended). It takes the parsed command and the key's values, and evaluates **in this order**:

| # | Rule | Kind |
|---|---|---|
| 1 | Command resolves to `msiexec.exe`, **or** `WindowsInstaller = 1` | `Msi` |
| 2 | Path contains `\Package Cache\{` + `}\` + `.exe`, **or** the key has `BundleCachePath` / `BundleProviderKey` | `BurnBundle` |
| 3 | Command contains `InstallShield Installation Information\{` | `InstallShield` |
| 4 | Command contains `powershell.exe` or `.ps1` | `PowerShell` |
| 5 | File name is `Update.exe` **and** the arguments contain `--uninstall` | `Squirrel` |
| 6 | File name matches `^unins\d{3}\.exe$` **and** `unins###.dat` exists next to it (also a hint: key name ends in `_is1` or has `Inno Setup: App Path`) | `InnoSetup` |
| 7 | The exe contains the ASCII text `Nullsoft` (scan at most the first 16 MB, off the UI thread, cached by path + size + mtime) | `Nsis` |
| 8 | Resolved file missing, or `Msi` with `MsiQueryProductState == -1` (unknown product) | `BrokenEntry` |
| 9 | Otherwise | `Unknown` (run as-is) |

Rule 8 always wins when the uninstaller is missing. Check it first in code and keep the order above for everything else.

**MSI ProductCode:** extract it from the command with `/[XxIi]\s*(\{[0-9A-Fa-f-]{36}\})`. Fall back to the key name **only** when `WindowsInstaller = 1`. Entries registered as `MsiExec.exe /I{GUID}` (modify) still uninstall with `/x`.

**MSI state:** P/Invoke `msi.dll!MsiQueryProductState` (`CharSet.Unicode`):

| Value | Meaning | Treat as |
|---|---|---|
| 5 | `INSTALLSTATE_DEFAULT` | installed |
| 1 | `INSTALLSTATE_ADVERTISED` | installed (advertised) |
| 2 | `INSTALLSTATE_ABSENT` | installed for another user; **not** broken |
| −1 | `INSTALLSTATE_UNKNOWN` | not registered, so a broken entry (or, after uninstall, removed) |

Put it behind `IMsiProductStateReader` so tests can fake it.

### 6.5 Discovery rules

Refactor `ReadApplicationFromKey` into a **pure** `UninstallEntryClassifier.Classify(UninstallEntryValues values, string keyName, string registryKeyPath, IFileSystemProbe fs, IMsiProductStateReader msi)`. `UninstallEntryValues` is a plain record of the values from [Appendix A](#appendix-a-uninstall-registry-values). The registry walk only reads values and calls the classifier, so every rule is unit-testable with fixtures.

Rules:
1. No `DisplayName` → skip.
2. `SystemComponent = 1` → hidden unless the **Show system components** setting is on. Drop the current "keep GUID-named ones" exception.
3. `ParentKeyName` set, or `ReleaseType` contains `Update` / `Hotfix` / `Security Update` → `IsUpdate`, hidden unless **Show updates** is on.
4. `NoRemove = 1` → `CanUninstall = false`.
5. Kind detection ([§6.4](#64-uninstaller-kind-detection)). `ProductCode` is set only for `Msi`.
6. `InstallLocation` empty → derive it (BCU's `InstallLocationGenerator` idea) from the uninstaller's folder, when that folder isn't System32, the Windows folder, `Package Cache`, `C:\Windows\Installer` or a known root. Otherwise try `DisplayIcon`'s folder, with the same checks.
7. Protection comes **only** from the injected `IProtectionService`. Delete the private `IsProtectedApplication` ([L496-512](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L496-L512)). Keep a parameterless constructor that defaults to `new ProtectionService()` so existing tests compile.
8. **AppX** (P2.5):
   - Use `Get-AppxPackage -PackageTypeFilter Main | Select … | ConvertTo-Json -Compress` and parse with `System.Text.Json`.
   - Raise the timeout to 30 s.
   - Kind is `StoreApp`.
   - Display name: read the manifest `DisplayName` when it's a plain string; fall back to the formatted package name when it's an `ms-resource:` string.
9. **WinGet:** delete `DiscoverWingetPackagesAsync`, `FindWinget` and the WinGet filter chip. Remove `WingetUninstallExecutor` from the factory and DI (see [decision 1](#10-open-decisions)).
10. **Deduplication:** keep the current priority order, but drop the WinGet ID step.
11. **Double registrations:** some products register two ARP entries for one install. On the audit PC, RustDesk has a `RustDesk` key *and* the MSI key `{CD24A421-…}`. When a name + publisher + version merge joins entries from **different keys**:
    - keep all their `RegistryKeyPath`s (`RelatedKeyPaths`);
    - run the `Msi` kind's uninstaller if there is one, otherwise the first entry's;
    - verify that **every** key is gone.

    A leftover sibling key whose uninstaller no longer exists is then offered as an `IsUninstallerRegistryKey` leftover. Add this pair to the classifier fixtures.

### 6.6 Running an uninstaller

**Command builder.** New `UninstallCommandBuilder.Build(app, preferQuiet)` returns an `UninstallPlan { Kind, FileName, Arguments, IsQuiet, Description }`. The confirmation dialog shows `Description`, dry run prints the command, and the executors run it. There is one source of truth for "what will run".

| Kind | Interactive (default) | Quiet (opt-in `PreferQuietUninstall`) |
|---|---|---|
| Registry `QuietUninstallString` present | — | use it as-is (parsed) |
| `Msi` | `msiexec.exe /x {PC} /qb /norestart /l*v "<log>"` | `msiexec.exe /x {PC} /qn /norestart /l*v "<log>"` |
| `BurnBundle` | `"<exe>" /uninstall` | `"<exe>" /uninstall /quiet /norestart` |
| `InnoSetup` | `"<exe>"` | `"<exe>" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |
| `Nsis` | `"<exe>" <args>` | `"<exe>" /S` (see [decision 3](#10-open-decisions)) |
| `Squirrel` | `"<Update.exe>" --uninstall` | `"<Update.exe>" --uninstall -s` (verify manually on Discord) |
| `InstallShield`, `PowerShell`, `Unknown` | registry `UninstallString` as-is | not generated; stays interactive |
| `StoreApp` | `Remove-AppxPackage -Package '<PackageFullName>'` | same |
| `BrokenEntry` | no command; offer **Remove broken entry** | same |

Never append switches to `Unknown` exes. Never pass `_?=` to NSIS: it stops the temp copy but also stops the uninstaller from deleting its own folder.

**Launch:**
- `UseShellExecute = true` so the uninstaller's own manifest can trigger UAC.
- `Win32Exception` with `NativeErrorCode == 1223` gives `Cancelled` ("Administrator permission was refused").
- `Verb = "runas"` is never needed.
- Delete the unused `RequiresElevation` flag.

**`ProcessTreeWatcher`** (new, Core, adapted from BCU's `BulkUninstallEntry` loop):

```csharp
public sealed record ProcessWatchOptions(
    bool IncludeMsiexecChildren, string? InstallLocation, TimeSpan PollInterval, TimeSpan GracePeriod);

public Task<ProcessTreeResult> WaitAsync(int rootPid, ProcessWatchOptions options,
                                          CancellationToken ct);
```

1. Take the first **Toolhelp snapshot** (`CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS)` → pid, parent pid, exe name) immediately after launch, then one every `PollInterval` (1 s). Toolhelp sees elevated processes too, which `Process.MainModule` can't.
2. Keep two sets:
   - **ever-watched:** every pid we have ever tracked, with its creation time;
   - **alive:** the ever-watched pids that still appear in the snapshot.

   Seed both with the root pid. Record a launch timestamp taken just before `Process.Start` as the root's lower-bound creation time.
3. Any process whose **parent pid is in ever-watched** and which was **created after that parent** is a descendant, even if the parent has already exited. A process's parent-pid field survives the parent's exit, so the NSIS temp copy is still caught when the first process is gone before our first snapshot.
   - Read creation times with `GetProcessTimes` via `PROCESS_QUERY_LIMITED_INFORMATION`, which works across integrity levels. The creation-time check guards against PID reuse. If a time can't be read, accept the process.
   - With `IncludeMsiexecChildren`, also add the children of `msiexec.exe` processes (custom actions).
4. When **alive** is empty, wait `GracePeriod` (2 s) and rescan once for stragglers:
   - processes whose image path (`QueryFullProcessImageName`) is under `InstallLocation` or under `%TEMP%`;
   - processes named like `Au_.exe`, `Un_A.exe` or `_iu*.tmp`.

   Add any found and keep waiting. BCU does the same with `FindAndAddProcessesToWatch`.
5. Return when **alive** stays empty, or on cancellation. On cancellation, report `WaitAbandoned`. **Never** kill the uninstaller unless the user explicitly chooses **Stop uninstaller** (quiet mode, P2.2).

**Exit-code table** (`ExitCodeInterpreter`). Verification always runs afterwards; the exit code only picks the wording when the app is still installed.

| Kind | Code | Meaning | Provisional |
|---|---|---|---|
| any | 0 | finished | verify |
| Msi / Burn | 3010, 1641 | success, restart needed | verify → `Removed`, or `RemovedPendingReboot` if still registered |
| Msi | 1605 | unknown product | verify → `AlreadyRemoved` |
| Msi / Burn | 1602 | user cancelled | `Cancelled` |
| Msi | 1618 | another installation in progress | wait 30 s, retry at most twice, then `Failed` |
| Msi | 1603 | fatal error | `Failed` (link the log) |
| Nsis | 1 | cancelled by user | `Cancelled` |
| Nsis | 2 | aborted by script | `Cancelled` |
| Nsis | 1627 | treated as OK by BCU | verify |
| any | −1073741510 (`0xC000013A`) | terminated (Ctrl+C / window closed) | `Cancelled` |
| any | other non-zero | unknown | verify; if still installed → `Failed` |

`MsiUninstallExecutor` ([L67-69](AxePower.Core/Services/Uninstaller/MsiUninstallExecutor.cs#L67-L69)) currently treats only 0/3010/1641 as success.

### 6.7 Verification & outcomes

New `IInstallStateVerifier` with `Task<bool> IsStillInstalledAsync(InstalledApplication app, CancellationToken ct)`:
- **Registry entries:** does `RegistryKeyPath` still open in the 64-bit view? Poll for up to **10 s** after the process tree exits, because some uninstallers remove the key last.
- **Msi:** also `MsiQueryProductState(ProductCode)`. Only `-1` counts as removed.
- **StoreApp:** `Get-AppxPackage | Where-Object PackageFullName -eq '<validated pfn>'` returns nothing. The PFN is already validated by `IsValidPackageName`.

Batch rule:
- `Removed` requires "not still installed".
- If the tree exited cleanly but the app is still installed:
  - `StillInstalled`, with the message "The uninstaller closed but <app> is still installed. It may have been cancelled."
  - If the uninstaller exe has since disappeared, offer **Remove broken entry**.

**Pre-check** (BCU does this too): if the key is already gone before we start, the outcome is `AlreadyRemoved` and nothing runs.

### 6.8 Broken entries

A `BrokenEntry` has a registry entry but no working uninstaller.
- **UI:** the **Broken** chip; the action button reads **Remove broken entry**, never "Uninstall".
- **Flow:**
  1. Confirm: "Only the listing is removed; files are not touched."
  2. Back it up with `reg export` (fail closed).
  3. Delete the key. HKLM needs admin; without it the outcome is `NeedsAdmin`.
  4. Outcome `BrokenEntryRemoved`.
  5. Optionally offer **Find leftovers**, with **nothing pre-ticked**.
- This replaces the current "ForcedOrphan = success" path ([BatchUninstallService.cs:93-106](AxePower.Core/Services/Uninstaller/BatchUninstallService.cs#L93-L106)). It is implemented as `BrokenEntryExecutor : IUninstallExecutor`.

### 6.9 Leftover scanner

The rebuild ports BCU's evidence model. `LeftoverScannerService` becomes an orchestrator over `ILeftoverFinder` implementations. Each finder returns items with `Evidence` records.

**Inputs:**
- the removed app (with trimmed names and candidate names);
- a **fresh** registry-only discovery snapshot of the other installed apps, taken after verification (used for the penalties);
- the settings.

**Name normalisation** (`AppNameNormalizer`):
- `DisplayNameTrimmed` (BCU `StripStringFromVersionNumber`): repeatedly drop a trailing `(…)` whose `(` is past index 4, then trailing digits and `. , - _`. We add: trailing ` x64`, ` x86`, ` 64-bit`, ` 32-bit`, ` v`, and locale tags like `en-US`.
- `PublisherTrimmed`: drop `(R)` and `(TM)`, then trailing `corp`, `corporation`, `limited`, `inc`, `incorporated` (BCU's list), plus `llc`, `ltd`, `gmbh`, `co`, `s.a.` and trailing punctuation.
- **Candidate names:**
  - `DisplayNameTrimmed`;
  - the `InstallLocation` folder name;
  - the uninstaller's parent folder name (unless generic);
  - the registry key name (unless it's a GUID or ends in `_is1`);
  - the names of the main exes in the install folder (P3.3b).

**Fuzzy match** (port of BCU's `MatchStringToProductName` plus `Sift4.SimplestDistance`, maxOffset 1):
- Lowercase and treat `_` as a space. Returns −1 for no match, 0–1 for a perfect match, 2 when one name contains the other, or the distance when it's under a third of the shorter length.
- Strings of 4 characters or fewer never match.
- If the product name contains the publisher, retry the match without the publisher.

**Evidence records** (BCU `ConfidenceRecords`, values unchanged):

| Record | Points | When |
|---|---|---|
| `IsUninstallerRegistryKey` | +20 | an uninstall key whose uninstaller or install location points into the removed app's folder |
| `ExplicitConnection` | +4 | the item **points into** the install folder or uninstaller folder (shortcut target, Run value, task action, CLSID server, App Paths…) |
| `CompanyNameMatch` | +4 | the parent folder matches the publisher |
| `AllSubdirsMatched` | +4 | every subfolder matched |
| `IsEmptyFolder` | +4 | the folder is empty |
| `ProductNamePerfectMatch` | +2 | match result 0–1 |
| `FolderHasNoSubdirectories` | +2 | |
| depth | 2 − 2×level | level 0 = directly in the scanned root |
| `FilesArePresent` | 0 | |
| `DirectlyInsideKnownFolder` | −1 | the item sits directly in a known folder root |
| `CompanyNameDidNotMatch` | −2 | |
| `ItemNameEqualsCompanyName` | −2 | the item is named after the publisher (the "Adobe" folder for "Adobe AIR") |
| `ProductNameDodgyMatch` | −2 | match result ≥ 2 |
| `ManyFilesArePresent` | −2 | |
| `UsedBySimilarNamedApp` | −2 | another installed app has a similar name (`TestForSimilarNames`) |
| `QuestionableDirectoryName` | −3 | generic names (`bin`, `data`, `common`, `temp`, `cache`…) |
| `ExecutablesArePresent` | −4 | |
| `ProgramNameIsStillUsed` | −4 | another installed app matches the name |
| `PublisherIsStillUsed` | −4 | another installed app has the same publisher |
| `DirectoryStillUsed` | −7 | another installed app's `InstallLocation` is inside this folder (also **blocked** by protection) |
| `IsStoreApp` | −10 | Store-app data (Windows cleans these up itself) |

**Score mapping:** `Score = clamp(50 + 10 × Σpoints, 0, 100)`. The existing labels are kept (≥95 Very High, ≥80 High, ≥60 Medium, ≥40 Low, otherwise Very Low). `ConfidenceReason` lists the records.

**Pre-tick only when all of these hold:**
- the item has `ExplicitConnection` or `IsUninstallerRegistryKey`;
- Σ ≥ 4;
- it has none of `DirectoryStillUsed`, `ProgramNameIsStillUsed` or `PublisherIsStillUsed`;
- it isn't protected or blocked;
- it doesn't need admin, or we are elevated.

Name-only matches are never pre-ticked.

**Finders, by phase:**

| Finder | Looks at | Evidence | Phase |
|---|---|---|---|
| `InstallLocationFinder` | `InstallLocation` and the derived location, if it still exists | ExplicitConnection | P3.3a |
| `CommonFolderFinder` | depth 0–1 (`<Name>`, `<Publisher>\<Name>`) under Roaming, Local and LocalLow AppData, ProgramData, both Program Files, both Common Files | fuzzy name, publisher, penalties | P3.3a |
| `SoftwareKeyFinder` | children of `HKCU\Software`, `HKLM\SOFTWARE`, `HKLM\SOFTWARE\WOW6432Node`, plus one level under a matching publisher key | fuzzy name, publisher | P3.3a |
| `UninstallKeyFinder` | other uninstall entries whose `InstallLocation` / `UninstallString` point into the removed folder **and** whose uninstaller is gone | IsUninstallerRegistryKey | P3.3a |
| `StartupFinder` | values in `HKCU` / `HKLM` / WOW `…\CurrentVersion\Run` and `RunOnce`; user and common Startup folders | ExplicitConnection | P3.3a |
| `ShortcutFinder` | `.lnk` files in user and common Start Menu\Programs and Desktop; targets resolved via `IShellLinkW` (or `WScript.Shell`) | ExplicitConnection | P3.3a |
| `AppPathsFinder` | `HKLM` / `HKCU` `…\CurrentVersion\App Paths\<exe>` with a default value in the folder | ExplicitConnection | P3.3b |
| `AppCompatFinder` | `HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers` and `…\Compatibility Assistant\Store` values named by paths in the folder | ExplicitConnection | P3.3b |
| `MuiCacheFinder` | `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache` values for exes in the folder | ExplicitConnection | P3.3b |
| `ComFinder` | `HKCU\Software\Classes\CLSID\{…}` (and HKLM when elevated) whose `InprocServer32` / `LocalServer32` all point into the folder | ExplicitConnection | P3.3b |
| `ScheduledTaskFinder` | Task Scheduler COM (`Schedule.Service`), recursive; `Exec` actions whose path is in the folder | ExplicitConnection | P3.3b |
| `ServiceInfoFinder` | services whose `ImagePath` is in the folder | **informational only**, never deletable | P3.3b |
| `FirewallRuleFinder` | firewall rules whose program is in the folder | informational until P5 | P3.3b |

Do **not** port: prefetch (needs admin and is harmless), UserAssist (ROT13, a privacy trace with no benefit), event-log sources, tracing keys, or the `Installer` folder scanner.

**Deleters** (`LeftoverCleanupService`, which replaces the switch in [UninstallerViewModel.cs:341-379](AxePower.App/ViewModels/UninstallerViewModel.cs#L341-L379)):

| Item type | Backup | Delete | Restore |
|---|---|---|---|
| RegistryKey | `reg export` → `.reg` | `DeleteSubKeyTree` (64-bit view) | `reg import` |
| RegistryValue / StartupEntry (registry) | typed JSON (kind + data) | `DeleteValue` | `SetValue` with the stored kind |
| Folder / File / Shortcut / Startup-folder item | Recycle Bin (existing) | `FileCleanupService` | from the Recycle Bin |
| ScheduledTask | save `task.Xml` | `folder.DeleteTask(name, 0)` | `RegisterTask(path, xml, …)` |
| ServiceInfo, FirewallRule | — | never (FirewallRule allowed in P5) | — |

Every deleter returns a `CleanupItemResult` ([§6.2](#62-model-changes)).

### 6.10 Protection rules

A single `ProtectionService` holds all rules. The new API is `string? GetBlockReason(string path, IReadOnlyCollection<string> otherInstallLocations)`; `IsPathProtected(path)` becomes `GetBlockReason(path, []) is not null`.

**Filesystem: blocked** (the path equals one of these, or is an **ancestor** of one):
- every drive root (`Path.GetPathRoot(p) == p`);
- `Environment.SpecialFolder` values: Windows, System, SystemX86, ProgramFiles, ProgramFilesX86, CommonProgramFiles(X86), CommonApplicationData, UserProfile, ApplicationData, LocalApplicationData, Desktop, CommonDesktopDirectory, MyDocuments, MyMusic, MyPictures, MyVideos, StartMenu, CommonStartMenu, Programs, CommonPrograms, Startup, CommonStartup, Templates, Fonts;
- known folders not in `SpecialFolder`: Downloads, `%LOCALAPPDATA%Low`, `%USERPROFILE%\OneDrive*`, `%TEMP%` root, `C:\Users`, `C:\ProgramData\Package Cache`, `C:\Windows\Installer`.

**Filesystem: blocked, including everything inside:**
- Windows, WindowsApps, `Program Files\Windows NT`, `Program Files\Windows Defender*`, `Program Files\Common Files\microsoft shared`;
- `%LOCALAPPDATA%\Packages` root;
- the `Microsoft` folders directly under Roaming, Local, ProgramData and both Program Files.

**Filesystem: blocked by other apps:**
- any path that **equals or contains** another installed app's `InstallLocation`, based on the fresh snapshot ([§6.9](#69-leftover-scanner));
- a folder named after the publisher (depth 0, `ItemNameEqualsCompanyName`) is selectable only if no other installed app has that publisher.

**Registry: blocked:**
- hive roots and paths with fewer than 2 segments (existing rule);
- `HKLM\SOFTWARE\Microsoft` and `HKLM\SOFTWARE\WOW6432Node\Microsoft` roots and their direct children (keys *deeper* inside, such as `…\Microsoft\Windows\CurrentVersion\Run` **values**, remain possible through the value deleters);
- `HKCU\Software\Microsoft` and `HKCU\Software\Classes` roots;
- `HKLM\SOFTWARE\Classes` (keep the whole prefix blocked until `ComFinder` in P3.3b; then allow only `CLSID\{guid}` keys with ExplicitConnection, elevated);
- `HKLM\SYSTEM`, `HKLM\SAM`, `HKLM\SECURITY`;
- `HKLM\SOFTWARE\Policies`, `HKCU\Software\Policies`;
- **all Windows Installer data:** `…\CurrentVersion\Installer` (both views) and `HKCR\Installer`;
- the `…\CurrentVersion\Uninstall` root itself (its subkeys are allowed).

**Needs admin** (`RequiresElevation`): anything under `HKLM`, `C:\Program Files*` or `C:\ProgramData`, tasks that aren't the user's own, and HKLM Run values. These are shown with a lock and aren't selectable unless elevated.

### 6.11 Backup & restore

- **Location:** `%LOCALAPPDATA%\AxePower\UninstallerHistory\{sessionId}\`. Move `sessions.json` there too, migrating once from `%APPDATA%` ([decision 6](#10-open-decisions)). This matches the repo rule for app data.
- **Files:**
  - `NNN.reg` — one per exported key, from `reg.exe export "<key>" "<file>" /y /reg:64`;
  - `values.json` — typed value backups;
  - `tasks\NNN.xml` — scheduled-task backups;
  - `manifest.json` — every item with its path, type, backup file and result, written atomically (temp file + `File.Move(overwrite)`).
- **Session ID:** the batch session's ID. A standalone "Scan Leftovers" run creates its own session.
- **Fail closed:** if `reg export` exits non-zero or the file is missing or empty, the item becomes `SkippedBackupFailed` and nothing is deleted.
- **`BackupRegistryChanges = false`:** allowed only for HKCU. HKLM always backs up.
- **Restore** (P4): `reg import "<file>" /reg:64` in reverse order, then values, then tasks. HKLM restore needs admin, so show the relaunch banner.
- `reg.exe` is always `%SystemRoot%\System32\reg.exe`, run with `ArgumentList` and `CreateNoWindow`.

### 6.12 Elevation

- **Uninstallers:** no change; they elevate themselves.
- **Leftovers and broken entries needing admin:**
  - While not elevated, show a banner: "N items need administrator rights. **Restart as administrator**". It calls `ElevationService.TryRelaunchElevated()` and shuts down on `Started`, matching the existing startup behaviour.
  - Before relaunching, save the pending leftover review into the session so it reopens after restart ("Resume leftover review" in History, P4).
- **Optional P5.1:** a one-shot elevated helper `AxePower.exe --uninstaller-cleanup "<plan.json>"`. It re-validates every item against `ProtectionService` (the plan is never trusted blindly), runs backup → delete, writes `result.json` and exits. That means one UAC prompt with no app restart.

### 6.13 UI & view model

[UninstallerViewModel.cs](AxePower.App/ViewModels/UninstallerViewModel.cs), [UninstallerView.xaml](AxePower.App/Views/UninstallerView.xaml):

- **Cancellation:** separate `_discoveryCts` and `_batchCts` (and `_scanCts`). Add `IsBusy`; Refresh, Scan and Uninstall are disabled while it's true.
- **CancelCommand:**
  - It stops the **queue** after the current app.
  - For the current app it asks: "The uninstaller is still running. Stop waiting?" A **Stop uninstaller** option appears only for quiet uninstalls. It never kills silently.
- **UninstallThisCommand** (the details panel): works on `SelectedApplication`, whether or not anything is checked.
- **Confirmation dialog:** before any batch, list each app with its `UninstallPlan.Description` (e.g. "RustDesk: Windows Installer (msiexec), interactive"), whether a restore point will be made, and any protected apps that will be **skipped**. Protected apps are skipped individually instead of aborting the batch (fixes D12).
- **Running apps** (fixes D15): before the batch, list running apps (via the Toolhelp and `QueryFullProcessImageName` version of `ProcessDetectionService`). Options per app: **Close app** (`CloseMainWindow`, wait 10 s, then offer **Force close**), **Skip app** or **Continue anyway**.
- **Results summary:** after the batch, one row per app with its outcome icon, the message, and **Open log** for MSI. The toast shows counts by outcome, not "Uninstalled: X".
- **Leftover review:**
  - grouped by app;
  - columns: checkbox, type icon, path, size, confidence;
  - evidence records in a tooltip or expander;
  - badges: **Needs admin** (lock), **Blocked: <reason>** (not checkable), **Shared**;
  - footer: "Backups: <open folder>";
  - after cleaning, a result list with per-item status.
- **Filter chips:** All, Win32, MSI, Store Apps, **Broken**, Protected. Remove WinGet.
- **Help text** ([MainViewModel.cs:620](AxePower.App/ViewModels/MainViewModel.cs#L620)): make it true. Once P1.9 ships, say "Registry keys are exported to .reg backups before deletion; files go to the Recycle Bin".
- **Prompts in tests:** introduce `IUninstallerPrompts` in `AxePower.App/Services` (confirm batch, running apps, stop waiting, remove broken entry, clean leftovers). The implementation uses `MessageBox`, matching [MainWindow.xaml.cs:283](AxePower.App/MainWindow.xaml.cs#L283); tests use a fake. Core gets `IUninstallInteraction` (running app, stalled uninstaller) as a callback passed into the batch.

### 6.14 Settings

Add `public UninstallerSettings Uninstaller { get; set; } = new();` to [AppSettings.cs](AxePower.Core/Models/AppSettings.cs). It is persisted atomically by the existing [SettingsService.cs](AxePower.Core/Services/SettingsService.cs). The VM reads it at construction and saves it on change.

| Setting | Default | Notes |
|---|---|---|
| `DefaultScanMode` | Moderate | Safe = P3.3a finders with explicit evidence only; Moderate = all P3.3a; Advanced = plus P3.3b |
| `PreferQuietUninstall` | **false** | new |
| `CreateSystemRestorePoint` | true | |
| `BackupRegistryChanges` | true | HKLM is always backed up |
| `MoveToRecycleBin` | true | |
| `AutoScanLeftovers` | true | only after a verified removal |
| `ShowProtectedApplications` | false | |
| `ShowOrphanedApplications` | true | shown as "Show broken entries" |
| `ShowSystemComponents` | false | new |
| `ShowUpdates` | false | new |
| `QuietStallMinutes` | 10 | new; P2.2 |
| `DryRunMode` | false | |
| `AllowAdvancedScan` | true | |
| `ExcludedRegistryPaths` / `ExcludedFilePaths` | [] | add simple list editors to the Settings panel |

---

## 7. Work plan

Size: **S** ≈ under half a day · **M** ≈ one to two days · **L** ≈ three days or more. Every task ends with `dotnet build` and `dotnet test`. Keep DI registrations in [App.xaml.cs](AxePower.App/App.xaml.cs) and [DependencyInjectionTests.cs](AxePower.Tests/Uninstaller/DependencyInjectionTests.cs) in sync (per WORK_LOG).

### Phase 0: Prep

- [ ] **P0.1 Quarantine the live test** (S) · closes D17
  - [LiveEchoMusicUninstallTest.cs](AxePower.Tests/Uninstaller/LiveEchoMusicUninstallTest.cs): `[Fact(Skip = "Live test: uninstalls a real app. Run manually only.")]` and `[Trait("Category", "Live")]`.
  - **Done when:** `dotnet test` runs no uninstaller.
- [ ] **P0.2 Attribution scaffolding** (S)
  - Add `THIRD_PARTY_NOTICES.md` at the repo root with BCU's Apache-2.0 notice (copy BCU's `NOTICE` and link the license). Put the header from [§5](#5-what-we-take-from-bcu) on every ported file.
  - **Done when:** the notice file exists and is linked from README.
- [ ] **P0.3 Capture fixtures** (S)
  - Run the fixture script ([Appendix C](#appendix-c-scripts)) to write `AxePower.Tests/Uninstaller/Fixtures/arp-entries.json` from the audit PC, with the user name redacted.
  - **Done when:** the fixture includes the RustDesk, ONLYOFFICE, Google Drive, Samsung USB Driver and Package Cache entries.

### Phase 1: Correctness & honesty

*Goal: every uninstall runs the right uninstaller, waits for it, verifies, and reports the truth. Nothing unsafe is offered.*

- [ ] **P1.1 `UninstallCommandParser` + `IFileSystemProbe`** (M) · D1
  - New `AxePower.Core/Services/Uninstaller/UninstallCommandParser.cs`, as in [§6.3](#63-command-parsing).
  - Replace `ExtractExecutablePath` ([L545-569](AxePower.Core/Services/Uninstaller/ApplicationDiscoveryService.cs#L545-L569)) and `Win32UninstallExecutor.ParseCommand` ([L94-126](AxePower.Core/Services/Uninstaller/Win32UninstallExecutor.cs#L94-L126)).
  - **Done when:** the parser tests in [§8](#8-automated-tests) pass, including bare `MsiExec.exe`, unquoted paths with spaces, `%ProgramFiles%` and `rundll32`.
- [ ] **P1.2 `UninstallerKindDetector` + `IMsiProductStateReader`** (M) · D1, D2
  - Rules from [§6.4](#64-uninstaller-kind-detection); ProductCode extraction; `MsiQueryProductState` P/Invoke.
  - **Done when:** on the fixtures, RustDesk and ONLYOFFICE → `Msi`; Google Drive and Samsung USB Driver → `Unknown` (exe); VC++ and .NET Package Cache entries → `BurnBundle`; missing exe → `BrokenEntry`.
- [ ] **P1.3 Discovery refactor** (M) · D1, D14
  - Pure `UninstallEntryClassifier` ([§6.5](#65-discovery-rules)).
  - New model fields ([§6.2](#62-model-changes)) with `RegistryKeyPath` in the 64-bit view.
  - SystemComponent, update and NoRemove rules.
  - Derived `InstallLocation`.
  - Inject `IProtectionService` and delete the private protection check.
  - Remove WinGet discovery, the executor registration and the chip ([decision 1](#10-open-decisions)).
  - Fix the AppX `\r` bug now (`line.TrimEnd('\r')`); the JSON rewrite comes in P2.5.
  - **Done when:** the [Appendix C](#appendix-c-scripts) comparison on the audit PC shows **Broken** holding only entries whose uninstaller really is missing, and the **MSI** chip listing the MSI apps.
- [ ] **P1.4 `UninstallOutcome` + `IInstallStateVerifier`** (M) · D4
  - Enum and result changes ([§6.2](#62-model-changes)); verifier ([§6.7](#67-verification--outcomes)).
  - Update `UninstallSession` counts and the history serialization (keep old JSON loading: a missing `Outcome` maps from the old `Success`).
  - **Done when:** the batch tests with a fake verifier produce `StillInstalled` when the key remains after exit code 0.
- [ ] **P1.5 `ProcessTreeWatcher` + process detection fix** (M) · D3, D15
  - Watcher as in [§6.6](#66-running-an-uninstaller).
  - Switch `ProcessDetectionService` from `MainModule` to Toolhelp + `QueryFullProcessImageName`.
  - Use the watcher in `Win32UninstallExecutor` and `MsiUninstallExecutor` (with `IncludeMsiexecChildren`).
  - **Done when:** the watcher test (a `cmd.exe` that starts a detached `ping` grandchild) waits for the grandchild; a manual NSIS uninstall (Notepad++) waits until the wizard closes.
- [ ] **P1.6 Executors by kind + `UninstallCommandBuilder` + exit codes** (M) · D2
  - Factory keyed on `UninstallerKind` ([UninstallExecutorFactory.cs](AxePower.Core/Services/Uninstaller/UninstallExecutorFactory.cs)):
    - `Msi` → MSI executor;
    - `StoreApp` → AppX executor;
    - `BrokenEntry` → new `BrokenEntryExecutor`;
    - everything else → Win32 executor running the `UninstallPlan`.
  - `ExitCodeInterpreter` from [§6.6](#66-running-an-uninstaller); UAC-refused (1223) → `Cancelled`; MSI 1618 retry.
  - Interactive commands only in this phase (quiet mode is P2.1).
  - **Done when:** dry run prints the exact planned command for each kind, and the exit-code tests pass.
- [ ] **P1.7 Batch flow rewrite** (M) · D1, D4
  - [BatchUninstallService.cs](AxePower.Core/Services/Uninstaller/BatchUninstallService.cs): pre-check → execute → wait → interpret → verify → leftover scan only for `Removed` / `AlreadyRemoved` / `BrokenEntryRemoved`, using a fresh snapshot of the other apps.
  - Add an `IUninstallInteraction` parameter.
  - Add the `Verifying` phase to `UninstallPhase`.
  - Remove the ForcedOrphan success path.
  - **Done when:** the batch tests in [§8](#8-automated-tests) pass and no path records success without verification.
- [ ] **P1.8 Protection rails + scanner hot-fixes** (M) · D7, D8
  - `GetBlockReason` with the full rule set from [§6.10](#610-protection-rules), including the other-apps check.
  - Remove the `Installer\UserData` scan and the `LocalLow\{Publisher}` add.
  - Open HKLM with an explicit 64-bit view in the scanner.
  - Blocked items are hidden from the review, or shown disabled with the reason when "Show blocked items" is on.
  - **Done when:** the protection tests pass and a fake app with `InstallLocation = C:\Program Files` produces no selectable folder.
- [ ] **P1.9 Backup + honest cleanup** (M) · D5, D6
  - `LeftoverCleanupService` with `reg export` / typed-value backups, fail-closed behaviour, and per-item `CleanupItemResult` ([§6.9](#69-leftover-scanner), [§6.11](#611-backup--restore)).
  - Fix the value serialization in `RegistryBackupService`.
  - Write `manifest.json`; add `CleanupResult` and `CleanedItems` to the session and re-save it.
  - The VM calls the service instead of its own switch and `DeleteRegistryKey`.
  - **Done when:** the backup round-trip test on `HKCU\Software\AxePower.Tests\{guid}` passes; a failed backup leaves the key in place; the summary counts match the per-item results.
- [ ] **P1.10 View-model & UI fixes** (M) · D10, D11, D12, D15, D16
  - Separate CTSs; `IsBusy`; `CancelCommand`; `UninstallThisCommand`.
  - `IUninstallerPrompts` confirmation and running-app prompt; per-app skipping of protected apps; results summary.
  - Rename the chip to **Broken**; **Remove broken entry** button.
  - Correct the help text.
  - **Done when:** the manual matrix rows M1–M8 pass ([§9](#9-manual-test-matrix)); Refresh during an uninstall is disabled; the details-panel button uninstalls the viewed app.

### Phase 2: Quiet mode, elevation, settings

- [ ] **P2.1 Quiet commands** (S)
  - The quiet column of the [§6.6](#66-running-an-uninstaller) table, behind `PreferQuietUninstall`.
  - The confirmation dialog says "silent" for each app.
  - **Done when:** the builder tests cover every kind in both modes.
- [ ] **P2.2 Quiet-mode stall handling** (S)
  - If a quiet uninstall's tree is still alive after `QuietStallMinutes`, ask **Keep waiting** or **Stop uninstaller** (kills the watched tree), then verify.
  - **Done when:** it's tested with a fake watcher that never ends.
- [ ] **P2.3 Needs-admin handling** (M)
  - Compute `RequiresElevation` per item and per broken entry; lock badges; the relaunch banner (`ElevationService.TryRelaunchElevated`); save the pending review before relaunching.
  - **Done when:** unelevated, HKLM items can't be ticked and the banner relaunches elevated; elevated, they can.
- [ ] **P2.4 Settings panel + persistence** (M) · D13
  - `AppSettings.Uninstaller`; a Settings panel in [UninstallerView.xaml](AxePower.App/Views/UninstallerView.xaml) bound to `ShowSettingsPanel`, with every setting from [§6.14](#614-settings) and the exclusion lists.
  - **Done when:** settings survive a restart and a corrupt settings file falls back to defaults (existing SettingsService behaviour).
- [ ] **P2.5 AppX discovery rewrite** (S) · D14
  - JSON output, `-PackageTypeFilter Main`, 30 s timeout, manifest display names, `StoreApp` verification.
  - **Done when:** Store apps show real names and removal is verified.

### Phase 3: Leftover scanner rebuild

- [ ] **P3.1 `AppNameNormalizer` + `Sift4` port** (S) · D9
  - [§6.9](#69-leftover-scanner) name rules; `MatchStringToProductName`; `TestForSimilarNames`.
  - **Done when:** "Mozilla Firefox (x64 en-US)" trims to "Mozilla Firefox"; "Google LLC" → "Google"; "AppX Extended" doesn't claim "AppX" folders.
- [ ] **P3.2 Evidence model + scoring** (S) · D9
  - `ConfidenceRecord`, the records table, the score mapping and the pre-tick rule. `LeftoverConfidenceService` becomes a thin adapter or is removed.
  - **Done when:** the scoring tests pass and nothing name-only is pre-ticked.
- [ ] **P3.3a Core finders** (L) · D9
  - InstallLocation, CommonFolder, SoftwareKey, UninstallKey, Startup (registry and folders), Shortcut.
  - **Done when:** the M9/M10 manual rows find real leftovers (AppData folders, Start Menu shortcuts, HKCU software keys) with evidence.
- [ ] **P3.3b Advanced finders** (L) · D9
  - App Paths, AppCompat, MuiCache, COM (with the Classes rule change from [§6.10](#610-protection-rules)), scheduled tasks, service info, firewall info.
  - **Done when:** each finder has fixture-based tests and they appear only in Advanced mode.
- [ ] **P3.4 Deleters for the new types** (M)
  - Shortcut, scheduled task (with XML backup), registry value (typed backup).
  - **Done when:** round-trip tests pass for the task and value backups.
- [ ] **P3.5 Leftover review UI** (M)
  - Grouping, evidence display, badges, per-item results, backup-folder link.
  - **Done when:** it's usable by keyboard and at 150% scaling (repo testing rule).

### Phase 4: History & restore

- [ ] **P4.1 History panel** (M)
  - Sessions list (date, apps, outcomes), per-app details, **Open log**, **Open backups folder**, **Restore registry backup** (runs `reg import` in reverse, then values and tasks; needs admin for HKLM), and **Resume leftover review**.
- [ ] **P4.2 Move history to LocalAppData** (S)
  - One-time migration of `%APPDATA%\AxePower\UninstallerHistory` → `%LOCALAPPDATA%\AxePower\UninstallerHistory`.
- [ ] **P4.3 Docs** (S)
  - Rewrite [skillsMD/uninstaller.md](skillsMD/uninstaller.md) to describe the new behaviour.
  - Update README, [AppWebsite/help.html](AppWebsite/help.html) and [features.html](AppWebsite/features.html) claims.
  - Remove the "Known gaps" that no longer apply.

### Phase 5: Optional extras

- [ ] **P5.1 Elevated cleanup helper** (M): see [§6.12](#612-elevation).
- [ ] **P5.2 WinGet via COM API** (M): UniGetUI-style `Microsoft.Management.Deployment`. Use it to show "available via winget" and "upgrade available" rather than as a discovery source.
- [ ] **P5.3 NSIS UI automation** (L): BCU `UninstallerAutomatizer` approach, for NSIS uninstallers that ignore `/S`.
- [ ] **P5.4 Firewall rule deletion** (S): via `INetFwPolicy2` (admin); back up the rule's properties to JSON first.
- [ ] **P5.5 More sources** (M each): Steam games (BCU `SteamFactory`), Windows optional features (`WindowsFeatureFactory`), portable apps found by scanning Program Files for folders with no registry entry (BCU `DirectoryFactory` / `ProgramFilesOrphans`).

---

## 8. Automated tests

Put the tests in `AxePower.Tests/Uninstaller/`. None may uninstall software; system-state changes are limited to `HKCU\Software\AxePower.Tests\{guid}` and temp folders, cleaned up in `finally`.

| Test class | Cases |
|---|---|
| `UninstallCommandParserTests` | `MsiExec.exe /X{CD24A421-5DFB-4EC7-9E1A-7930D58ADA49}` → `%SystemRoot%\System32\msiexec.exe` + `/X{…}`; `msiexec.exe /x {2BC23EF7-7EF5-317D-9967-2AE583956171} AI_UNINSTALLER_CTP=1`; quoted Package Cache path with `  /uninstall`; **unquoted path with spaces** (temp fixture `…\Drive File Stream\1.0\uninstall.exe`); `%ProgramFiles%\X\uninst.exe` expansion; `RunDll32.exe "<missing>.dll",Uninstall` → missing; bare `cmd`/`powershell`; empty or whitespace; a working directory set to a temp folder must not change any result |
| `UninstallerKindDetectorTests` | each row of the [§6.4](#64-uninstaller-kind-detection) table; GUID key + `WindowsInstaller=0` + exe → not `Msi`; `WindowsInstaller=1` + key-name GUID → `Msi`; `unins000.exe` without `.dat` → not Inno; fake file containing `Nullsoft` → `Nsis`; `Update.exe --uninstall` → `Squirrel`; MSI state −1 → `BrokenEntry`, 2 → still `Msi` |
| `UninstallEntryClassifierTests` | driven by `Fixtures/arp-entries.json`: RustDesk/ONLYOFFICE → Msi; Google Drive → Unknown; Package Cache → BurnBundle; SystemComponent hidden; ParentKeyName/ReleaseType → IsUpdate; NoRemove → CanUninstall false; derived InstallLocation |
| `ExitCodeInterpreterTests` | the full [§6.6](#66-running-an-uninstaller) table |
| `BatchUninstallServiceTests` (fakes: executor, verifier, scanner, interaction) | exit 0 + key present → `StillInstalled`, no scan; exit 0 + key gone → `Removed`, scan; 1605 + gone → `AlreadyRemoved`; 1602 → `Cancelled`, no scan; key already gone → `AlreadyRemoved`, executor not called; BrokenEntry → never `Removed`; cancellation → session saved with the finished items; one failure doesn't stop the queue |
| `ProcessTreeWatcherTests` | `cmd.exe /c start "" /b ping -n 3 127.0.0.1` (cmd exits at once, like an NSIS stub) → still waits for the orphaned `ping` through the ever-watched parent rule; cancellation returns `WaitAbandoned` and kills nothing |
| `ProtectionServiceTests` (extend) | drive root; each SpecialFolder and its ancestors; `%LOCALAPPDATA%\Microsoft`; `Package Cache`; the parent of another app's InstallLocation; HKLM/HKCU `Software\Microsoft` roots; `…\CurrentVersion\Installer`; `HKCR\Installer`; the Uninstall root blocked but its subkey allowed |
| `AppNameNormalizerTests` / `Sift4Tests` | trimming examples; match thresholds; ≤4 characters never match; similar-name penalty |
| `LeftoverScoringTests` | score mapping; pre-tick rule; `DirectoryStillUsed` blocks |
| `LeftoverCleanupServiceTests` | `reg export`/`import` round-trip on the test key; typed value round-trip (String, ExpandString, DWord, QWord, Binary, MultiString); backup failure → `SkippedBackupFailed` and the key remains; dry run deletes nothing; counts equal the per-item results |
| `UninstallCommandBuilderTests` | every kind × {interactive, quiet}; registry `QuietUninstallString` preferred in quiet mode; `Unknown` never gets switches |
| Update existing | [ApplicationDiscoveryTests.cs](AxePower.Tests/Uninstaller/ApplicationDiscoveryTests.cs) (WinGet cases at L69-71, L82-83, L120-135); [EndToEndSafetyValidationTests.cs](AxePower.Tests/Uninstaller/EndToEndSafetyValidationTests.cs) (filter cases at L84-85, L97-98: WinGet → removed, Orphaned → "Broken"); [UninstallStrategyTests.cs](AxePower.Tests/Uninstaller/UninstallStrategyTests.cs); [DependencyInjectionTests.cs](AxePower.Tests/Uninstaller/DependencyInjectionTests.cs) (new registrations) |

---

## 9. Manual test matrix

**Where to test:**
- The audit PC runs **Windows 11 Home**, which has no Windows Sandbox or Hyper-V. Use a VirtualBox or VMware Windows VM.
- If testing on the audit PC instead, create a restore point first and use only the throwaway apps below.
- Install the apps with `winget install --id <id> -e`.

| # | App (winget ID) | Kind | Steps | Expected |
|---|---|---|---|---|
| M1 | PuTTY (`PuTTY.PuTTY`) | Msi | Uninstall from the details panel | Listed under **MSI**; msiexec runs; outcome `Removed`; key gone; leftover review opens |
| M2 | Notepad++ (`Notepad++.Notepad++`) | Nsis | Uninstall and let the wizard sit for 30 s before finishing | Progress stays on "Waiting for uninstaller" until the wizard closes; then `Removed` |
| M3 | Notepad++ (reinstall) | Nsis | Uninstall, click **Cancel** in the wizard | `Cancelled` or `StillInstalled`; **no** leftover scan; app still listed |
| M4 | WinSCP (`WinSCP.WinSCP`) | InnoSetup | Uninstall interactive, then again with quiet mode (P2) | Both `Removed`; quiet shows no UI |
| M5 | Discord (`Discord.Discord`) | Squirrel | Uninstall | `Removed`; `%LOCALAPPDATA%\Discord` offered with evidence |
| M6 | .NET 6 Runtime (`Microsoft.DotNet.Runtime.6`), only if nothing uses it | BurnBundle | Uninstall | Bundle exe `/uninstall` runs (not `msiexec /x {bundle}`); `Removed` |
| M7 | Fake broken entry ([Appendix C](#appendix-c-scripts)) | BrokenEntry | **Remove broken entry** | Confirmation says files aren't touched; `.reg` backup exists; entry gone; `BrokenEntryRemoved` |
| M8 | Any app, twice checked | — | Start a 2-app batch, click **Refresh** and **Cancel** mid-way | Refresh disabled; Cancel stops the queue after the current app; the session is saved |
| M9 | Notepad++ leftovers | — | After M2, review leftovers | `%APPDATA%\Notepad++` and Start Menu shortcut found with evidence; nothing outside the app pre-ticked |
| M10 | Unelevated leftover cleanup | — | Clean a set containing HKLM items without admin | HKLM items locked; banner shown; relaunch elevated enables them |
| M11 | Backup & restore (P4) | — | Clean registry leftovers, then **Restore registry backup** | Keys return with their original values |
| M12 | UAC refusal | — | Uninstall a machine-scope NSIS app, click **No** on UAC | `Cancelled` ("Administrator permission was refused"), not `Failed` |
| M13 | Running app | — | Open Notepad++, then uninstall | Running-app prompt: Close / Skip / Continue work as labelled |

---

## 10. Open decisions

| # | Decision | Recommendation | Why |
|---|---|---|---|
| 1 | Keep WinGet in discovery? | **Remove** (restore later via COM, P5.2) | `winget list` only re-lists registry and AppX entries; parsing its text is locale- and width-fragile and creates duplicates |
| 2 | Quiet uninstall default | **Off** | Silent removal without the vendor's prompts surprises users; BCU also makes quiet opt-in |
| 3 | NSIS quiet method | **`/S` now**, UI automation later (P5.3) | `/S` is built into NSIS uninstallers; verification catches the few that ignore it |
| 4 | Elevation for admin-only cleanup | **Relaunch banner now** (P2.3), elevated helper later (P5.1) | Reuses `ElevationService`, keeps AxePower standard-user by default |
| 5 | Show `SystemComponent` entries | **Hidden by default**, setting to show | Matches Windows Settings and BCU; avoids offering hidden sub-components of bundles |
| 6 | History location | **Move to `%LOCALAPPDATA%`** with migration | Repo rule stores app data under LocalAppData; MSI logs already live there |
| 7 | "Orphaned" wording | **"Broken"** in the UI, enum unchanged | Clearer to users; avoids a breaking rename in saved history |

---

## Appendix A: Uninstall registry values

Keys: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<key>`, `HKLM\SOFTWARE\WOW6432Node\…\Uninstall\<key>`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<key>`.

| Value | Type | Use |
|---|---|---|
| `DisplayName` | SZ | required to list |
| `DisplayVersion`, `Publisher`, `InstallDate` (yyyyMMdd), `EstimatedSize` (KB, DWORD) | | display |
| `InstallLocation` | SZ | leftover root; often missing (derive it, see [§6.5](#65-discovery-rules)) |
| `DisplayIcon` | SZ | icon; fallback for `InstallLocation` |
| `UninstallString` | SZ / EXPAND_SZ | interactive uninstall command |
| `QuietUninstallString` | SZ | vendor-provided silent command |
| `ModifyPath` | SZ | modify/repair (not used) |
| `WindowsInstaller` | DWORD | `1` = MSI product; key name is the ProductCode |
| `SystemComponent` | DWORD | `1` = hidden from Programs and Features |
| `NoRemove` / `NoModify` / `NoRepair` | DWORD | `NoRemove=1` → can't uninstall |
| `ParentKeyName` / `ParentDisplayName` | SZ | child entry (update or component of another product) |
| `ReleaseType` | SZ | `Update`, `Hotfix`, `Security Update` → update |
| `BundleCachePath`, `BundleProviderKey`, `BundleUpgradeCode` | SZ | WiX Burn bundle markers |
| `Inno Setup: App Path` (key name ends `_is1`) | SZ | Inno Setup marker |

---

## Appendix B: BCU source map

Paths are relative to `source/` in [BCUninstaller/Bulk-Crap-Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller).

| Topic | File |
|---|---|
| Kind enum | `UninstallTools/UninstallerType.cs` |
| Kind detection | `UninstallTools/Factory/InfoAdders/UninstallerTypeAdder.cs` |
| MSI detection helper | `UninstallTools/Factory/ApplicationEntryTools.cs` (`PathPointsToMsiExec`) |
| Quiet strings | `UninstallTools/Factory/InfoAdders/InnoSetupQuietUninstallStringGenerator.cs`, `NsisQuietUninstallStringGenerator.cs`, `MsiUninstallStringGenerator.cs`, `QuietUninstallStringCopier.cs` |
| Deriving InstallLocation | `UninstallTools/Factory/InfoAdders/InstallLocationGenerator.cs` |
| Registry discovery & filters | `UninstallTools/Factory/RegistryFactory.cs` (`GetIsUpdate`, `NoRemove`, `SystemComponent`) |
| Entry model, trimmed names, `RegKeyStillExists` | `UninstallTools/ApplicationUninstallerEntry.cs` |
| Wait loop, exit codes, stall detection | `UninstallTools/Uninstaller/BulkUninstallEntry.cs` |
| Child processes | `KlocTools/Extensions/ProcessExtensions.cs` (`GetChildProcesses`) |
| Version stripping | `KlocTools/Tools/StringTools.cs` (`StripStringFromVersionNumber`) |
| Fuzzy distance | `KlocTools/Tools/Sift4.cs` |
| Confidence | `UninstallTools/Junk/Confidence/ConfidenceRecords.cs`, `ConfidenceGenerators.cs` |
| Junk orchestration | `UninstallTools/Junk/JunkManager.cs`, `Junk/Finders/JunkCreatorBase.cs` |
| Drive finders | `UninstallTools/Junk/Finders/Drive/` (`CommonDriveJunkScanner`, `InstallLocationScanner`, `UninstallerLocationScanner`, `SpecificUninstallerKindScanner`, `WerScanner`, `PrefetchScanner`) |
| Registry finders | `UninstallTools/Junk/Finders/Registry/` (`SoftwareRegKeyScanner`, `UninstallerKeySearcher`, `ComScanner`, `AppCompatFlagScanner`, `FirewallRuleScanner`, `RegisteredApplicationsFinder`, …) |
| Misc finders | `UninstallTools/Junk/Finders/Misc/ShortcutJunk.cs`, `StartupJunk.cs` |
| Program Files orphans | `UninstallTools/Junk/ProgramFilesOrphans.cs` |
| NSIS automation | `UninstallerAutomatizer/` |

---

## Appendix C: Scripts

**Reproduce D1/D2 counts** (PowerShell, read-only):

```powershell
$paths = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
         'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
         'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
$all = Get-ItemProperty $paths -ErrorAction SilentlyContinue | Where-Object DisplayName
"Total entries: $($all.Count)"
"D1  unquoted MsiExec entries: " + ($all | Where-Object { $_.UninstallString -match '^(?i)msiexec(\.exe)?\s' }).Count
"D2  GUID key but not MSI:     " + ($all | Where-Object {
        $_.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$' -and $_.WindowsInstaller -ne 1 -and
        $_.UninstallString -notmatch '(?i)msiexec' }).Count
```

Audit PC result on 2026-09-22: 152 total, D1 = 74, D2 = 11.

**Capture test fixtures** (PowerShell, read-only; redacts the user name):

```powershell
$paths = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
         'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
         'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
$out = 'AxePower.Tests\Uninstaller\Fixtures\arp-entries.json'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$props = 'DisplayName','Publisher','DisplayVersion','InstallLocation','DisplayIcon','UninstallString',
         'QuietUninstallString','WindowsInstaller','SystemComponent','NoRemove','ParentKeyName',
         'ReleaseType','BundleCachePath','BundleProviderKey'
$rows = Get-ItemProperty $paths -ErrorAction SilentlyContinue | Where-Object DisplayName | ForEach-Object {
    $o = [ordered]@{ KeyName = $_.PSChildName; ParentKey = ($_.PSPath -replace '^.*::','' -replace '\\[^\\]+$','') }
    foreach ($p in $props) { $o[$p] = $_.$p }
    [pscustomobject]$o
}
($rows | ConvertTo-Json -Depth 3) -replace [regex]::Escape($env:USERNAME), '<user>' |
    Set-Content -Encoding utf8 $out
```

**Create a fake broken entry for M7** (HKCU only; delete it afterwards if the test fails):

```powershell
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BrokenEntryTestApp'
New-Item $k -Force | Out-Null
Set-ItemProperty $k DisplayName 'Broken Entry Test App'
Set-ItemProperty $k Publisher 'Test Publisher'
Set-ItemProperty $k UninstallString '"C:\DoesNotExist\uninst.exe"'
# cleanup: Remove-Item $k -Recurse
```

Don't put "AxePower" in the fake entry's name: the name rule protects it.

---

## Appendix D: Glossary

- **ARP entry**: an "Add/Remove Programs" registry key under `…\CurrentVersion\Uninstall`. It's what Windows Settings → Apps lists.
- **MSI / Windows Installer**: a package installed through `msiexec`, identified by a ProductCode GUID. Its registration lives in the Windows Installer database, which we never edit.
- **Burn bundle**: a WiX bootstrapper (`*.exe` in `C:\ProgramData\Package Cache\{…}`) that chains several MSIs. It's uninstalled by running the bundle exe with `/uninstall`, not `msiexec`.
- **NSIS**: Nullsoft installer. Its uninstaller copies itself to `%TEMP%` and exits at once, which is why we watch the whole process tree.
- **Inno Setup**: `unins000.exe` + `unins000.dat` uninstaller. It supports `/VERYSILENT`.
- **Squirrel**: an updater framework used by Discord, Slack and others. Uninstalled with `Update.exe --uninstall`.
- **Broken entry** (was "Orphaned"): an ARP entry whose uninstaller is missing, or whose MSI product isn't registered.
- **Toolhelp snapshot**: the `CreateToolhelp32Snapshot` Win32 API, which lists all processes with their parent PIDs, including elevated ones.
- **Evidence / confidence record**: a signed score component explaining why a leftover belongs to the removed app (BCU's model).
