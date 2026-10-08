# AxePower security assessment brief

**For:** an independent security tester engaged by the owner. Everything needed to test is here
or linked; nothing depends on talking to the developers.
**Status:** no third-party assessment has taken place. AxePower makes no claim of being audited
or certified. Findings go in [REVIEW_FINDINGS.md](REVIEW_FINDINGS.md).

## Authorisation and boundaries

Testing is authorised only within the written engagement the owner signs. Unless that engagement
says otherwise, these defaults apply.

**In scope:**

- the AxePower 2.1.2 release candidate (installer, application, update mechanism) on machines and
  networks the tester controls;
- the source in this repository;
- a separate Supabase project the owner provides for testing.

**Out of scope:**

- AxePower's production Supabase project (`kjnqsgdlcytpooueymdt`) and other users' accounts;
- GitHub, YouTube, Steam, Google, Discord, Cloudflare, NVIDIA, Microsoft and every other third
  party;
- the AxePower website's hosting;
- denial-of-service and load testing against anything not on the tester's own machines;
- social engineering;
- AxePower Mobile (a separate product).

**Rules:**

- **Data:** use test accounts only, and do not keep or share any real user data you come across.
- **Disclosure:** report as described in [SECURITY.md](../../SECURITY.md).

## What AxePower is, security-wise

- **The app:** a WPF .NET 8 desktop application that usually runs elevated (administrator), by
  user choice.
- **It changes Windows:**
  - power plans, services, registry, scheduled tasks, DNS and Windows Update policy;
  - it installs and removes software;
  - it deletes files.
- **It runs network listeners only on request:**
  - the LAN Drop / Phone Hub HTTP server (port 5050);
  - the Share Screen room server (WebSocket, HTTP(S)) and a MediaMTX WebRTC server;
  - a one-shot OAuth callback on 127.0.0.1.
- **It talks to:**
  - Supabase (optional accounts, Friends);
  - GitHub (updates, tools);
  - YouTube (Music);
  - lyrics sites, Steam's image CDN, and NVIDIA/AMD driver sites;
  - WinGet.

## Architecture and threat models

- **Architecture:**
  - [docs/engineering/ARCHITECTURE.md](../engineering/ARCHITECTURE.md): projects, services and the
    process model.
  - Trust & Permissions page (in the app): the live inventory of listeners, connections and data
    stores. Its source is `AxePower.Core/Services/Trust/TrustInventory.cs`, kept complete by
    `TrustInventoryTests`.
- **Threat models:**
  - [SHARE_SCREEN_THREAT_MODEL.md](../security/SHARE_SCREEN_THREAT_MODEL.md)
  - [REMOTE_ACCESS.md](../security/REMOTE_ACCESS.md): a known High, with the feature disabled in
    code.
  - [SIGNED_UPDATES.md](../security/SIGNED_UPDATES.md) and
    [CODE_SIGNING.md](../security/CODE_SIGNING.md)
  - [RUNTIME_BOOTSTRAP.md](../security/RUNTIME_BOOTSTRAP.md)
  - [PRIVACY.md](../security/PRIVACY.md): every data flow.
- **The self-review:** [FINAL_SECURITY_REVIEW.md](../security/FINAL_SECURITY_REVIEW.md), with
  findings, fixes and accepted risks. Please try to break its "reviewed without findings" claims.

## Build provenance

- **Source:** branch `2.1.2-industry-standards-followup`; the exact commit is in the release
  candidate report.
- **Build:** `scripts/package-release.ps1`, a locked restore and a deterministic build.
- **Outputs:**
  - `SHA256SUMS`;
  - `sbom.cdx.json` (CycloneDX 1.5);
  - `provenance.json`;
  - a signed `update-manifest.json` (ECDSA P-256 key pinned in the app).
- **Signing:** releases are **not** Authenticode-signed yet (FINAL_SECURITY_REVIEW H1).
- **CI:** [.github/workflows/ci.yml](../../.github/workflows/ci.yml) runs a locked restore, build,
  tests with coverage, a dependency audit and TruffleHog.

## Test evidence available

- **Tests:** about 1,900 automated tests in `AxePower.Tests`. Security-relevant suites include:
  - `LanDrop*Tests`, including fuzzing and abuse tests (`Fuzzing/`);
  - `ShareScreen/*`;
  - `UpdateSecurityTests` and `UpdateSigningSetupTests`;
  - `Trust/*`;
  - `Operations/*` (change journal and rollback);
  - `Uninstaller/EndToEndSafetyValidationTests` and `ProtectionServiceTests`;
  - `Outcomes/OutcomeTests` (no raw errors or secrets in the UI);
  - `FirstRunTests`;
  - `PrivacyClaimsTests`.
- **Supabase:** policies are in `supabase/migrations/`, with tests in `supabase/tests/`.

## Suggested focus

1. **Privilege boundaries.**
   - Can a standard-user process influence what AxePower does elevated? For example: settings
     files, `%TEMP%` (see M1), the Software Hub allow-list, scheduled tasks, the single-instance
     pipe, and the Terminal.
2. **Destructive operations.**
   - Can uninstaller leftover cleanup or the cache cleaners be steered into deleting outside their
     targets (junctions, symlinks, crafted registry entries)?
3. **LAN Drop.**
   - Authentication, session handling, path traversal in uploads and downloads, size limits,
     lockout, behaviour on Public networks.
4. **Share Screen.**
   - The room handshake (nonce/HMAC), Origin and Host checks, end-to-end encryption of chat, voice
     and control, remote-input approval, WebView2 confinement (M2).
5. **Updates.**
   - Manifest signature bypass, downgrade, the hash and size checks, the launch-time re-check.
6. **Accounts and Supabase (test project only).**
   - RLS on profiles, friendships, presence and invites.
   - `security definer` functions.
   - The `steam-login` and `delete-account` functions.
   - PKCE and the loopback callback.
   - Token storage (DPAPI).
7. **Third-party execution.**
   - Debloat's `irm | iex` (accepted risk M3), and WinGet invocation.
8. **Logs and diagnostics bundles.**
   - Anything secret or personal surviving `LogRedactor`.

## Known limitations

Don't report these as new, but do report anything worse than described:

- **H1:** unsigned installer and updates.
- **M3:** Debloat runs unpinned third-party scripts as administrator after a high-risk
  confirmation.
- **M4:** LAN Drop is plain HTTP on Private networks.
- **Low:** L1, L2, L4–L6 in FINAL_SECURITY_REVIEW.
- **Remote access:** the cloud bridge is disabled in code; its known problem is described in
  REMOTE_ACCESS.md.

## Reporting

For each finding, give:

- title;
- severity (CVSS 3.1 vector, plus the tester's own rating);
- affected component and version;
- prerequisites;
- steps to reproduce;
- impact;
- evidence;
- suggested remediation.

Send findings privately as in SECURITY.md. The owner records them in REVIEW_FINDINGS.md, and turns
confirmed ones into tracked fixes.
