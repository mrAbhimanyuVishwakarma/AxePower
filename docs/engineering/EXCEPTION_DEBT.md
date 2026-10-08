# Exception-handling debt report

**Scope:** every `catch` in `AxePower.App` and `AxePower.Core` (generated `obj/` code excluded).
**Last measured:** 2026-10-06 (Prompt 21, then re-measured after merging commit `385b0d9`), by `AxePower.Tests/Diagnostics/ExceptionHandlingPolicyTests.cs`.
**Enforced by:** that test, which fails the build on a regression (see *Keeping it this way*).

## Status against the exit criteria

| Criterion | State |
|---|---|
| Security and destructive-operation failures cannot disappear silently | **Met.** In those areas, every broad catch without a filter logs, reports to the caller or user, rethrows, or carries a written justification. Count of exceptions to that rule: **0**. |
| Logging is useful without exposing secrets | **Met.** `LogService` runs every message and exception through `LogRedactor` (URL queries and fragments, auth headers, JWTs, key=value secrets, cookies, emails, long opaque tokens). LAN Drop no longer logs quick-text content. `LogWarning(message, ex)` records the type and message without the stack. |
| Remaining empty catches are intentional and documented | **Met.** Empty catches without a comment: **0** (was 210). |

## Measurements

| Measure | Start of Prompt 21 | Now |
|---|---|---|
| Catch clauses | 785¹ | 767 |
| Broad (bare or `Exception`) | 629¹ | 597 |
| … with a narrowing `when` filter | 24 | 182 (88 use `ExpectedFailures`) |
| Broad, unfiltered, and silent (empty, comment-only, `return`, other) | 370¹ | 164, all documented except the debt below |
| Empty with no comment | 210 | **0** |
| Broad catches that neither explain nor report (the debt) | not measured | **43**, none in security or destructive code |

¹ The first scan also counted about 20 `catch` words inside string literals, mostly PowerShell
scripts embedded in C#. The scanner now masks strings and comments, so later figures are exact.

Handling by area now (catch clauses):

| Area | Logs | Reports or rethrows | Typed or filtered | Documented | Unexplained |
|---|---|---|---|---|---|
| Security (Share Screen host and session, LAN Drop, accounts, Friends, updates, elevation, logging, process runner) | 51 | 12 | 70 | 10 | **0** |
| Destructive (uninstaller, cleaners, tweaks, services, Game Mode guards, Software Hub) | 47 | 23 | 79 | 22 | **0** |
| Everything else | 132 | 83 | 98 | 83 | 43 |

## Classification and how each kind is handled

| Kind | What it looks like | How it is handled now |
|---|---|---|
| **Expected cleanup / best effort** | Closing a socket, killing a process that may have exited, deleting a temp file, disposing on shutdown | Narrowed to the expected exceptions where they are clear (`ExpectedFailures.FileSystem/Process/Registry/Com`, `Connection.IsGone`); otherwise kept broad with a one-line `Best-effort:` reason. Kept broad on purpose where throwing would be worse: crash handlers, shutdown, the logger itself. |
| **Recoverable operational failure** | Probing the registry, WMI or the file system; a missing vendor feature; one bad entry in a scan | The probe returns its documented default ("unknown", "not installed", skipped) with the reason in a comment, narrowed with `ExpectedFailures.*`. Sampled every second (telemetry) or vendor-scattershot (battery modes), so deliberately not logged. |
| **User-action failure** | A button that copies, opens a page, starts a game, deletes history, joins a room | Typed catch plus a toast that says what failed and what to do: copy (clipboard busy), open links and Settings pages, Play on a game card, delete a history entry, a joined room's chat ending ("the room refused this link… ask for a new one"). |
| **Programming / invariant failure** | A null reference, a bad cast | No longer swallowed where filters were added: `ExpectedFailures` and `Connection.IsGone` deliberately exclude them, so a defect surfaces instead of looking like a dropped connection. |
| **Security-relevant failure** | Session-lock watch, network-change watch, admission, update verification, protected-path lists | Logged or failing closed with the reason recorded: see the list below. |

## Highest-risk cases fixed (in order of risk)

