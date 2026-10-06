# Safe changes: one framework for everything that modifies Windows

Prompt 24, 2026-10-06. Code: `AxePower.Core/Services/Operations/`. UI: the preview window
(`App/Windows/OperationPreviewWindow`) and Settings › Change history.

## The rule

Every AxePower action that changes Windows state follows the same four steps:

1. **Plan.** It reads the current state of each target and describes the change. Nothing changes.
2. **Preview and approve.** The user sees each target's value now and the value it will get, the
   reversibility level, and whether administrator rights are needed. Nothing changes until they
   click Apply (or Roll back).
3. **Execute.** Right before each step, a fresh snapshot is taken. The step is applied, and the
   result is read back and compared.
4. **Record.** The operation, with every snapshot and result, goes to the journal
   (`%LOCALAPPDATA%\AxePower\Operations`, newest 300 kept). It can be inspected and, where
   supported, rolled back from Settings › Change history.

**A rollback is an operation too.** It is built from the snapshots, previewed, executed and
recorded the same way. Steps it cannot honestly undo are listed, not hidden.

## The operation record

| Field | Meaning |
|---|---|
| `action`, `source`, `subject` | What was asked ("Apply tweak: …"), which feature asked ("System Optimizer › Tweaks"), and what it is about ("tweak:mouse-accel-disable") so a feature can find its last change |
| `requiresAdmin` (per step) | Taken from the target: HKLM and services need administrator rights. Without them, the operation is refused before any change |
| `steps[]` | Kind, target, `before` (the snapshot), `after`, a readable `description`, `status`, `message`, `executedUtc` |
| `reversibility` (per step) | `FullyReversible`, `PartiallyReversible` or `Irreversible`, with a plain-words `reversibilityNote` when it is not full. The operation's level is its **weakest** step |
| `status` | `Succeeded`, `PartiallySucceeded`, `Failed`, `FailedAndUndone` or `Refused` |
| `rollbackOf` / `rolledBackBy`, `rolledBackUtc` | The link between an operation and its rollback |
| `createdUtc`, `approvedUtc`, `completedUtc`, `appVersion`, `correlationId` | When it happened, with which AxePower version, and the diagnostics correlation ID of the run |

## Honesty rules (enforced in code and tests)

- **Never promise more than can be done.** A level is set by the change handler for its kind of
  change, not by the feature. A service change is *partially* reversible: its settings come back
  exactly, but whatever it was doing when it was stopped does not. A third-party uninstaller or
  script is *irreversible* from AxePower.
- **The Roll back button only appears when rollback is possible.** Otherwise the reason is shown:
  "Already rolled back.", "Cannot be undone from AxePower.", "It was undone automatically when it
  failed.", and so on.
- **Rollback never overwrites a later change.** If a target is no longer in the state AxePower
  left it in (the user or another program changed it since), that step is skipped and the history
  says "Changed since by something else; left as it is."
- **A step that fails part-way is still undone.** For example, a service's start type changed but
  it would not stop.
- **Results are reported as they happened.** Messages give the real number of steps that worked
  and that failed. If a change cannot be saved to the history, the record says it cannot be rolled
  back from there.

## Change handlers

| Kind | Targets | Snapshot | Reversibility |
|---|---|---|---|
| `registry.value` | `HKLM\…\Key\|Name`, `HKCU\…\Key\|Name` | Exact type and data, or "not set"; plus the topmost missing key, so rollback also removes keys it created (only while they are still empty) | Fully |
| `registry.key` | `HKCU\…\Key` | Present, or absent with the topmost missing ancestor (rollback removes exactly what was created) | Fully |
| `service` | Service name | Start type (including delayed start) and running state, through the service control manager (no `sc.exe`) | Partially |

Adding a kind means writing an `IChangeHandler` (`Read`, `Apply`, `Describe`,
`RequiresAdministrator`, `ReversibilityOf`, and `Same` when two states can look different but mean
the same) and registering it with the runner in `App.ConfigureServices`.

## Integrated (Prompt 24)

**Low risk: System Optimizer tweaks.**
- Every tweak is now a list of exact registry requests (`TweakService.ApplyRequests`).
- Apply and Revert are previewed, and Apply all and Revert all show every tweak in one preview.
- Revert rolls back the recorded apply, which restores the user's own earlier values. Before this,
  Revert wrote hard-coded "defaults" (for example mouse speed 1/6/10), whatever the user had
  before.
