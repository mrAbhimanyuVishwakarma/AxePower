# AxePower Engineering Baseline

- **Captured:** 2026-10-03 19:21 IST (`2026-10-03T19:21:00+05:30`)
- **Branch:** `v2.1.1---monitization-features`
- **Commit:** `b68f0019f850a051a736b60277a57c922ec581cc` (`b68f001`)
- **Commit subject:** `Merge pull request #13 from mrAbhimanyuVishwakarma/V2.1.0`
- **Assessment type:** read-only baseline plus this report; no application behavior was changed

## Outcome

The tracked desktop solution builds successfully and its automated desktop and backend tests pass.

| Gate | Result |
|---|---|
| Release rebuild | PASS — 0 errors, 4 emitted warnings (2 distinct warnings, each emitted for the WPF temporary project and final project) |
| .NET tests | PASS — 1,120 passed, 0 failed, 1 skipped, 1,121 total |
| Supabase migration/RLS tests | PASS — migrations loaded and 48/48 assertions passed |
| Coverage | 50.37% lines, 39.60% branches overall |
| NuGet vulnerability audit | FAIL — one high-severity transitive package advisory is present |
| NuGet deprecation audit | ATTENTION — SQLite native package and xUnit v2 packages are reported legacy |
| Release signing | FAIL — all 19 tracked installer executables are unsigned |
| User settings isolation | PASS — `settings.json` length, timestamp, and SHA-256 were byte-identical before and after tests |

This is a baseline, not a production-readiness certification. The test results do not prove unexercised live services, UI rendering, installer upgrade behavior, or security against an active attacker.

## Scope

### Included tracked product surfaces

| Area | Tracked files | Source files | Approx. source lines | Baseline treatment |
|---|---:|---:|---:|---|
| `AxePower.Core` | 253 | 252 | 36,495 | Built, unit/integration tested, statically inspected |
| `AxePower.App` | 173 | 129 | 35,490 | Built, WPF tests run, statically inspected; UI not visually exercised |
| `AxePower.Tests` | 94 | 92 | 13,320 | Complete automated .NET suite run |
| `Installer` | 22 | 3 | 294 | Scripts inspected; tracked installers' signatures checked; installer not rebuilt or executed |
| `AppWebsite` | 50 | 15 | 7,079 | Static HTML/CSS/JS inspected; no browser/E2E test exists |
| `supabase` | 13 | 8 | 846 | Migrations and RLS rules tested in PGlite; functions inspected; live project not queried |

The line counts include tracked C#, XAML, SQL, TypeScript/JavaScript, HTML/CSS, PowerShell, and Inno Setup sources where applicable. Assets, screenshots, planning documents, skins, and reference material are tracked but are not executable product code for this baseline.

### Explicitly excluded untracked/local material

The initial worktree contained the following pre-existing changes. They were preserved and were not treated as part of the tracked desktop product baseline:

```text
 M idea.txt
?? .claude/
?? .release/
?? AxePower Mobile/
?? LenovoLegionToolkit/
?? SOFTWARE_COMPLETION_PROMPTS.md
?? Smart-Disk-Analyser/
?? Social Media/skills_library/
?? implimented_ideas.txt
?? lenovo_vantage/
?? monitization.md
?? winutil/
```

In particular, `AxePower Mobile`, `LenovoLegionToolkit`, `lenovo_vantage`, `Smart-Disk-Analyser`, `winutil`, and `Social Media/skills_library` are untracked prototypes/reference repositories and were not built, tested, scanned, or modified. `idea.txt` was already modified before the baseline. This task added this report, marked Prompt 1 complete in the existing prompt playbook, and updated the mandatory Music/LAN verification ledger with newly discovered portal risks.

## Environment

| Component | Value |
|---|---|
| OS | Microsoft Windows NT 10.0.26300.0 |
| Time zone | India Standard Time |
| PowerShell | 5.1.26100.9549 |
| .NET SDK | 8.0.425 |
| Node.js | v24.19.0 |
| Git | 2.55.0.windows.5 |
| Target framework | `net8.0-windows10.0.19041.0` |

## Commands executed

Commands were run from `D:\Projects\AxePower`.

