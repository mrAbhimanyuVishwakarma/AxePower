# Diagnostics: structured, redacted, local

Prompt 23, 2026-10-06. Replaces the old `AxePower.log` (plain text in `%APPDATA%\AxePower`, raw
`File.AppendAllText` calls, no lock, rotated at 5 MB, and appended to by every test run).

## Where and how long

| | |
|---|---|
| Folder | `%LOCALAPPDATA%\AxePower\Diagnostics` (this machine only, never roamed) |
| Files | `axepower-YYYYMMDD.jsonl`, one JSON object per line; a day rolls to `-2`, `-3` … at 2 MB |
| Kept | 14 days or 20 MB in total, whichever comes first; oldest files go first (owner's choice) |
| Old logs | `AxePower.log` and `AxePower.log.old` are deleted on the first start (owner's choice: they predate full redaction) |
| Upload | None. `Services/Diagnostics` has no network code, and a test fails if any appears |

Retention runs at every start and whenever a file rolls.

## One event

```json
{"time":"2026-10-06T07:35:26.80Z","level":"WARN","category":"LanDrop","eventId":3000,
 "event":"LanDrop.AccessRefused","correlationId":"a1b2c3d4","message":"…","exception":"…"}
```

- **level:** INFO, WARN or ERROR.
- **category:** the subsystem. Existing calls written as `LogInfo("[LanDrop] …")` get it from the
  leading tag, and the tag is removed from the message. Calls without a tag get `App`.
- **eventId / event:** important events have fixed numbers in `Services/Diagnostics/LogEvents.cs`:
  - 1xxx: app lifetime (start, unhandled exceptions, startup failure).
  - 2xxx: diagnostics itself (export, removal of the old logs).
  - 3xxx: security-relevant refusals (LAN Drop lockout, refused update).

  Every other call has ID 0, and `event` names its call site (`LanDropService.StartServerAsync`).
  It holds the file name only, never the build machine's path. Never reuse or renumber an ID.
- **correlationId:** set with `using var _ = LogService.BeginCorrelation();`. It follows the async
  flow, and nested scopes restore the outer ID. Each of these is one operation:
  - a LAN Drop request (`LanDropRequestHandler.HandleAsync`);
  - an update check or download (`UpdateService`);
  - a Software Hub action (`WinGetHubService.RunAsync`);
  - an uninstall batch (`BatchUninstallService`).
- **exception:** a warning gets the type and message; an error also gets the stack. Both are redacted.

## Redaction

`LogRedactor` runs on every text field inside `LogService`, so no caller can opt out. It runs
again on every line when a bundle is exported, which catches lines written before a rule existed.
It removes:

- **URLs:** query strings and fragments (LAN Drop `?t=`, room links `#v=`, OAuth codes and
  tokens), and `user:password@` inside a URL.
- **Headers:** `Authorization` values (Bearer, Basic, Token, Digest), whole `Cookie` and
  `Set-Cookie` headers, and `key=value` / JSON secrets (password, token, api key, client secret, …).
- **Token formats:** JWTs; GitHub, Supabase, OpenAI/Anthropic, Stripe, Slack, AWS, Google and
  Discord key formats; private-key blocks; and any long opaque token mixing letters and digits.
  GUIDs are kept, because uninstalls need them.
- **Personal details:**
  - email addresses become `[email]`;
  - this user's profile path becomes `%USERPROFILE%`, in either slash style;
  - other users' profile folders become `C:\Users\[user]`;
  - the user name becomes `[user]` and the computer name becomes `[computer]`. Common account
    names (`user`, `admin`, …) are only replaced inside paths.

**User content is never logged.** The call sites are responsible for this, because no pattern can
recognise it:

- chat text and quick-text content are not logged (Prompt 21);
- files sent through LAN Drop are logged by kind and size only (`a .pdf file (2.1 MB)`), not by name;
- music library file names in tag errors stay; they are the user's own library, and the path's
  user part is replaced.

## Settings › Diagnostics & Privacy

- **Summary:** the number of files, total size, oldest date and the retention rule.
- **Open…:** the inspect-and-export window. Every file of a bundle is listed with its full text,
  and **Save bundle…** writes exactly those items to a .zip where the user chooses. Cancelling the
  Save dialog writes nothing. Tick-boxes add:
  - **settings:** personal lists and values are replaced by `[removed: N item(s)]`. The list is
    `DiagnosticsBundleBuilder.PersonalSettings`, and a test fails if one of its names disappears
    from the settings model.
  - **uninstall logs:** the 10 newest Windows Installer logs, the last 256 KB of each, redacted,
    and renamed so the program's name is not in the file name.
- **Delete all…:** asks first, then removes every `axepower-*.jsonl` file and clears the in-app
  list. It writes no event about itself, because that would put a file straight back.

A bundle always contains `README.txt` (the file list) and `system-summary.txt`. The summary holds
the app, Windows and .NET versions and the processor, graphics and memory. It never includes the
computer name or the user name.

## Tests and tools

- `new LogService()` keeps events in memory only, and `App.ConfigureServices()` defaults to it.
  So tests and `--self-test` never write diagnostics to the PC. Only real start-up passes a store.
- Tests are in `AxePower.Tests/Diagnostics/`:
  - `SecretFormatRedactionTests`: every format above;
  - `SecretsInErrorPathsTests`: real failures logged to disk, then the raw files are searched;
  - `DiagnosticsStoreTests`: format, 8 threads at once, rolling, retention by age and size, delete;
  - `DiagnosticsBundleTests`: the preview equals the zip byte for byte, personal settings are
    removed, the folder has no network code;
  - `DiagnosticsViewModelTests`: delete needs a yes, export needs a chosen path;
  - `DiagnosticsViewRenderTests`: the card and the window bind without errors in Dark, Light and
    Glass. Set `AXEPOWER_RENDER_DIR` to save PNGs.