- A tweak applied before this history existed reverts to Windows' defaults, and its preview says
  that plainly.
- A test applies and reverts every tweak on a scratch registry and checks that it comes back
  identical, keys included.

**High risk: Game Mode service pausing.**
- Before, it stopped services and ran `sc.exe config … start= demand`, with no record of the old
  start type, no way back, and a success message whether it worked or not.
- Now each selected service is snapshotted (start type, delayed start, running), previewed and
  changed through the service control manager.
- One failing service (for example Windows Update Medic, which Windows protects) does not stop
  the others, and the result says which ones failed.
- Rolling back puts each start type back and starts the services that were running.

## Migration path for the rest

Each item says what the feature changes today, how it maps onto the framework, the honest level,
and what is needed first. Do them one at a time, each with the same tests: a round trip on a
scratch registry or fake, plus the preview and history UI.

| Feature | Changes today | Mapping | Level | Needs first |
|---|---|---|---|---|
| **Power plans** (`PowerPlanService`) | Duplicates schemes, writes AC/DC value indexes, deletes schemes, switches the active scheme (`IPowerSchemeApi`) | A `powerscheme` handler: active scheme (snapshot: the previous GUID); value index (snapshot: previous AC/DC); scheme existence (create = duplicate; rollback = delete the duplicated scheme) | Switching: fully. Creating AxePower's plans: fully (delete the copies). **Deleting a user's own scheme: irreversible** unless it is exported first (`powercfg /export`), then partially | Mode switches happen on every power event, so record only user-started plan edits, not automatic switching, or the history floods |
| **DNS** (`DnsOptimizerService`) | `netsh interface ipv4 set dnsservers … static`; "Reset" goes back to DHCP | A `dns` handler per adapter: snapshot of DHCP vs static and the current servers (IPv4 and IPv6), read through `Get-DnsClientServerAddress` or WMI; apply through netsh, allow-listed (Prompt 19) | Fully, while the adapter still exists. Today's Reset to DHCP **loses** a user's own static DNS; after migration, rollback restores it | Adapter identity by interface index or GUID, not name; netsh output parsing |
| **Windows Update policy** (`WindowsUpdateShieldService`) | Registry policy values under `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` (and `\AU`); "off" deletes them | Plain `registry.value` requests, the same as the tweaks: almost free | Fully (registry). Note: Windows may already have applied a deferral it read earlier; the note says the policy is restored, not the update history | Move `IWindowsUpdateSystem` writes into requests, like `TweakService` |
| **Debloat** (`DebloatService`) | Launches third-party scripts (Win11Debloat, WinUtil) in their own window | AxePower cannot see what the scripts change. Record **one irreversible step**, "Ran Win11Debloat (third-party)", with the note "Changes made by this script cannot be listed or undone by AxePower; create a restore point first." Offer a restore point in the preview | Irreversible. Never shown with a Roll back button | Nothing technical; the preview must say it plainly |
| **Service throttling** (`ServiceThrottlerService`) | `net stop` / `net start` while gaming, restored automatically afterwards | `service` requests with `running` only (start type unchanged). Its automatic restore becomes the rollback of the recorded operation | Partially (same reason as Game Mode) | Automatic restore must use `PlanRollback` + execute, not its own list |
| **Game Mode GPU** (`GameModeGpuService`, `GpuDisplayModeService`) | Per-game `UserGpuPreferences` registry values, backed up in settings.json; firmware display mode (MUX) through Lenovo WMI | GPU preference: `registry.value` requests (replace the settings.json backup with the journal). Display mode: a `firmware.displaymode` handler (snapshot: current mode) | GPU preference: fully. Display mode: **partially**, because it needs a restart to take effect and rollback needs one too, which the note says | Migrate the existing settings.json backups into a journal entry once, so nothing is lost |
| **Uninstaller cleanup** (`LeftoverCleanupService`, `BackupManifest`, `RegistryBackupService`) | The program's own uninstaller (third party), then leftover files (Recycle Bin or backup folder) and registry keys (exported first), with a session restore | Three step kinds in one operation: `uninstaller.run` (irreversible: a third-party program), `file.recycle` (partially: back from the Recycle Bin while it has not been emptied), `registry.exportdelete` (fully: from the export). The session restore becomes the rollback | Mixed, so the operation as a whole is **irreversible**, and the preview shows each step's own level. Rollback restores the files and registry and never claims to reinstall the program | The existing backup manifest becomes the snapshot store for these steps; keep its "damaged index is kept" rule |