```powershell
# Identity and initial state
git rev-parse HEAD
git branch --show-current
git log -1 --format='%H%n%h%n%cI%n%s'
git status --short

# Release build; --no-restore used the already-restored dependency graph
dotnet build AxePower.sln -c Release --no-restore -t:Rebuild

# Complete desktop suite plus coverage
dotnet test AxePower.Tests\AxePower.Tests.csproj `
  -c Release --no-build --no-restore `
  --collect:"XPlat Code Coverage" `
  --results-directory artifacts\baseline-coverage `
  --logger "console;verbosity=minimal"

# Current NuGet metadata from https://api.nuget.org/v3/index.json
dotnet list AxePower.sln package --vulnerable --include-transitive
dotnet list AxePower.sln package --deprecated --include-transitive
dotnet list AxePower.sln package --outdated --include-transitive
dotnet list AxePower.sln package --outdated --include-transitive --highest-minor

# Supabase migration and RLS test (dependency installed temporarily)
npm.cmd install --prefix supabase\tests --no-package-lock --ignore-scripts --no-audit --no-fund
npm.cmd test --prefix supabase\tests

# Installer signature inspection
Get-AuthenticodeSignature AxePower.App\bin\Release\net8.0-windows10.0.19041.0\AxePower.exe
Get-AuthenticodeSignature Installer\Output\AxePower-v2.1.0-Setup.exe
Get-ChildItem Installer\Output -Filter *.exe | ForEach-Object {
  Get-AuthenticodeSignature $_.FullName
}
```

The temporary `artifacts\baseline-coverage` directory and `supabase\tests\node_modules` were deleted after their results were summarized. No npm lock file was created.

## Build results and warnings

The clean Release rebuild completed in approximately 18 seconds:

```text
Build succeeded.
4 Warning(s)
0 Error(s)
```

There are two distinct warnings. WPF reports each once while compiling its generated temporary project and again for the final application project:

1. `CS0108` at `AxePower.App/Views/PowerTimerControl.xaml.cs:65`: `PowerTimerControl.Parent(DependencyObject)` hides `FrameworkElement.Parent`.
2. `MVVMTK0034` at `AxePower.App/ViewModels/SettingsViewModel.cs:451`: the `[ObservableProperty]` backing field `_currentSettings` is referenced directly instead of through the generated property.

An incremental build reported zero warnings because the affected project did not need recompilation; `-t:Rebuild` is therefore the reproducible warning check.

## Test results

### Desktop solution

```text
Passed:  1120
Failed:     0
Skipped:    1
Total:   1121
Duration: 42 seconds test time (approximately 50 seconds including collection)
```

The skipped test is:

```text
AxePower.Tests.Uninstaller.LiveEchoMusicUninstallTest.RunControlledUninstallAndScan_EchoMusic
Reason: "Live test: uninstalls a real app. Run manually only."
```

It is intentionally destructive: it discovers and uninstalls a real Echo Music installation, creates a restore point, and scans leftovers. It was correctly not enabled during this baseline.

Tests that use HKCU and Windows DPAPI were run with normal current-user access. Before and after the suite, `%LOCALAPPDATA%\AxePower\settings.json` had:

```text
Length: 8430 bytes
SHA-256: 1DCBEBED18A1EF828B6EA739246816E19D2B9A3E8591D53F45288791BC81B3C7
Last-write UTC: 2026-10-03T13:05:42.8684675Z
```

This proves the test run did not alter that settings file. It does not prove that no other machine state can ever be affected by every test or manual workflow.

### Supabase backend

`supabase/tests/friends.test.mjs` successfully loaded every migration into PGlite and passed 48/48 authorization and behavior assertions covering friend requests, RLS isolation, presence, invitations, blocking, profile search, anonymous denial, and account-deletion cascades.

This is a PostgreSQL-in-WASM stand-in. It does **not** verify the deployed Supabase project's migrations, grants, environment variables, Edge Function settings, JWT verification settings, or live provider configuration.

### Self-test not executed