**Security**
- `ShareViewerHost`: if watching for the PC locking fails, it is now logged. That watch is what stops remote control on lock. The 23 silent socket catches are narrowed to `Connection.IsGone`, and `Connection.Close` is made safe so callers need no catch.
- `ShareRoomSession`: a refused admission or a dropped chat used to vanish. It is now logged and raised as `ChatEnded` with a plain reason, which the Friends page shows. Messages that are authenticated but malformed are logged, without their content.
- `LanDropService`: if watching for network changes fails, it is now logged (protection falls back to the periodic session check). The request time-outs and refusal sends are narrowed.
- `LanDropClientRegistry.EndAll`: one device's failing cancellation can no longer stop the others from being cut off.
- `UpdateService`: an update whose verification throws is now logged as "will not be installed". The Authenticode and manifest verifiers were already failing closed and are documented.
- `LanDropEndpoints`: quick-text messages are no longer written to the log (only their length).

**Destructive operations**
- `UninstallHistoryService`: a history file that could not be read (locked or corrupt) was treated as empty, and the next save then **erased the whole history**. Read errors now propagate, a corrupt file is kept beside the new one, and delete and clear report failure to the page.
- `BackupManifestStore`: a corrupt backup index was overwritten by the next record, **losing the map to every backup file** of that session. It is now moved aside and logged.
- `ProtectedPaths`: if enumerating `Program Files\Windows*` failed, those folders went unprotected. The well-known names are now listed explicitly as well.
- `AppKillerService`: a resume that fails, whether by NTSTATUS or by exception, is now logged. The id stays listed, so the next resume retries it and no app is left silently frozen.
- `ServiceThrottlerService`: `net stop/start` failures were swallowed, and the count and log claimed success anyway. Success now comes from net's exit code, and a failed restart stays marked for the next restore.
- `TweakService`: a failed revert (an undeletable value or key) is no longer swallowed, so the page reports it instead of saying "reverted".
- `FileCleanupService`: the cause of a failed delete is logged (the item already showed "Failed").
- `SystemDoctorService`: a cancel that cannot stop the tool now says so.

## Conventions for new code

- **Catch what you expect.** Use `catch (Exception caught) when (ExpectedFailures.Registry(caught))`
  (or `FileSystem`, `Process`, `Com`, `Wmi`, `Path`, `Parse`), or `Connection.IsGone` for sockets.
  A bare `catch` or a plain `catch (Exception)` needs a reason it must be broad.
- **Say why in the catch.** Start the comment with what happens instead: `Best-effort:`,
  `Fails closed:`, `Fails safe:`, `Reported:` (the caller shows it), or the default that is used.
- **Log failures that change behaviour,** with `LogWarning("what failed and what happens now", ex)`.
  Do not log in per-second loops or vendor probes that are expected to fail on most PCs.
- **Tell the user** when a button they pressed did nothing, in words they can act on.
- **Never log** credentials, tokens, room or share links, OAuth codes, cookies, command secrets,
  or message content. `LogRedactor` is the safety net, not the plan: log a length or a kind instead.

## Remaining debt (43 broad catches that neither explain nor report)

All are outside security and destructive code. Most are UI-thread conveniences, music-engine
plumbing and telemetry fallbacks. Each should become typed, documented or reported when its file
is next touched; lower `MaxUnexplainedBroadCatches` in the policy test when you do.