## Tests

`AxePower.Tests/Operations/`:

- `OperationRunnerTests`: preview changes nothing; execution records everything; exact rollback,
  including absent values and created keys; skip when changed since; refuse without admin; undo on
  failure, including a half-applied step; journal survives restart and skips damaged files.
- `TweakAndServiceOperationTests`: every tweak round-trips to an identical registry; the user's own
  value comes back; defaults are used only without a record, with a notice; the classic menu key is
  removed whole; service pause preview, rollback and partial failure; not installed; `TweakService`
  writes no registry and Game Mode launches no process.
- `OperationViewModelTests`: nothing changes without approval; history rollback needs approval; the
  wording never promises more than the level.
- `OperationViewRenderTests`: the preview and history bind without errors in Dark, Light and Glass.

## Destructive-action confirmation standard (Prompt 27)

Every destructive or system-wide action asks through **one** dialog (`App/Windows/ActionConfirmationWindow`).
Its content is an `ActionConfirmation` (`Core/Services/Operations/ActionConfirmation.cs`), built
from the action's own data by `ConfirmationCatalog`, or by `ActionConfirmation.ForOperation` for a
planned operation. So the content is tested without showing the dialog.

Each confirmation shows:

- the action and its scope in plain words ("Remove 14 leftovers of uninstalled apps");
- the feature asking;
- everything affected (scrollable): the apps, files, keys, services, adapter or packages;
- whether administrator rights are needed;
- the risk: low, medium or high;
- what is backed up first and how to get it back, or that nothing is;
- whether a restart or sign-out is needed;
- whether it is fully, partly or not reversible, with the reason when it is not fully.

Its behaviour:

- **Cancel is the default button and has focus**, so Enter or a stray click changes nothing.
- The action button names the action ("Remove 2 leftovers", "Run Win11Debloat"), never "OK" or "Yes".
- **High risk needs a tick box** ("I understand what will change" / "… cannot be undone from AxePower"),
  never pre-ticked. The action button stays disabled until it is ticked, with the keyboard or the
  controller as well. This was the owner's choice over type-to-confirm and hold-to-confirm.
- No "Are you sure?", no urgency words, no preselected destructive option (`ConfirmationStandardTests`).

| Flow | Confirmation | Risk | Reversibility |
|---|---|---|---|
| Uninstall apps (Uninstaller Pro) | `UninstallApps`: every app and its uninstall method, skipped (protected) apps, restore point on or off | High (dry run: low) | Not reversible: each app's own uninstaller removes it |
| Clean leftovers | `CleanLeftovers`: every file, folder and registry item by type and path | High (dry run: low) | Partly: registry from the .reg backups; files from the Recycle Bin until it is emptied, or not at all when Recycle Bin is off |
| Restore uninstall backups | `RestoreUninstallBackup` | Medium | Partly |
| Debloat scripts (Win11Debloat, WinUtil) | `RunDebloatScript`: what the script does, and its exact command | High, restart needed | Not reversible: AxePower cannot see or undo a third-party script's changes |
| Windows Update cache reset | `ResetWindowsUpdateCache`: each service stopped and started again, and the folder emptied | Medium | Partly: services are restored, downloads are fetched again |
| DNS change | `ChangeDns`: the adapter's servers now and after | Low | Fully from automatic DHCP; partly from servers set by hand, which "Automatic" does not bring back (the dialog shows them, so the user can note them) |
| DNS back to automatic | `ResetDnsToAutomatic` | Low | Same rule |
| Install several apps | `InstallPackages`: every app | Medium (one app: low) | Partly: apps can be uninstalled; what installers set up may stay |
| Update all apps | `UpgradeAllPackages` | Medium | Not reversible: earlier versions are not kept |
| Trust & Permissions controls | stop listeners, rotate access, delete data, sign out, delete account | Low to high | As stated in each |

Still on plain message boxes, to move to this standard next: deleting a routine, closing caution
apps in Game Mode (a countdown dialog), and the in-progress uninstaller questions (app still running,
stop waiting). The in-progress questions are decisions during a run, not confirmations of a
destructive action, so they may stay as they are.