`AxePower.exe --self-test` was inspected but not executed. Although repository guidance describes it as read-only, `App.RunSelfTest` resolves `PortalAccessToken.Value`, which can create and persist a DPAPI-protected portal secret on first use. Running it would therefore risk modifying real user data during a baseline whose instructions require preservation. This mismatch is recorded as a current risk.

## Coverage

Coverage was collected with Coverlet's XPlat collector and then deleted after parsing.

| Assembly | Line coverage | Branch coverage |
|---|---:|---:|
| `AxePower.Core` | 58.14% | 49.15% |
| `AxePower` (WPF app) | 35.60% | 21.12% |
| **Overall** | **50.37%** (16,260 / 32,279) | **39.60%** (6,775 / 17,108) |

Important low/zero-coverage areas include live Share Screen startup/tool acquisition, LAN download handling, Android tool acquisition, real media/provider paths, Windows power actions, battery polling, DNS operations, and several WPF view-model workflows. Coverage measures executed code, not correctness; the lower WPF number is expected in part because visual and native Windows behavior needs additional integration/manual verification.

## NuGet dependency baseline

Package metadata was queried from NuGet on 2026-10-03. Results will change over time.

### Vulnerable packages

One resolved transitive package is reported in all three consuming projects:

| Resolved package | Version | Severity | Advisory | Introduced through |
|---|---:|---:|---|---|
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.6 | High | `GHSA-2m69-gcr7-jv3q` | `Microsoft.Data.Sqlite` 8.0.11 |

The duplicate project listings are one dependency issue propagated into Core, App, and Tests, not three independent vulnerabilities.

### Deprecated packages

- `SQLitePCLRaw.lib.e_sqlite3` 2.1.6 is marked `Legacy`; NuGet names `SourceGear.sqlite3` as the alternative.
- The test project's top-level `xunit` 2.9.3 and its v2 transitive packages are marked `Legacy`; NuGet names xUnit v3 packages as alternatives.

Deprecation is not itself proof of an exploitable vulnerability. The high-severity SQLite advisory above is separately actionable.

### Outdated direct packages

The unconstrained audit reported these direct updates:

| Project | Package | Resolved | Latest reported |
|---|---|---:|---:|
| Core | `Microsoft.Data.Sqlite` | 8.0.11 | 10.0.12 |
| Core | `Microsoft.Win32.SystemEvents` | 10.0.10 | 10.0.12 |
| Core | `NAudio.Core` | 2.4.0 | 3.1.0 |
| Core | `NAudio.Vorbis` | 1.5.0 | 3.0.0 |
| Core | `NAudio.Wasapi` | 2.4.0 | 3.1.0 |
| Core | `System.Management` | 10.0.10 | 10.0.12 |
| Core | `System.Security.Cryptography.ProtectedData` | 8.0.0 | 10.0.12 |
| Core | `System.ServiceProcess.ServiceController` | 10.0.10 | 10.0.12 |
| App | `CommunityToolkit.Mvvm` | 8.2.2 | 8.4.2 |
| App | `Microsoft.Extensions.DependencyInjection` | 8.0.0 | 10.0.12 |
| App | `Microsoft.Web.WebView2` | 1.0.3485.44 | 1.0.4258.31 |
| Tests | `coverlet.collector` | 6.0.4 | 10.1.0 |
| Tests | `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.10.1 |
| Tests | `Moq` | 4.20.70 | 4.21.0 |
| Tests | `xunit.runner.visualstudio` | 3.1.4 | 4.0.0 |

Latest major versions are not automatically appropriate for a .NET 8 application. The compatible-minor audit separately reported `Microsoft.Data.Sqlite` 8.0.31, DI 8.0.1, and the same minor/patch updates for several `10.0.x` packages. Any upgrade requires its own review and test cycle.

No NuGet lock files or central package-management file are present, so restore resolution is not locked at repository level.

## Security-sensitive network and IPC inventory

### Inbound/local listeners

| Component | Binding/default | Purpose and current control |
|---|---|---|
| Single-instance named pipe | `AxePower.Desktop.Singleton.Pipe` | Local process activation/argument forwarding; created without explicit `PipeSecurity` in `App.xaml.cs` |
| OAuth loopback | `127.0.0.1:53682` | One-shot browser callback; loopback-only; OAuth uses PKCE |
| LAN Drop / Phone portal | `0.0.0.0:5050` by default | Raw HTTP server; requires portal token; offers file upload/download, clipboard text, media actions, music controls, and workstation lock |
| Share viewer host | `0.0.0.0:8890` or next available | Raw HTTP and WebSocket; session path/token gates viewing/chat/voice, separate eight-character code gates optional remote input |
| MediaMTX WebRTC | TCP `0.0.0.0:8889`, UDP `0.0.0.0:9189` by default | Viewer media transport; generated config disables HTTPS for WHEP/WebRTC signaling (`webrtcEncryption: no`), allows every origin, and grants anonymous read access to the unguessable session path. WebRTC media encryption is protocol-level and is distinct from this signaling setting. |
| MediaMTX control API | `127.0.0.1:9997` or next available | Loopback-only control/status API |
| MediaMTX RTSP ingest | `127.0.0.1:8554` | Loopback-only publisher ingest with a per-room publisher token |

### Outbound and hosted services

- `Friends/RealtimeFeed` is a WebSocket **client** to Supabase Realtime over `wss://`; it is not a local listener.
- Account, profile, and Friends APIs call the configured Supabase HTTPS project using its public publishable key plus user bearer tokens.
- `supabase/functions/delete-account` and `supabase/functions/steam-login` are cloud-side `Deno.serve` handlers. They are not desktop listeners.
- The application also makes outbound HTTPS requests to GitHub releases, Steam, music/lyrics providers, game-art sources, vendor driver pages, WinGet sources, and tool download sources.