| File | Count | Lines |
|---|---|---|
| `AxePower.App/App.xaml.cs` | 4 | 69, 589, 613, 652 |
| `AxePower.App/ViewModels/PhoneHubViewModel.cs` | 3 | 288, 467, 997 |
| `AxePower.Core/Services/SystemTelemetryService.cs` | 3 | 524, 631, 698 |
| `AxePower.Core/Services/TerminalService.cs` | 3 | 52, 92, 121 |
| `AxePower.App/MainWindow.xaml.cs` | 2 | 419, 435 |
| `AxePower.App/ViewModels/AboutViewModel.cs` | 2 | 166, 184 |
| `AxePower.App/ViewModels/LanDropViewModel.cs` | 2 | 197, 351 |
| `AxePower.Core/Services/Music/Library/TagLibTagReader.cs` | 2 | 64, 78 |
| `AxePower.App/ViewModels/GameCardViewModel.cs` | 1 | 117 |
| `AxePower.App/ViewModels/MainViewModel.cs` | 1 | 498 |
| `AxePower.App/ViewModels/Music/AmbientModeViewModel.cs` | 1 | 112 |
| `AxePower.App/ViewModels/Music/LyricsViewModel.cs` | 1 | 266 |
| `AxePower.App/ViewModels/Music/MusicViewModel.cs` | 1 | 1087 |
| `AxePower.App/ViewModels/Music/NowPlayingViewModel.cs` | 1 | 573 |
| `AxePower.App/ViewModels/SettingsViewModel.GameMode.cs` | 1 | 89 |
| `AxePower.App/ViewModels/SoftwareHubViewModel.cs` | 1 | 1027 |
| `AxePower.App/ViewModels/ToolsViewModel.cs` | 1 | 344 |
| `AxePower.App/Views/ShareScreen/ShareScreenView.xaml.cs` | 1 | 233 |
| `AxePower.Core/Helpers/CommandRunner.cs` | 1 | 84 |
| `AxePower.Core/Services/AfterburnerSensorReader.cs` | 1 | 98 |
| `AxePower.Core/Services/AndroidControlService.cs` | 1 | 280 |
| `AxePower.Core/Services/GpuControlAppService.cs` | 1 | 174 |
| `AxePower.Core/Services/GraphicsDriverService.cs` | 1 | 501 |
| `AxePower.Core/Services/Music/Engine/EngineWorker.cs` | 1 | 35 |
| `AxePower.Core/Services/Music/Engine/NAudioEngine.cs` | 1 | 366 |
| `AxePower.Core/Services/Music/Library/LibraryDatabase.cs` | 1 | 96 |
| `AxePower.Core/Services/Music/Providers/StreamCache.cs` | 1 | 116 |
| `AxePower.Core/Services/ShareScreen/ShareAttachmentTransfer.cs` | 1 | 97 |
| `AxePower.Core/Services/ShareScreen/ShareAudioPump.cs` | 1 | 144 |
| `AxePower.Core/Services/ShareScreen/ShareEncoderProbe.cs` | 1 | 139 |

**Known issues found but not fixed here**
- `PhoneHubViewModel` still builds a winget command line in UI code as a fallback when the
  Software Hub service is missing (it never is in the app); route it through `IWinGetHubService`.
- `BatteryService.SetConservationMode/SetRapidCharge` logs "set to Enabled" even when no vendor
  mechanism applied; it should report whether the driver call succeeded.
- `LeftoverScannerService` drops a failing finder's results without a log line (documented, not logged).
- `CloudBridgeService` (merged on 2026-10-06 from commit `385b0d9`) links this PC to a Supabase
  Realtime channel, so AxePower Mobile can read telemetry and drive power and music from anywhere.
  That is remote control over the internet. Its catches now meet the policy (and it is in the
  test's security area), but the service itself has not had a security review: who may send
  commands, what is validated, and what is logged. Prompt 32's security review should cover it.
- Untested on a real system: the throttler's net.exe results, the resume retry in
  `AppKillerService`, tweak-revert failures (no access-denied registry in tests), and the Friends
  page's "chat ended" toast (owner to see; it is a visible change).

## Keeping it this way

`ExceptionHandlingPolicyTests` scans every catch on each test run:
- `NoEmptyCatchIsUndocumented`: an empty catch needs a comment.
- `SecurityAndDestructiveFailures_CannotDisappearSilently`: zero unexplained broad catches in the
  security and destructive areas (their file patterns are at the top of the test).
- `RemainingDebt_OnlyGoesDown`: at most 43 unexplained broad catches elsewhere and 417 unfiltered
  broad catches. Lower the limits as debt is paid; the test's detailed output lists each site.

`LogRedactorTests` pins what is redacted and what is kept, and `FailureHandlingTests` covers the
history, backup-index, room-chat and filter behaviour above.
