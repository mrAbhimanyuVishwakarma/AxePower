# Errors, offline, cancellation and partial success

**For:** anyone writing code that can fail in front of the user.
**Last updated:** 2026-10-08 (Prompt 30).

Every user action ends in one of four outcomes:

- **Succeeded.**
- **Partly succeeded:** a batch where some items were done.
- **Cancelled:** never an error. It says "cancelled. Nothing was changed".
- **Failed.**

A failure has a kind the user can act on. The types live in
[`AxePower.Core/Services/Outcomes/Outcomes.cs`](../../AxePower.Core/Services/Outcomes/Outcomes.cs).

| Kind | When | What the user is told to do |
|---|---|---|
| Offline | No connection, host not found, network down | Check the internet connection and try again |
| SessionExpired | HTTP 401, an expired or revoked account session | Sign in again from Profile |
| ServiceUnavailable | A third-party service answered with an error (GitHub, YouTube, Supabase, lyrics sites, WinGet sources) | It may be down for a while; try later |
| HardwareUnavailable | WMI/driver/API missing, not supported on this PC | This PC does not support it, or a driver is missing |
| ElevationDenied | The UAC prompt was declined (Windows error 1223), or the action needs admin (740) | Accept the prompt, or turn on Run as administrator |
| Blocked | Access denied, file in use or locked, antivirus (225/226), WinGet checksum or policy refusals | Close other programs; the file may be held or read-only |
| PackageManagerFailed | Any other WinGet failure | See Software Hub's log, or try later |
| NotFound | A file, package or page that is not there | It may have been moved or removed |
| TimedOut | Timeouts, including HttpClient's | Try again in a moment |
| Unexpected | Anything else | Details are in Settings › Diagnostics |

## Rules

- **Show failures with `NotificationService.ShowFailure("Verb the thing", ex)`.** The user reads
  "Could not verb the thing." plus the advice for its kind. For a status line, use
  `UserMessages.Failed("Verb the thing", ex).Text`.
- **Failures are always shown**, even with notifications turned off: they answer something the
  user just did. Ordinary news still follows the switch.
- **Raw exception text never reaches the UI.** `OutcomeTests.TheApp_ShowsNoRawExceptionText`
  fails on any `ex.Message` or `ex.StackTrace` outside logging. The only exception is
  `AccountException`, whose messages are written for users.
- **The Details link** shows `OutcomeClassifier.Detail(ex)`: the exception type, the Windows
  error, HRESULT or HTTP status, and the message, all through `LogRedactor` (no tokens, links,
  emails, user or computer name). There is never a stack trace; that stays in the diagnostics log.
- **Batches report exactly what happened.** Build an `Outcome.Batch` from the items; the message
  says "2 of 4 done (7-Zip, VLC). Not done: Steam (offline). Cancelled before it started:
  Discord." Software Hub's multi-app install uses it (`PackageBatchResult.ToOutcome`).
- **WinGet exit codes are translated** (`OutcomeClassifier.ClassifyWinGet`). "Already installed"
  and "no newer version" count as success.
- **Cancellation:** pass the operation's token to `Outcome.From`/`UserMessages.Failed`. An
  `OperationCanceledException` from that token is Cancelled; one nobody asked for (an HttpClient
  timeout) is TimedOut.
- **Start-up failure** shows the plain message and the redacted detail, and points to
  `%LOCALAPPDATA%\AxePower\Diagnostics`. It used to show the stack trace.

## Not yet converted

Places that still build their own failure text without an exception (for example "Uninstall of
X finished. Check logs.") were left as they are. They contain no raw exception text. Converting
them to `Outcome` is follow-up work, done as each area is touched.