### Listener-related risks observed

1. `LanDropService` binds every interface and uses plain HTTP. Its authenticated upload/text/action handlers trust declared `Content-Length`; file uploads are buffered fully into a `MemoryStream` without an explicit maximum before being written. This permits memory/disk exhaustion after token compromise or by an authorized but malfunctioning client.
2. Share viewer/control uses `http://` and `ws://`; viewer/control credentials and content do not receive TLS transport protection from the app itself.
3. MediaMTX serves WHEP/WebRTC signaling without HTTPS and allows every origin. WebRTC media is normally protected by its protocol, but access signaling still relies on the secrecy of the room path/token rather than TLS and an origin allow-list.
4. The eight-character remote-control code has no observed server-side attempt rate limit.
5. LAN portal and Share Screen have automated loopback/unit coverage, but the repository's live verification ledger says the phone portal and several real Share Screen paths have not been verified end to end on separate devices.

## Elevation paths

| Path | Behavior |
|---|---|
| Application manifest | `asInvoker`; normal launch does not inherently require administrator rights |
| Optional whole-app elevation | `ElevationService.TryRelaunchElevated` launches the current executable with shell verb `runas` when the saved setting requests it |
| Startup task | `StartupService` registers a scheduled task as `HighestAvailable` when “run as administrator” is enabled, permitting elevated logon startup without a new UAC prompt |
| Debloat tools | `DebloatService` launches PowerShell with `runas`; current commands download and immediately execute Win11Debloat/WinUtil scripts through `irm`/`iex` or `ScriptBlock.Create` |
| System service optimizer | Uses a `runas` process path for privileged service operations |
| Installer | Inno Setup declares `PrivilegesRequired=admin`; it can kill running AxePower processes, uninstall previous versions, delete leftover install directories, and launch the runtime bootstrap PowerShell script with `ExecutionPolicy Bypass` |
| Already-elevated operations | Hardware drivers/sensors, restore points, registry/service/update changes, some uninstaller cleanup, and other system operations depend on the process already holding administrator rights |

The optional elevation architecture is deliberate and safer than an always-admin manifest, but running the whole application elevated increases the impact of any command, WebView, parser, downloaded-tool, or network-listener flaw during that session.

## Update and downloaded-code execution paths

