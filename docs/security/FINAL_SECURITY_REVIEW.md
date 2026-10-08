# Final security review

**For:** the owner deciding on release, and any outside reviewer.
**Reviewed:** 2026-10-08, at commit `5167682` (branch `2.1.2-industry-standards-followup`), before
any change. Fixes made after the review are listed per finding and in "Changes made by this review".
**Scope:** the tracked product only: `AxePower.App`, `AxePower.Core`, `AxePower.UpdateSigning`,
`Installer/`, `scripts/`, `supabase/`, and the website's privacy pages. Not in scope: AxePower
Mobile, and the reference clones (`LenovoLegionToolkit`, `lenovo_vantage`, `winutil`,
`Smart-Disk-Analyser`).
**Method:** code reading with targeted searches, the existing security test suites, a NuGet
vulnerability scan and a secret scan. This is a self-review, not an independent audit (see Prompt
34's reviewer packages).

## Summary

| Severity | Open for release | Fixed by this review | Accepted with owner and date |
|---|---|---|---|
| Critical | 0 | 0 | 0 |
| High | **1** (H1, unsigned installer and updates; needs the owner's purchase) | 0 | 0 |
| Medium | 0 | 2 (M1, M2) | 2 (M3, M4) |
| Low | 0 | 1 (L3) | 5 |

One earlier High (remote access over public Supabase channels, found in Prompt 25) is closed for
this release: the feature is turned off in code (`CloudBridgeService.IsAvailable = false`, with a
test). It must not come back before the fix plan in [REMOTE_ACCESS.md](REMOTE_ACCESS.md) is done.

## Findings

### H1 — Production installer and updates are not Authenticode-signed (High, open)
- **Area:** update and install signature enforcement.
- **Prerequisites:**
  - **Install:** an attacker who can tamper with or replace the installer before the user runs it,
    such as a mirror or a compromised download page.
  - **Update:** after Prompt 9 the update manifest is signed with a pinned key and the installer's
    size and SHA-256 are checked, so an update attack needs the signing key.
  - **SmartScreen:** Windows shows "Unknown publisher" for every release, which trains users to
    click through.
- **Evidence:**
  - `docs/security/CODE_SIGNING.md`.
  - `UpdateSecurityOptions.RequireAuthenticode` is false for unsigned releases.
  - Owner decision D1 (2026-10-06): "unsigned for now".
  - `Installer/AxePower.iss` signs only when `SignToolCommand` is defined.
- **Remediation:** buy an OV/EV code-signing certificate. Then:
  - set `SignToolCommand` in the release pipeline;
  - turn on `RequireAuthenticode` with the publisher name.

  The plumbing exists and is tested.
- **Residual risk:** none, once signed.
- **Owner:** the project owner (purchase). Prompt 35 treats an unsigned production installer as an
  automatic NO-GO.

### M1 — Update installer could be swapped between verification and launch (Medium, fixed)
- **Area:** update and install signature enforcement, and elevation.
- **Prerequisites:** a program already running as the same Windows user, which replaces
  `%TEMP%\AxePower_Update\<installer>` after the download is verified and before the user clicks
  Install. The user then approves the administrator prompt for the swapped file. That prompt looks
  the same, because releases are unsigned (H1). The swap turns user-level code into administrator
  code with one routine click.
- **Evidence:** `UpdateService.ApplyUpdate` re-checked Authenticode only when it was required
  (H1), so an unsigned release was launched without any check.
- **Fixed:**
  - `UpdateService` remembers the SHA-256 of every installer it verified.
  - `ApplyUpdate` opens the file denying writes and deletes, hashes it, and refuses to launch on a
    mismatch or an unknown file.
  - It keeps that handle open until the installer process has started, so the file cannot change
    in between.
  - Test: `UpdateSecurityTests.ApplyUpdate_RefusesAnInstallerChangedAfterVerification` and
    `…_RefusesAnInstallerItNeverVerified`.
- **Residual risk:** same-user malware can still do anything a user can do. The fix only removes
  the free route to administrator rights.

### M2 — Share Screen viewer page could navigate anywhere inside AxePower (Medium, fixed)
- **Area:** WebView2 navigation and script-injection boundaries.
- **Prerequisites:** the user joins a room or watch link from a hostile host. The host's page,
  shown in AxePower's embedded browser, can then navigate the view to any website or open
  pop-ups. It could show a convincing fake sign-in form "inside AxePower".
- **Evidence:** `ShareScreenView.xaml.cs` set no `NavigationStarting` or `NewWindowRequested`
  handlers. Dev tools and context menus were already off. There is no host object or
  `WebMessageReceived` bridge, so the page never had access to native code. The injected scripts
  are fixed text, and the one value they carry is JSON-encoded.
- **Fixed:**
  - Navigation is limited to the origin AxePower opened, plus `about:blank`
    (`WebViewNavigationPolicy`).
  - Pop-ups are blocked.
  - Script dialogs are off.
  - Test: `WebViewNavigationPolicyTests`.
- **Residual risk:** the host still controls the page content of its own room, which is the
  point of watching a stream.

### M3 — Debloat runs third-party scripts from the internet as administrator, unpinned (Medium, accepted)
- **Area:** elevation and command execution.
- **Prerequisites:** a compromise of `debloat.raphi.re` or `christitus.com` at the moment the
  user runs that tool.
- **Evidence:**
  - `DebloatService` runs `irm <url> | iex` with `Verb = runas`.
  - The confirmation standard (Prompt 27) shows the exact command, marks it high risk and
    irreversible, and needs a tick before the named button works.
- **Why accepted:** it is the publisher-documented way to run these tools, the user asks for that
  specific tool, and the command is disclosed before it runs.
- **Remediation, next:** run a pinned GitHub release of Win11Debloat, checked against a known
  SHA-256, in place of `irm | iex`. WinUtil publishes signed releases.
- **Owner:** project owner. **Review by:** 2027-01-08.

### M4 — LAN Drop is plain HTTP on the local network (Medium, accepted)
- **Area:** LAN authentication and encryption.
- **Prerequisites:** an attacker on the same Wi-Fi who can sniff traffic while the server runs.
  They capture the access token and the files sent.
- **Evidence:**
  - The server runs only when started.
  - Public networks are refused unless allowed for that session.
  - Five wrong tries lock an address out.
  - The access link can be rotated (Prompts 12, 13 and 25; `LanDropSecurityTests`).
  - The open decision is D4 in `OWNER_CHECKS.md`, entry P4 in `MUSIC_FIX_PLAN.md`.
- **Why accepted:** it works only on networks the user marked Private; it is disclosed in
  PRIVACY.md and Trust & Permissions; and browsers on phones cannot trust a self-signed LAN
  certificate without friction.
- **Owner:** project owner (decision D4). **Review by:** 2027-01-08.

### Low findings

| Id | Finding | Status |
|---|---|---|
| L1 | Friends' realtime feed joins a shared public topic (`realtime:axepower-friends`) with broadcast and presence on. Row data still follows RLS per subscriber, and the client acts only on `postgres_changes`, so others' broadcasts are ignored. | Accepted; move to a private channel with the remote-access work. Owner; 2027-01-08 |
| L2 | The installer adds an inbound firewall rule for port 5050 (Private and Domain) at install, before LAN Drop is ever used. Nothing listens until the user starts the server. | Accepted; a future version can add the rule on first start instead. Owner; 2027-01-08 |
| L3 | `Installer/AxePowerSetup.iss` (a second, legacy script; the pipeline uses `AxePower.iss`) launched AxePower after install without `runasoriginaluser`, so it would start elevated, possibly under another account. | **Fixed:** flag added |
| L4 | LAN Drop does not validate the `Host` header. A DNS-rebinding page could reach the server but has no access token, so every request is refused. | Accepted. Owner; 2027-01-08 |
| L5 | The Terminal page runs whatever the user types, elevated when AxePower is. | By design; disclosed on the page |
| L6 | Supabase limits in `supabase/config.toml` apply to local development. The production project's Auth rate limits, and that its tables match the migrations, are set in the dashboard and were not verified here. | Owner check: compare the dashboard with the migrations. Owner; before release |

## Areas reviewed without findings

- **Trust boundaries and threat models.** Covered by:
  - `docs/engineering/ARCHITECTURE.md`;
  - `SHARE_SCREEN_THREAT_MODEL.md`;
  - `REMOTE_ACCESS.md`;
  - Trust & Permissions' inventory, which lists every listener, connection and data store and is
    kept complete by `TrustInventoryTests`.
- **Update verification.**
  - Signed manifest with a pinned key, plus size and SHA-256 (`SIGNED_UPDATES.md`,
    `UpdateSecurityTests`).
  - The .NET runtime bootstrapper checks Microsoft's Authenticode signature and an optional
    pinned hash (`RUNTIME_BOOTSTRAP.md`).
- **Command execution.**
  - Software Hub runs only allow-listed executables (`ExecutableAllowList`,
    `AllowListedProcessRunner`, Prompt 19).
  - Destructive actions go through the confirmation standard and the operation journal
    (Prompts 24 and 27).
- **Deletion containment.**
  - Leftover cleanup asks `ProtectionService.GetBlockReason` again when deleting, not only when
    scanning (`LeftoverCleanupService` line 135).
  - It backs up registry items and tasks first, and verifies afterwards
    (`EndToEndSafetyValidationTests`, `ProtectionServiceTests`).
- **Share Screen.**
  - Chat, voice and control are end-to-end encrypted.
  - The viewer handshake uses a single-use nonce with HMAC, plus Origin and Host checks
    (`ShareViewerHost`, Prompts 14 and 18).
- **OAuth and tokens.**
  - PKCE S256 on every provider; the loopback listener on 127.0.0.1 accepts one callback.
  - Tokens are stored with DPAPI for the current user (`AccountSessionStore`).
  - Steam sign-in verifies Steam's reply with `check_authentication`, the OP endpoint and a
    challenge read from the signed `return_to`.
- **Supabase.**
  - Every table has RLS, and anon access is revoked.
  - Every `security definer` function sets `search_path`.
  - Functions are granted to `authenticated` only.
  - Abuse caps: 100 pending friend requests, 30 invites an hour, invites expire.
  - `delete-account` verifies the caller's token.
- **Secrets and logs.**
  - No secrets in tracked files; only the public publishable key.
  - The update-signing private key was never committed (git history checked).
  - Every log line and Details text passes through `LogRedactor`.
- **Dependencies.** `dotnet list package --vulnerable --include-transitive`: no vulnerable
  packages in any project (2026-10-08). CI repeats this (`CI.md`).
- **Privacy-policy accuracy.**
  - PRIVACY.md, Trust & Permissions and the in-app and website privacy pages agree
    (`PrivacyClaimsTests`).
  - Prompt 29 added the latency and DNS-benchmark pings, and widened the inventory test so a ping
    cannot be missed again.

## Changes made by this review

1. **M1:** `UpdateService` re-verifies the installer's SHA-256 under a no-write lock at launch.
2. **M2:** Share Screen's WebView2 is held to its own origin; pop-ups and dialogs are blocked.
3. **L3:** `AxePowerSetup.iss` launches AxePower as the original user.
4. **Found while fixing M1:** About › Install update closed AxePower even when the installer was
   refused, leaving the user with no app and no update. It now closes only once the installer is
   running, and otherwise says why.

Items 1–3 each come with a regression test (`UpdateSecurityTests`,
`WebViewNavigationPolicyTests`), and the full suite passes.