1. **AxePower self-update:** `UpdateService` queries two GitHub latest-release endpoints, downloads the selected asset to `%TEMP%\AxePower_Update`, checks length and the two-byte `MZ` header, then launches it with `Process.Start`. It does not currently verify SHA-256, a signed manifest, Authenticode status/publisher, an explicit download-host allow-list, or downgrade provenance.
2. **.NET Desktop Runtime bootstrap:** `Installer/install-dotnet.ps1` downloads Microsoft's redirect URL and checks only the `MZ` header before executing the installer. It does not validate Authenticode publisher or a pinned hash.
3. **Share Screen tools:** `ShareToolProvider` downloads FFmpeg from gyan.dev and MediaMTX from the latest GitHub release, extracts them, and later executes the binaries. No checksum/signature verification was found.
4. **Android control:** `AndroidControlService` downloads and extracts the latest `Genymobile/scrcpy` release and launches tools from the extracted directory. No checksum/signature verification was found.
5. **Graphics drivers:** `GraphicsDriverService` downloads vendor installers and executes them. PE/MZ-style validation is present in paths inspected, but expected publisher/signature verification was not found.
6. **MSI Afterburner/tool acquisition:** uses WinGet and PowerShell download/extraction paths before launching acquired executables.
7. **Debloat:** two UI actions fetch network script text and execute it immediately in elevated PowerShell rather than pinning and reviewing a versioned script.
8. **Software Hub:** can install, upgrade, and uninstall packages with WinGet/PowerShell and can open direct third-party download pages.

### Tracked release artifact status

Nineteen historical installers are tracked under `Installer/Output`. Every one returned `NotSigned` from `Get-AuthenticodeSignature`.

Latest tracked installer checked:

```text
File: Installer/Output/AxePower-v2.1.0-Setup.exe
Length: 19,938,174 bytes
Authenticode: NotSigned
SHA-256: 39F35C02492C975E7197D9955751AD169439E1F639A6C45AD8A04D2AAD626EC6
```

The freshly built Release application host was also unsigned:

```text
File: AxePower.App/bin/Release/net8.0-windows10.0.19041.0/AxePower.exe
Length: 155,648 bytes
Authenticode: NotSigned
SHA-256: 578E113A0A069129B9F543E86DCE5FDA7A66D3FCAE0ADDCFE8CA1F7AB4558E92
```

No Inno Setup `SignTool`, Authenticode verification, signed-update-manifest, SBOM, or release checksum pipeline was found.

## Destructive and system-changing operation inventory

This is a capability inventory, not a finding that each operation is unsafe.

### Registry and Windows policy

- `TweakService`, `WindowsUpdateShieldService`, and `WindowsUpdateSystem` create/update/delete registry values and policies.
- `GameModeGpuService` changes and restores per-application GPU preferences.
- The uninstaller can delete registry keys/values after classification and optional backup.
- Registry backup/restore services export and restore selected keys/values.

### Files and directories

- Uninstaller cleanup deletes or recycles application leftovers and maintains backup manifests/history.
- LAN Drop/Phone Hub UI can delete received files.
- Update, driver, Android, Share Screen tool, music cache/art, log rotation, Windows Update reset, and installer paths create, replace, and delete files/directories.
- Inno Setup removes stale application files and previous install directories during upgrade.

### Processes and applications

- The uninstaller launches Win32/MSI/AppX/WinGet uninstallers and can terminate related process trees.
- `AppKillerService`, Game Mode/routines, the terminal, system doctor, encoders, MediaMTX, and single-instance cleanup can stop, suspend, resume, or kill processes.
- Software Hub installs/upgrades/uninstalls packages.
- Debloat and tool workflows can execute third-party code.

### System configuration

- Power plan and timeout services call `powercfg` and change active plan/settings.
- DNS optimizer changes adapter DNS configuration and flushes DNS.
- Windows Update repair stops/starts services and resets update cache/state.
- Startup service creates/removes scheduled tasks.
- Service optimizers alter Windows service state/startup behavior.
- Restore-point services enable System Restore, change its throttle policy, and create checkpoints.
- Power timer can initiate shutdown/restart/sleep/hibernate/lock operations.
- Account deletion invokes a Supabase Edge Function that deletes the authenticated user and cascades related backend data.

Safety mechanisms already present include dry-run modes, protected-item classification, confirmation prompts, backup manifests, restore support, operation results, and tests. Coverage and live verification are not uniform across all operations.

## Current risks and gaps

### High priority

1. **Known vulnerable native SQLite dependency:** high-severity advisory `GHSA-2m69-gcr7-jv3q` affects resolved `SQLitePCLRaw.lib.e_sqlite3` 2.1.6.
2. **Downloaded executable/update integrity:** multiple paths execute downloaded installers/tools after transport and format checks without cryptographic content or expected-publisher verification.
3. **Elevated remote-script execution:** Debloat actions download current network content and execute it elevated, so upstream or delivery compromise becomes privileged code execution.

### Medium priority

4. **Unsigned release artifacts:** all tracked installer binaries are unsigned, weakening publisher identity and update trust.
5. **LAN resource limits:** LAN Drop buffers uploads in memory and lacks explicit body/storage/concurrency limits in the inspected server path.
6. **Plain local-network transports:** LAN Drop and the viewer/control channels use HTTP/ws; MediaMTX signaling is HTTP and all WebRTC origins are accepted.
7. **No enforced CI/release gate:** no `.github` workflow, package lock, central build policy, automated signing check, SBOM, or provenance generation is present.
8. **Moderate coverage:** half of instrumented lines and roughly two-fifths of branches ran; several native, network, tool-download, and UI workflows remain unverified.
9. **Self-test side effect:** the documented read-only self-test may persist a portal secret.

### Engineering and trust debt

10. The Core project globally suppresses nullable warnings `CS8600`, `CS8602`, and `CS8604`.
11. A fresh rebuild emits two distinct warnings.
12. No root `LICENSE`, `SECURITY.md`, `CONTRIBUTING.md`, `.editorconfig`, `Directory.Build.props`, or package lock is present, although public pages claim the MIT license.
13. README's absolute “100% Local” / “zero remote data collection” headline is broader than the actual optional accounts, update, provider, tool-download, and peer-network behavior described elsewhere.
14. Several Music, portal, and Share Screen flows remain explicitly marked unverified in `skillsMD/MUSIC_FIX_PLAN.md` despite compiling and passing fake/loopback tests.

## Baseline limitations

- No application code or configuration was fixed in this step.
- The WPF UI was not launched or visually compared with references; the app may relaunch elevated and visual correctness cannot be inferred from compilation.
- The application self-test was not run because inspection found a potential write to real portal-secret storage.
- The installer was not rebuilt, installed, upgraded, repaired, or uninstalled in a clean VM.
- A normal Release-build host and the tracked installers were checked for signatures; a clean `dotnet publish`/installer compilation was not performed.
- The live Supabase deployment, OAuth providers, Steam API, GitHub releases, WebRTC across devices, LAN portal on a phone, music providers, WinGet, driver vendors, and remote scripts were not exercised.
- The website has no automated browser/accessibility/link test in the repository and was not deployed or rendered.
- This was not a penetration test, fuzz test, malware analysis, legal review, or independent audit.

## Reproduction notes

For a clean clone, restore before using the exact build command:

```powershell
dotnet restore AxePower.sln
dotnet build AxePower.sln -c Release --no-restore -t:Rebuild
dotnet test AxePower.Tests\AxePower.Tests.csproj -c Release --no-build --no-restore
```

Registry and DPAPI tests require normal current-user Windows access. Run them only where creation and cleanup of isolated `HKCU\Software\AxePower.Tests\<guid>` keys is permitted. Snapshot `%LOCALAPPDATA%\AxePower\settings.json` before the test and compare length/hash afterward.

Dependency results are time-sensitive and require access to NuGet. Supabase tests require a temporary install of `@electric-sql/pglite`; `node_modules` should not be committed.

## Exit-criteria evidence

- **Release build documented:** PASS — Release rebuild succeeded with 0 errors and the two distinct warnings listed above.
- **Test totals and skips documented:** PASS — 1,120/1,121 .NET tests passed, one intentionally destructive live uninstall test skipped; Supabase 48/48 passed.
- **Coverage and dependency audit documented:** PASS — project/overall line and branch coverage plus vulnerable, deprecated, and outdated package results are recorded.
- **Tracked product distinguished from untracked references/prototypes:** PASS — included and excluded scopes and the initial dirty worktree are explicitly listed.
