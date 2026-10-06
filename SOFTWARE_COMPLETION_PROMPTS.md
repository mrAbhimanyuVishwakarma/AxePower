# AxePower Software Completion Prompt Playbook

This document is a sequential set of prompts for bringing AxePower from its current state to a production-ready Windows utility. Use **one prompt at a time, in order**. Do not start the next prompt until the current prompt's exit criteria pass.

The prompts assume the repository root is `D:\Projects\AxePower` and the primary product is the tracked desktop solution (`AxePower.sln`), installer, website, and Supabase backend. Untracked reference repositories and prototypes are out of scope unless a prompt explicitly includes them.

## Rules for every prompt

Prepend or retain these rules whenever a prompt is handed to a coding agent:

```text
Work in D:\Projects\AxePower.

Read CLAUDE.md and any applicable AGENTS.md before changing files. Preserve all unrelated user changes and untracked files. Do not edit vendored/reference projects such as winutil, LenovoLegionToolkit, lenovo_vantage, Smart-Disk-Analyser, Social Media/skills_library, or AxePower Mobile unless this prompt explicitly asks for it.

Inspect the existing implementation before proposing changes. Prefer small, reviewable changes that preserve current behavior. Do not commit, push, publish, create a release, or use real credentials unless explicitly asked.

For implementation tasks:
- Add or update tests for the behavior changed.
- Run `dotnet build AxePower.sln -c Release`.
- Run `dotnet test AxePower.Tests/AxePower.Tests.csproj -c Release`.
- Report files changed, tests run, results, residual risks, and any manual verification still required.
- If a test needs HKCU or Windows DPAPI, run it with normal current-user access and use isolated test keys/directories that are cleaned afterward.
- Never weaken a security check merely to make a test pass.
```

---

## Phase 1 — Establish a trustworthy baseline

### Prompt 1 — Baseline and scope report

```text
Audit the current tracked AxePower desktop product without changing application behavior. Establish a reproducible baseline for the solution, installer, website, and Supabase backend.

Do the following:
1. Record the current git status without modifying unrelated changes.
2. Build Release and run the complete test suite.
3. Collect line and branch coverage, summarize it by project, and remove temporary coverage output afterward.
4. Run NuGet vulnerable, deprecated, and outdated package checks.
5. List build warnings, skipped tests, security-sensitive network listeners, elevation paths, update execution paths, and destructive system operations.
6. Create `docs/engineering/BASELINE.md` containing commands, results, current risks, and a date/commit identifier.
7. Do not fix issues in this step.

Exit criteria:
- Release build result is documented.
- Test totals and skipped tests are documented.
- Coverage and dependency audit results are documented.
- The report clearly distinguishes tracked product code from untracked reference/prototype directories.
```

### Prompt 2 — Remove the known vulnerable dependency

```text
Remediate the current high-severity SQLite transitive dependency vulnerability GHSA-2m69-gcr7-jv3q.

Start by confirming the currently resolved dependency chain. Upgrade `Microsoft.Data.Sqlite` within the .NET 8 servicing line to the newest compatible secure version (the audit previously identified 8.0.31, but verify the current available version). Avoid unrelated major-version upgrades.

After updating:
1. Restore packages.
2. Build and run all tests.
3. Run `dotnet list AxePower.sln package --vulnerable --include-transitive` and prove the advisory is gone.
4. Record the dependency change and validation in `docs/engineering/DEPENDENCIES.md`.

Exit criteria:
- No high-severity SQLite advisory remains.
- All tests pass.
- The resolved secure versions are documented.
```

### Prompt 3 — Repository standards and governance

```text
Add the minimum repository governance files expected for a production open-source Windows application without changing runtime behavior.

Implement:
1. A root MIT `LICENSE` matching the project's existing MIT claims and correct copyright owner/year.
2. `SECURITY.md` with supported versions, private vulnerability-reporting instructions, expected response windows, disclosure process, and a warning not to post exploitable reports publicly.
3. `CONTRIBUTING.md` with build/test commands, branch/PR expectations, safety rules for Windows registry/system tests, and release boundaries.
4. A root `.editorconfig` for C#, XAML, JSON, SQL, Markdown, and PowerShell with conservative formatting rules.
5. `CODE_OF_CONDUCT.md` only if the project owner confirms that it is wanted; otherwise document the decision.
6. Reconcile README and website links so license and security documents are discoverable.

Do not invent an email address or legal entity. If the correct security contact or copyright holder cannot be derived from the repository, stop and request that specific information.

Exit criteria:
- The actual license file matches public claims.
- Security reporting is discoverable.
- Contributor verification commands work.
```

### Prompt 4 — Centralize build and version policy

```text
Eliminate version drift and establish centralized .NET build policy.

Inspect all current version sources, including the app and core project files, installer definitions, updater fallback, release documentation, and generated file metadata.

Implement:
1. `Directory.Build.props` for shared nullable, language, deterministic-build, analyzer, and version settings.
2. A single authoritative product version that both .NET projects and the installer consume where technically practical.
3. Removal or correction of the stale updater fallback version.
4. A build-time validation that fails if installer and application versions disagree.
5. Documentation for the exact version-bump procedure.

Do not bump the public product version as part of this task unless explicitly instructed.

Exit criteria:
- One intentional version change updates every produced artifact.
- A deliberate mismatch is caught automatically.
- Build and tests pass.
```

---

## Phase 2 — Automate quality and supply-chain checks

### Prompt 5 — Continuous integration

```text
Create a Windows CI workflow for AxePower using GitHub Actions. Do not add release publication or signing yet.

The workflow must:
1. Trigger on pull requests and pushes to maintained branches.
2. Use a pinned supported Windows runner and .NET SDK.
3. Restore with package lock enforcement where practical.
4. Build `AxePower.sln` in Release.
5. Run the complete test suite, including HKCU/DPAPI tests in isolated locations.
6. Collect coverage and publish test/coverage artifacts.
7. Run NuGet vulnerability and deprecated-package checks and fail on high/critical vulnerabilities.
8. Run secret scanning and static analysis using maintained, pinned actions.
9. Use least-privilege workflow permissions and no repository secrets.
10. Document locally equivalent commands.

Exit criteria:
- The workflow YAML is syntax-valid.
- It has least-privilege permissions.
- Local equivalents pass.
- No signing secrets or broad write permissions are introduced.
```

### Prompt 6 — Package locking and dependency policy

```text
Make dependency restoration deterministic and define an update policy.

Implement NuGet lock files for the production and test projects, enable locked restore in CI, and document how intentional dependency updates refresh the locks. Add a lightweight scheduled dependency-update mechanism, but do not automatically merge updates.

Ensure CI distinguishes:
- high/critical vulnerability failures;
- normal outdated packages;
- major-version upgrades requiring manual review.

Exit criteria:
- Clean checkout restore is deterministic.
- CI fails if lock files unexpectedly drift.
- Dependency update instructions are documented.
```

### Prompt 7 — Static analysis without hiding problems

```text
Introduce production-appropriate .NET analyzers and create a staged warning policy.

Start by inventorying existing warnings and global suppressions. Pay special attention to the current suppression of CS8600, CS8602, and CS8604 in AxePower.Core.

Implement:
1. Recommended Microsoft/.NET analyzers with a conservative initial severity.
2. Warnings-as-errors in CI for newly introduced warnings.
3. Narrow, documented suppressions only where a warning is demonstrably incorrect.
4. A tracked cleanup ledger for existing warnings that cannot safely be resolved in this task.

Do not mass-add null-forgiving operators or blanket `NoWarn` entries.

Exit criteria:
- No new broad suppressions exist.
- CI prevents warning regression.
- Existing debt is measured and documented.
```

---

## Phase 3 — Secure installation and updates

### Prompt 8 — Code-signing architecture

```text
Design and implement the code-signing pipeline structure for AxePower without placing certificates, passwords, tokens, or private keys in the repository.

Cover:
1. Authenticode signing for AxePower.exe, shipped DLLs where appropriate, the Inno Setup installer, and uninstall executable.
2. SHA-256 timestamping with a configurable RFC 3161 timestamp server.
3. Local developer behavior when no certificate is available.
4. CI/release secret boundaries and least-privilege access.
5. Verification commands using `Get-AuthenticodeSignature` and `signtool verify`.
6. A release gate that refuses unsigned or invalid production artifacts.

Add scripts/configuration and documentation, but do not fabricate a certificate. Clearly list the one external action the owner must take to acquire and configure a trusted signing identity.

Exit criteria:
- Signing can be enabled entirely through secure external configuration.
- Production release mode fails closed if signing is unavailable or invalid.
- No secret material is committed.
```

### Prompt 9 — Cryptographically verify application updates

```text
Harden AxePower's updater so an executable is never trusted merely because it starts with an MZ header.

Implement a signed-update design that:
1. Accepts downloads only from explicitly allow-listed HTTPS GitHub release hosts and repositories.
2. Downloads a signed release manifest containing version, asset name, byte length, and SHA-256.
3. Verifies the manifest signature against a public key pinned in the application.
4. Verifies asset length and SHA-256.
5. Verifies the installer's Authenticode signature and expected publisher identity.
6. Rejects redirects to unexpected schemes/hosts and rejects downgrade/replay attempts.
7. Uses unique temporary files, restrictive sharing, cleanup, cancellation, and atomic promotion.
8. Produces user-readable failure messages without offering a bypass.

Add tests for tampered manifest, wrong key, changed installer, wrong publisher, unexpected redirect, downgrade, truncated file, concurrent download, and valid update.

If no signing key/certificate exists yet, implement test keys/fixtures only in the test project and leave production key configuration explicit and fail-closed.

Exit criteria:
- MZ-only validation is no longer sufficient to execute an update.
- Every negative test proves execution is refused.
- A valid signed fixture completes the flow.
```

### Prompt 10 — Secure prerequisite installation

```text
Harden the .NET Desktop Runtime bootstrap path in `Installer/install-dotnet.ps1` and the Inno Setup integration.

Implement:
1. HTTPS-only download with redirect restrictions.
2. Authenticode validation requiring a valid Microsoft signer before execution.
3. Optional pinned hash support for a release-specific runtime package, where operationally practical.
4. Safe temporary-file creation and reliable cleanup.
5. Clear failure behavior that links the user to Microsoft's official manual installer instead of bypassing validation.
6. Tests or a safe validation harness for invalid signature, HTML/error payload, failed download, and valid Microsoft-signed installer metadata.

Exit criteria:
- MZ-header checking alone can never authorize runtime execution.
- A non-Microsoft or unsigned runtime installer is rejected.
```

### Prompt 11 — Reproducible release pipeline and SBOM

```text
Create a release-candidate pipeline that produces verifiable artifacts but does not publish them automatically.

It must:
1. Build from a clean tagged commit with deterministic settings.
2. Run all tests and security gates.
3. Produce the x64 application and installer.
4. Sign artifacts when release signing credentials are available.
5. Generate an SPDX or CycloneDX SBOM.
6. Generate SHA-256 checksums.
7. Verify signatures and checksums after packaging.
8. Produce machine-readable provenance containing commit, SDK, dependency-lock hash, and workflow identity.
9. Generate draft release notes from an explicit changelog.
10. Keep publication as a separately approved step.

Exit criteria:
- A release candidate can be traced back to one commit.
- Artifact, SBOM, checksum, and provenance outputs agree.
- Unsigned production artifacts cannot pass the release gate.
```

---

## Phase 4 — Harden local networking and remote control

### Prompt 12 — Bound and stream LAN Drop requests

```text
Harden `LanDropService` against memory exhaustion, disk exhaustion, slow clients, malformed requests, and unsafe filenames while preserving the existing portal behavior.

Implement:
1. Explicit maximum request-header, text-message, individual-file, multipart-part, total-request, and session-storage sizes.
2. Early rejection of missing, negative, malformed, conflicting, or excessive Content-Length values.
3. Streaming uploads directly to uniquely named temporary files rather than buffering entire uploads in memory.
4. Atomic move into the download directory only after a complete valid upload.
5. Read/write/idle timeouts and a bounded number of concurrent clients.
6. Cleanup of partial files after cancellation, disconnect, or parse failure.
7. Filename normalization and final-path containment checks.
8. Disk-free-space checks and user-readable rejection responses.

Add adversarial tests for oversized headers, huge declared bodies, slow clients, early disconnect, multipart edge cases, traversal filenames, duplicate filenames, concurrency limits, and cancellation.

Exit criteria:
- Request memory use is bounded independently of upload size.
- Incomplete uploads leave no final files.
- Path traversal and disk-exhaustion cases are rejected.
```

### Prompt 13 — LAN authentication and session lifecycle

```text
Improve LAN Drop authentication and session lifecycle.

Implement:
1. A fresh high-entropy access token for each server session instead of a long-lived reusable credential.
2. Automatic token invalidation on stop, restart, network-profile change, timeout, or explicit disconnect.
3. Rate limiting and temporary backoff for failed authentication.
4. Constant-time token validation using framework cryptographic APIs.
5. No token values in logs, errors, analytics, referers, or persisted history.
6. A connected-client list and a prominent UI action to disconnect all clients and rotate access.
7. Default binding/firewall behavior restricted to private networks where Windows APIs permit it.
8. A visible warning when the active network is public or cannot be classified.

Exit criteria:
- A URL from a previous session no longer works.
- Repeated guessing is throttled.
- Tokens never appear in logs.
- Users can see and terminate active access.
```

### Prompt 14 — Share Screen transport and control security

```text
Threat-model and harden Share Screen, chat, voice, and remote control.

Address:
1. Plain HTTP/ws exposure on untrusted Wi-Fi.
2. Viewer-token and control-code transport.
3. WebSocket Origin validation and cross-site WebSocket attacks.
4. Brute-force protection for control codes.
5. Per-session key derivation with domain separation for chat, voice, and control.
6. Replay resistance and message ordering where required.
7. Frame size, rate, connection-count, and idle-time limits.
8. Immediate revocation and release of held input on disconnect or app lock.
9. Clear host-side indication of every viewer and controller.
10. A safe migration path to TLS/WSS or an authenticated encrypted tunnel.

Write `docs/security/SHARE_SCREEN_THREAT_MODEL.md` before implementation. Add tests for wrong origin, replay, oversized frames, brute force, stale sessions, disconnect cleanup, and cryptographic tampering.

Exit criteria:
- The documented threats have explicit mitigations or clearly disclosed residual risk.
- Remote input cannot be reached without both current-session authorization and user-enabled control.
```

### Prompt 15 — Network service fuzz and abuse tests

```text
Create deterministic fuzz/property-based tests for AxePower's custom HTTP, multipart, query-string, and WebSocket parsing paths.

The tests must exercise malformed encodings, truncated frames, integer boundaries, repeated headers, contradictory lengths, invalid UTF-8, multipart boundary confusion, excessive nesting/fields, and random byte input. Tests must enforce time and memory limits and must never modify real user files or settings.

Save minimized regression inputs for every discovered crash or hang. Fix only issues demonstrated by the tests, keeping each fix narrow.

Exit criteria:
- Parsers do not crash, hang, allocate without bound, or escape configured storage paths for the tested corpus.
- Regression fixtures cover every repaired issue.
```

---

## Phase 5 — Improve maintainability without destabilizing behavior

### Prompt 16 — Feature-oriented architecture map

```text
Document the architecture that actually exists and propose a low-risk path from broad App/Core service buckets to feature-oriented modules.

Create `docs/engineering/ARCHITECTURE.md` covering:
- process and trust boundaries;
- presentation, application, domain, infrastructure, and native-integration responsibilities;
- dependency direction;
- feature areas such as Accounts, Friends, Music, LAN Drop, Share Screen, Uninstaller, Software Hub, Power, and Telemetry;
- data ownership and persistence;
- elevation and network boundaries;
- rules preventing view models from accumulating infrastructure logic.

Do not call the architecture DDD unless the implementation meets that definition. Provide a staged refactoring sequence with no big-bang rewrite.

Exit criteria:
- The document matches the code rather than marketing terminology.
- Every major service has a defined owner/layer and dependency direction.
```

### Prompt 17 — Split LanDropService safely

```text
Refactor `LanDropService` into cohesive, testable components without changing its user-visible behavior or routes.

Separate at least:
- listener/session lifecycle;
- HTTP request parsing and response writing;
- authentication;
- upload/download storage;
- multipart parsing;
- remote media actions;
- portal HTML/content delivery.

Use interfaces only where they improve test isolation. Preserve cancellation, disposal, logging, and DI behavior. First add characterization tests for existing supported behavior; then refactor in small steps.

Exit criteria:
- No replacement class becomes another oversized god object.
- Existing and new tests pass.
- Networking, storage, and domain actions can be tested independently.
```

### Prompt 18 — Split ShareScreenViewModel safely

```text
Refactor the oversized ShareScreen view model by extracting application services for room lifecycle, connection state, chat attachments, voice, viewer hosting, and UI-independent orchestration.

Keep CommunityToolkit.Mvvm commands and observable UI state in the view model, but move socket, cryptographic, file, and media orchestration out of it. Preserve all current bindings and behavior.

Add characterization tests before extraction and focused unit tests for each new service.

Exit criteria:
- The view model primarily coordinates UI state and commands.
- Network/file/crypto behavior is testable without WPF.
- Existing XAML bindings remain valid.
```

### Prompt 19 — Split Software Hub and command execution

```text
Refactor Software Hub so catalog data, package discovery, installation planning, process execution, progress reporting, and UI state are separate responsibilities.

Ensure every non-interactive command uses executable allow-lists and `ProcessStartInfo.ArgumentList`, not concatenated shell strings. Treat package identifiers, URLs, filenames, and catalog data as untrusted input even when currently hard-coded.

Add tests for quoting, malicious identifiers, cancellation, timeout, exit codes, elevation decisions, and partial failure.

Exit criteria:
- Installation commands cannot be altered through argument injection.
- UI code does not construct shell command lines.
- Cancellation terminates the correct process tree.
```

### Prompt 20 — Break up oversized XAML views

```text
Refactor the largest XAML files into feature-focused user controls and resource dictionaries while preserving the current visual design.

Start with MainWindow and one feature view at a time. Preserve bindings, accessibility metadata, keyboard navigation, dynamic resources, skin compatibility, and controller navigation. Avoid moving business logic into code-behind.

Add or update view-construction and binding tests. Produce a manual visual-verification checklist because automated tests cannot prove pixel-level equivalence.

Exit criteria:
- No broken bindings or missing resources.
- All skins still compile.
- Keyboard and controller navigation remain functional.
- Owner completes the documented visual check before the task is marked fully verified.
```

### Prompt 21 — Replace silent exception handling strategically

```text
Audit the broad and empty catch blocks in the tracked AxePower App/Core code. Do not mechanically replace every catch.

Classify each occurrence as:
- expected cleanup/best-effort;
- recoverable operational failure;
- user-action failure;
- programming/invariant failure;
- security-relevant failure.

For security-relevant and user-action paths, add typed exception handling, safe logging, and actionable user feedback. For cleanup catches, add narrow exception filters or a concise justification. Never log credentials, tokens, private room links, command secrets, or personal message content.

Create a measurable debt report and reduce the highest-risk cases first.

Exit criteria:
- Security and destructive-operation failures cannot disappear silently.
- Logging is useful without exposing secrets.
- Remaining empty catches are intentional and documented.
```

### Prompt 22 — Complete nullable correctness

```text
Remove the global suppression of CS8600, CS8602, and CS8604 from AxePower.Core through real nullable-flow fixes.

Work in small feature batches. Use validation, nullable annotations, explicit result types, or guarded control flow. Do not use blanket null-forgiving operators, dummy values, or new global suppressions.

Add tests for newly handled null/missing-data cases, especially registry values, JSON fields, network responses, hardware sensors, and optional Windows APIs.

Exit criteria:
- The three global suppressions are removed.
- Release build has no nullable warnings.
- Tests prove important missing-data paths degrade safely.
```

---

## Phase 6 — Safety, observability, privacy, and user trust

### Prompt 23 — Structured, redacted diagnostics

```text
Replace ad-hoc file logging with a structured, privacy-aware diagnostics design.

Implement:
1. Thread-safe structured log events with category, level, event ID, and correlation ID.
2. Central redaction for tokens, authorization headers, email addresses where unnecessary, room links, query credentials, local usernames/paths where appropriate, and user content.
3. Bounded retention by size and age.
4. A Settings action to inspect, export, and delete diagnostics.
5. A diagnostic bundle that requires explicit user action and previews exactly what will be included.
6. No automatic upload.
7. Tests proving known secret formats are redacted.

Exit criteria:
- Secrets do not appear in logs under tested error paths.
- Users can delete all diagnostics.
- Export is local and consent-driven.
```

### Prompt 24 — Change journal, preview, and rollback

```text
Design a unified safety framework for all AxePower actions that modify Windows state.

Create an operation model that records:
- requested action and source feature;
- administrator requirement;
- exact planned changes;
- pre-change snapshot/backup where possible;
- execution results per step;
- reversibility level;
- rollback action and outcome;
- timestamps and app version.

Integrate it first with one representative low-risk feature and one high-risk feature, then document the migration path for power plans, DNS, Windows Update policy, debloat, service changes, game mode, and uninstaller cleanup.

The UI must clearly distinguish fully reversible, partially reversible, and irreversible actions. Never promise rollback when Windows or a third-party uninstaller cannot guarantee it.

Exit criteria:
- Users can preview changes before approval.
- Completed changes have an inspectable history.
- Supported actions can be rolled back from that history.
```

### Prompt 25 — Trust and permissions center

```text
Create an in-app Trust & Permissions page using the existing visual system.

It should show:
- whether the app is running elevated;
- startup behavior;
- active local network listeners and connected clients;
- account sign-in status and cloud-stored data categories;
- local storage locations for settings, logs, music data, backups, and received files;
- optional third-party connections and when they are contacted;
- update source, current version, build commit, and signature status;
- controls to stop listeners, rotate access tokens, open/delete local data, sign out, and request account deletion.

All descriptions must be derived from actual behavior and privacy documentation. Add accessibility names and keyboard/controller navigation.

Exit criteria:
- A user can discover every meaningful permission, listener, and data store from one place.
- Displayed state is live and accurate.
```

### Prompt 26 — Make privacy claims precise and consistent

```text
Audit every privacy, security, local-only, open-source, telemetry, analytics, reversibility, and administrator-rights claim across README, release README, website, in-app About/Settings, installer, and prompts.

Create a data-flow inventory listing:
- data stored locally;
- optional account data stored in Supabase;
- data sent to GitHub, authentication providers, music/lyrics providers, download sources, or peers;
- purpose, trigger, retention, and user control for each flow.

Replace absolute claims such as `100% local` or `zero remote data collection` with accurate language. Keep the legitimate claim that AxePower has no ads or developer usage analytics if the code audit confirms it. Ensure website analytics consent is separate from desktop-app behavior.

Exit criteria:
- No two public surfaces contradict one another.
- Every network connection is disclosed by category and trigger.
- Claims are specific and testable.
```

### Prompt 27 — Destructive-action UX standard

```text
Create and apply a consistent UX standard for destructive or system-wide actions.

Each confirmation must show:
- plain-language action and scope;
- affected apps/files/settings;
- administrator requirement;
- risk level;
- backup/restore behavior;
- whether restart/sign-out is needed;
- whether the action is fully, partly, or not reversible;
- a safe default button and explicit confirmation for high-risk actions.

Do not use generic `Are you sure?` dialogs. Avoid dark patterns, preselected destructive options, and urgency language. Apply the standard first to uninstall cleanup, debloat, Windows Update repair, DNS changes, and bulk software operations.

Exit criteria:
- High-risk operations cannot execute from an ambiguous or accidental click.
- Confirmation content is testable from operation metadata rather than duplicated strings.
```

---

## Phase 7 — Product quality and usability

### Prompt 28 — Accessibility audit and fixes

```text
Audit AxePower's WPF interface for keyboard use, focus order, screen-reader names, contrast, high-contrast mode, text scaling, reduced motion, controller navigation, and color-independent status communication.

Use automated checks where possible, then create a manual test matrix for Windows Narrator, keyboard-only navigation, 200% text/display scaling, Windows High Contrast, and controller input.

Fix issues in small batches without redesigning the product. Ensure custom controls expose correct automation peers/properties.

Exit criteria:
- Core workflows are operable without a mouse.
- Status and errors are not conveyed by color alone.
- Manual accessibility results and remaining exceptions are documented.
```

### Prompt 29 — Performance and resource-budget audit

```text
Measure startup time, idle CPU, working set, handle/thread counts, background polling, network idle behavior, view navigation latency, and shutdown cleanup in Release builds.

Define realistic budgets for a Windows utility and record measurements on named hardware. Identify duplicate timers, unnecessary polling, undisposed resources, event-handler leaks, and services that run while their feature is inactive.

Optimize only measured hotspots. Add regression benchmarks or tests for stable, automatable cases.

Exit criteria:
- Baseline and final measurements are documented.
- Idle features do not perform unnecessary work or network traffic.
- Listener, media, hardware, and WPF resources are disposed on shutdown.
```

### Prompt 30 — Error and offline experience

```text
Standardize user-facing error, offline, cancellation, retry, and partial-success behavior.

Create typed application outcomes and map them to concise messages with optional technical details. Cover account offline/session expiry, update failures, unavailable hardware APIs, denied elevation, winget failures, blocked files, LAN disconnects, and third-party provider outages.

Never display raw stack traces, tokens, opaque HRESULTs, or misleading success messages. Preserve detailed redacted diagnostics for support.

Exit criteria:
- Expected failures have actionable messages.
- Cancellation is not presented as an error.
- Partial success identifies exactly what did and did not complete.
```

### Prompt 31 — First-run and safe-default review

```text
Review first-run behavior and every default setting using least privilege, minimum network exposure, reversibility, and user comprehension as criteria.

The application must not silently enable startup, open LAN listeners, sign users in, upload diagnostics, run optimizations, change power/update policies, or execute elevated actions. Introduce concise onboarding for optional capabilities and explain why each permission is needed at the moment it is requested.

Add tests for persisted consent/default states and upgrades from existing settings.

Exit criteria:
- Fresh installation performs no surprising system or network action.
- Existing users retain compatible settings unless a security migration explicitly requires a safer default and explains it.
```

---

## Phase 8 — Final security and release readiness

### Prompt 32 — Full security review

```text
Perform a final security review of the tracked production product after the preceding hardening work. Do not begin by changing code.

Review:
- trust boundaries and threat models;
- update/install signature enforcement;
- elevation and command execution;
- registry/filesystem deletion containment;
- LAN and Share Screen authentication/encryption;
- OAuth/account token storage and refresh;
- Supabase RLS, functions, grants, rate limits, and abuse controls;
- WebView2 navigation and script injection boundaries;
- secret handling and logs;
- dependency vulnerabilities;
- installer/uninstaller safety;
- privacy-policy accuracy.

Create `docs/security/FINAL_SECURITY_REVIEW.md` with severity, exploit prerequisites, evidence, remediation, and residual risk. Fix critical/high findings in separate, reviewable changes and add regression tests.

Exit criteria:
- No known critical or high issue remains unresolved for release.
- Accepted lower risks have owner, rationale, and review date.
```

### Prompt 33 — Release-candidate verification

```text
Produce and verify a release candidate without publishing it.

From a clean worktree/tag candidate:
1. Run locked restore, Release build, all tests, coverage, analyzers, secret scan, and vulnerability scan.
2. Build the application and installer through the release pipeline.
3. Verify Authenticode signatures and timestamps.
4. Verify SHA-256 checksums, SBOM, provenance, file versions, installer version, publisher, and upgrade/uninstall metadata.
5. Test clean install, upgrade from the latest public version, repair/reinstall, uninstall, rollback after failed install, standard-user launch, and first-run behavior in clean Windows VMs.
6. Run smoke tests with network offline and with optional services unavailable.
7. Confirm privacy, terms, license, third-party notices, and security links ship with the product.
8. Create `docs/release/RELEASE_CANDIDATE_REPORT.md` with evidence and all manual sign-offs.

Do not publish, push tags, or upload artifacts.

Exit criteria:
- Every automated gate passes.
- Required manual Windows tests are signed off.
- No unexplained file/version mismatch exists.
```

### Prompt 34 — Independent usability and security validation

```text
Prepare AxePower for independent review rather than self-certifying it.

Create two reviewer packages:
1. A usability test plan for representative Windows users, covering installation trust, first run, common optimizations, undo, LAN sharing, error recovery, update, and uninstall.
2. A security assessment brief containing scope, architecture, threat models, build provenance, test evidence, known limitations, and explicit authorization boundaries for a third-party tester.

Do not claim an audit or certification has occurred. Record findings from actual reviewers separately and convert confirmed findings into tracked remediation tasks.

Exit criteria:
- An external reviewer can test the product without needing undocumented knowledge.
- Marketing makes no unsupported certification claim.
```

### Prompt 35 — Final go/no-go assessment

```text
Perform a final evidence-based go/no-go assessment for public release. Do not change code in this task.

Evaluate:
- build/test/coverage results;
- unresolved defects by severity;
- dependency and secret scans;
- signing and update verification;
- install/upgrade/uninstall results;
- privacy/legal consistency;
- accessibility and usability sign-offs;
- performance budgets;
- support and vulnerability-reporting readiness;
- backup/rollback behavior;
- known limitations and incident response.

Create a concise release decision document with one of: GO, CONDITIONAL GO, or NO-GO. Every condition must have an owner and deadline. A critical/high security issue, unsigned production installer, unverifiable update, or failing destructive-operation safety test is automatically NO-GO.

Exit criteria:
- The decision is based on linked evidence, not feature count or schedule pressure.
- Residual risks are explicitly accepted by the project owner before publication.
```

---

## Suggested completion tracking

Maintain this table as prompts are completed. Link each result to the relevant commit or report.

**Where it stands (2026-10-06):**
- **Done:** prompts 1–23.
  - Fully done: 1–12, 15–19 and 22.
  - Still need the owner's own check: 13, 14, 20, 21 and 23. Step-by-step instructions are in
    `docs/engineering/OWNER_CHECKS.md`.
- **Next:** 24.
- **Owner decisions made on 2026-10-06:**
  - Releases stay unsigned for now.
  - The update-signing key is set up and its public key is pinned (2026-10-06).
  - Updates are trusted on the signed manifest plus the signed size and SHA-256 until releases are
    code-signed.
  - The stale Share Screen encryption text is fixed.
- **Waiting on the owner** (`docs/engineering/OWNER_CHECKS.md`):
  - A1–A3: on this PC
  - B1–B2: phone, installer
  - C1–C2: second PC, Tailscale
- **Commits:** prompts 1–21 are committed locally on `2.1.2-industry-standards-followup` (not
  pushed). The branch also contains commit `385b0d9`, which adds `CloudBridgeService`
  (remote control over Supabase Realtime). It needs a security review: see
  `docs/engineering/EXCEPTION_DEBT.md`.

| Prompt | Status | Evidence | Residual work |
|---:|---|---|---|
| 1 | Complete | `docs/engineering/BASELINE.md` (2026-10-03, `b68f001`) | High SQLite advisory; unsigned/unverified downloads; LAN/Share Screen hardening; warnings and coverage gaps are recorded for later prompts |
| 2 | Complete | `docs/engineering/DEPENDENCIES.md` (2026-10-03, based on `b68f001`) | Continue broader dependency modernization separately; no unrelated major versions were changed |
| 3 | Complete | `LICENSE`, `SECURITY.md`, `CONTRIBUTING.md`, `.editorconfig`, plus README/website links (2026-10-03) | A standalone `CODE_OF_CONDUCT.md` remains intentionally uncreated pending explicit owner approval |
| 4 | Complete | `Directory.Build.props`, `Directory.Build.targets`, `Installer/AppVersion.iss`, `docs/engineering/VERSIONING.md`, `AxePower.Tests/VersionPolicyTests.cs` (2026-10-04) | Public version remains 2.1.0; full analyzer warning cleanup is staged for Prompt 7 |
| 5 | Complete | `.github/workflows/ci.yml`, `docs/engineering/CI.md`, `AxePower.Tests/CiWorkflowTests.cs` (2026-10-04) | Release publication and signing will be designed in later prompts |
| 6 | Complete | `packages.lock.json` in all projects, `Directory.Build.props`, `.github/dependabot.yml`, `scripts/audit-dependencies.ps1`, `docs/engineering/DEPENDENCIES.md`, `AxePower.Tests/DependencyPolicyTests.cs` (2026-10-04) | Full major-version modernization is intentionally deferred to dedicated feature tasks |
| 7 | Complete | `Directory.Build.props`, `.editorconfig`, `docs/engineering/WARNINGS_DEBT.md`, `PowerTimerControl.xaml.cs`, `SettingsViewModel.cs`, `AxePower.Tests/AnalyzerPolicyTests.cs` (2026-10-04) | CS8600/CS8602/CS8604 in AxePower.Core remain tracked for true nullable resolution in Prompt 22 |
| 8 | Complete | `Installer/AxePower.iss`, `scripts/sign-file.ps1`, `scripts/verify-signature.ps1`, `scripts/release-gate.ps1`, `docs/security/CODE_SIGNING.md`, `AxePower.Tests/CodeSigningPolicyTests.cs` (2026-10-04) | Awaiting external owner acquisition of production Authenticode certificate or Azure Trusted Signing credentials |
| 9 | Complete | `AxePower.Core/Services/Update/`, `UpdateService.cs`, `AboutViewModel.cs`, `MainViewModel.cs`, `docs/security/SIGNED_UPDATES.md`, `AxePower.Tests/UpdateSecurityTests.cs` (2026-10-04) | Public key pinned 2026-10-06 (D2). Each release still needs `scripts/sign-update-manifest.ps1` run and `update-manifest.json` uploaded next to the installer; no release has done that yet |
| 10 | Complete | `Installer/install-dotnet.ps1`, `Installer/AxePower.iss`, `docs/security/RUNTIME_BOOTSTRAP.md`, `AxePower.Tests/DotNetBootstrapTests.cs` (2026-10-04) | Runtime bootstrap enforces HTTPS, Microsoft domain allow-list, Authenticode validation, and fails closed without bypass |
| 11 | Complete | `.github/workflows/release-candidate.yml`, `scripts/package-release.ps1`, `scripts/generate-sbom.ps1`, `scripts/generate-provenance.ps1`, `scripts/generate-release-notes.ps1`, `CHANGELOG.md`, `docs/engineering/RELEASE_PIPELINE.md`, `AxePower.Tests/ReleasePipelineTests.cs` (2026-10-04) | Release candidates produce verified artifacts, CycloneDX SBOM, checksums, and provenance; public releases remain manual |
| 12 | Complete | `AxePower.Core/Services/LanDrop/`, `LanDropService.cs`, `AxePower.Tests/LanDropSecurityTests.cs` (2026-10-04) | Hardened against memory/disk exhaustion, slow clients, malformed headers/Content-Length, traversal names, and concurrency limits |
| 13 | Complete (code); manual checks open | `PortalAccessToken.cs`, `LanDrop/NetworkProfileClassifier.cs` (live Network List Manager profile per adapter), `LanDrop/LanDropSecurityLimits.cs`, `LanDropService.cs` (refuse Public/unclassified unless allowed per session, bind to chosen adapter, session watchdog for expiry/idle/profile change, HttpOnly SameSite=Strict cookie replacing `?t=`), `PhoneHubViewModel.cs` + `PhoneHubView.xaml` (warning, override, connected devices, disconnect-all), installer private/domain-only firewall rule, `AxePower.Tests/LanDropAuthenticationTests.cs`, `AxePower.Tests/LanDropSessionLifecycleTests.cs`; full suite 1265 passed / 1 skipped, `--self-test` prints the live network profile (2026-10-04) | Owner must check the Phone Hub UI and a real phone session (MUSIC_FIX_PLAN P1, P6), run a real install for the firewall rule (P7); per-device disconnect is not a revocation (P8); no TLS (P4, Prompt 14) |
| 14 | Complete (code); manual checks open | `docs/security/SHARE_SCREEN_THREAT_MODEL.md` (rewritten; the earlier same-day version overclaimed), `ShareScreenCrypto.cs` (HKDF domains, nonce + HMAC admission, per-sender replay window), `RoomMessageCrypto.cs` v2 / `RoomVoiceCrypto.cs` v3 (sender + sequence as AES-GCM AAD), `ShareViewerHost.cs` (no secrets on the wire, Host check vs DNS rebinding, MAC'd ordered control frames, no takeover, per-session failure cap, head/idle/ping/connection limits), `ShareViewerPage.cs` (in-page SHA-256/HMAC/HKDF), `ShareRoomClient.cs`, `ShareScreenService.cs` (Public-network refusal with one-session override, participants incl. MediaMTX video viewers), Friends page banner + room list, `AxePower.Tests/ShareScreen/ShareScreenSecurityTests.cs`; full suite 1284 passed / 1 skipped (2026-10-04) | Follow-up fixes 2026-10-04: control frames AES-GCM encrypted (vendored @noble), host Allow prompt for control, per-IP nonce cap, WHEP read proofs via MediaMTX authMethod http (live-verified with MediaMTX + FFmpeg), HTTPS/WSS over Tailscale via `tailscale cert`. Remaining residual (threat model §6): plain-LAN page integrity (R6), IPv4-only listener (R10), real Tailscale run unverified (R11). Two-machine run, browser control and UI unverified (MUSIC_FIX_PLAN P9, P10); older AxePower versions cannot join chat/voice |
| 15 | Complete | `AxePower.Tests/Fuzzing/FuzzHarness.cs` (per-input thread with hard timeout, allocation budget, ddmin minimization, artifacts, fixture replay incl. gzipped), `ParserPropertyFuzzTests.cs` (HTTP heads, query strings, cookie, control frames, chat/voice envelopes, control events incl. deep nesting, MediaMTX list, filenames, WebSocket frames), `MultipartStagingFuzzTests.cs`, `NetworkServerFuzzTests.cs` (both servers over loopback), `Fuzzing/Regressions/` + README; fixes: multipart boundary capped at 70 (CPU hang), upload extension capped at 32 (unusable names), MediaMTX parser kind checks (throw); `LanDropService` download-folder seam so no test touches the real Downloads; full suite 1303 passed / 1 skipped, settings.json and real Downloads unchanged (2026-10-04) | Nonce exhaustion by a link holder recorded as Share Screen residual R9; corpus is mutation-based (no coverage guidance) |
| 16 | Complete | `docs/engineering/ARCHITECTURE.md` (2026-10-04): process/elevation/network boundaries, roles per type, owners and dependencies for every service registered in `App.ConfigureServices` (checked by script: 201 of 201 named), data ownership with real paths, view-model rules with a counted violation baseline, target dependency direction, 8-stage refactor plan | Documentation only. Stage 1 (architecture ratchet test) is the next concrete step; stage 7 (privilege split) needs its own design |
| 17 | Complete | Characterization first (`AxePower.Tests/LanDropCharacterizationTests.cs`, 17 tests over real sockets, green on the old code), then six steps each verified by the 150-test LAN Drop family: portal page → `LanDropPortalPage`; HTTP → `LanDropHttp`; remote actions → `IRemoteMediaActions`/`WindowsRemoteMediaActions`/`RemoteMediaActionParser`; storage → `LanDropStorage`/`LanDropClipboard`; auth and devices → `LanDropAuthenticator`/`LanDropClientRegistry`; routing → `LanDropRequestHandler` + `LanDropEndpoints`/`LanDropMusicEndpoints`. `LanDropService` 1,953 → 488 lines, largest new class 342. `LanDropComponentTests.cs` (20, no sockets). Full suite 1372 passed / 1 skipped (2026-10-05) | Routes, statuses, headers, events, DI and cancellation unchanged. Only interface added: `IRemoteMediaActions` (real one presses keys); internal `ILanDropHost` for endpoint tests. Quick-text content was still logged verbatim; fixed in Prompt 21 (only its length is logged) |
| 18 | Complete | Characterization first (`ShareScreenViewModelCharacterizationTests.cs`, 8 tests: sending and receiving text with unread count, received file verified and listed, wrong-hash file discarded, voice both ways, history restore and clear, joining a real room host for chat and voice, bad join addresses; green on the old code) and a binding test (`ShareScreenViewBindingTests.cs`: lays the Friends page out in chat, files, refused, open-room and join states and fails on any WPF binding error, with a canary). Extracted to Core: `ShareRoomLink` (link normalizing, viewer secret), `ShareRoomSession` (joining client: admission, chat and voice sockets, decryption, replay windows), `ShareAttachmentChunker`/`ShareAttachmentAssembler` (file chunking and verified reassembly, 8 in flight), `ShareRoomConversation` + `ShareRoomRouter` (history, send/receive, host vs joined), `ShareVoiceCoordinator`. `ShareScreenViewModel` 1,491 → 1,166 lines, with no WebSocket, crypto, file or `Task.Run` code; public surface unchanged (checked by script). `ShareRoomServicesTests.cs` (13 tests, no WPF). Live MediaMTX tests share a `LiveMediaMtx` helper that retries on a port race. Full suite 1394 passed / 1 skipped, twice (2026-10-05) | Commands, observable properties and bindings unchanged. Layout of the page still to be looked at by the owner. Joined-client protocol still unproven between two machines (MUSIC_FIX_PLAN P9) |
| 19 | Complete | Software Hub split by responsibility: `SoftwareCatalog` (data + validation; a hostile entry is left out), `PackageDiscovery` (install state, one `winget list`, App Paths), `InstallPlanner` (pure: action → steps, fallbacks, refusals, rights), `WinGetHubService` (coordinator, 978 → 300 lines; progress, batch with partial failure, cancellation), `GitHubPortableToolDownloader` (allowed hosts, temp file, PE check). New `Services/Processes`: `ExecutableAllowList` (fixed paths; winget from its Program Files package folder when elevated), `AllowListedProcessRunner` (`ArgumentList`, stdin closed, timeout, per-run job object), `UserShellLauncher` + `ElevationPolicy` (pages, apps and downloaded tools start as the signed-in user via Explorer when elevated; no elevated fallback). `PackageInput` rejects identifiers that could be read as options or carry separators, non-https/http URLs, downloads off vendor hosts, and unsafe file names. Graphics, firmware, restore point and driver updater launch through the same pieces. View model: no process, file or shell code (`SoftwareHubUiCode_BuildsNoCommandLines`); "Install in Terminal" runs the same safe installs with the Terminal showing output instead of typing a PowerShell script; Cancel on busy cards and the batch bar. Tests in `AxePower.Tests/SoftwareHub/` (157 incl. theory cases): malicious ids, argv round-trip through a real process, exit codes, timeout, cancel ending the tree and an orphaned grandchild (job object proven necessary by disabling it), elevation matrix, partial failure, downloads. Full suite 1533 passed / 1 skipped, twice (2026-10-05) | Behaviour changes: uninstall adds `--exact`; launches drop to the signed-in user when elevated; a missing bundled installer counts as not installed in a batch; Ninite is looked for next to AxePower only (not the working directory). The Cancel buttons are a visual change for the owner to check. MSI installs run inside the Windows Installer service, outside the run's tree, so a cancel cannot stop one already handed over. When not elevated, the driver updater still writes its script to a user-writable file before the UAC prompt |
| 20 | Complete (code); owner visual check open | `MainWindow.xaml` 2,612 → 282 lines: `Views/Shell/ShellSidebar`, `ToolHelpOverlay`, `UpdateBanner`, `PatchNotesDialog` (moved verbatim; the two feed-scroll handlers moved with it), `Resources/ViewTemplates.xaml`, `ShellChromeStyles.xaml`, `TrayMenuStyles.xaml`. `SoftwareHubView.xaml` 1,383 → 68 lines: `Views/SoftwareHub/UpdatersTab`, `PackagesTab`, `UpdateShieldTab`, `WebLinkWarningDialog`, `NvidiaDriverPickerDialog`, `Resources/SoftwareHubStyles.xaml`. No logic moved into code-behind. Evidence: a throwaway harness rendered 14 states under 5 skins and dumped each state's tab stops, accessibility names, commands, positions and arrow-key targets, before and after; all identical except the intended red icons (and patch-note like counts, which also varied between two runs before the change). `AxePower.Tests/Shell/ShellViewTests.cs` (8 tests): binding errors in every shell state and Software Hub tab/dialog under dark/light/glass, resource keys of the split files in all 44 skins, every `DynamicResource` in every app XAML in all 44 skins, all skins compile, sidebar Up/Down and title-bar Left/Right navigation, tray menu frame/styles/items, overlays and feed scrolling. `TestServiceContainer` shared with the DI test. Full suite 1541 passed / 1 skipped, twice (2026-10-05) | Fixed while scanning: `DangerBrush` was in no skin (Software Hub Cancel/Uninstall, System Doctor Cancel Task, Terminal Stop now use `ErrorBrush`, so they turn red); never-defined `SurfaceBrush`/`SurfaceHoverBrush` became `Transparent` (no visual change, by owner choice). **Fully verified only when the owner completes `docs/engineering/VISUAL_CHECKS.md`.** Other large views (Uninstaller, Phone Hub, Music, Game Mode) not split yet |
| 21 | Complete (code); one visible change for the owner | Audit of all 785 catches (`docs/engineering/EXCEPTION_DEBT.md`), classified as cleanup, recoverable, user-action, programming or security. Central redaction: `LogRedactor` in `LogService` (URL queries/fragments, auth headers, JWTs, key=value secrets, cookies, emails, opaque tokens), `LogWarning(message, ex)`, quick-text content no longer logged. `ExpectedFailures` filters (Registry, FileSystem, Process, Com, Wmi, Path, Parse) and `Connection.IsGone`. Highest-risk fixes: uninstall history was **wiped** by a save after a failed read, and the backup index was overwritten when corrupt (both now preserved and logged); protected `Windows*` folders listed explicitly; resume of suspended apps checked and retried; service throttle/restore report net.exe's real result; tweak reverts report failure; Share Screen session-lock and LAN Drop network watches log when unavailable; refused room admission tells the user why (`ChatEnded`); copy and open buttons, Play, and history delete report failure. Empty catches without a comment 210 → 0; security and destructive areas: 0 unexplained broad catches; remaining debt 43, listed. Tests: `ExceptionHandlingPolicyTests` (scanner with string/comment masking, three rules, ratchet), `LogRedactorTests`, `FailureHandlingTests` (35 tests). Full suite 1576 passed / 1 skipped, twice (2026-10-06), after fixing the live MediaMTX test helper, which could pick a WebRTC port whose +300 UDP port passed 65535 | The Friends page's chat-ended toast is a visible change for the owner to see. Untested on a real system: throttler net.exe results, the resume retry, tweak-revert access denied. Not fixed (listed in the report): Phone Hub's winget fallback in UI code, the optimistic battery-mode log line |
| 22 | Complete | Suppression removed from `AxePower.Core.csproj`. Measured first: Core already had **0** CS8600/CS8602/CS8604 warnings (a planted dereference was reported, so the compiler was checking). What hid nullability were 9 `null!`/`default!`, all replaced: `[NotNullWhen(true)] out T?` on 4 Try methods, `string?` on 2 native fields, a restructured MSI retry loop. Real bugs they hid: the DisplayConfig refresh-rate path never ran (`QueryDisplayConfig` with null arrays returns error 87, checked on this PC), so it was removed and GDI does the job as before (owner's choice); a portable-tool fallback could reach `HttpClient.GetAsync(null)`. JSON nulls the compiler cannot see: a `settings.json` with `"appRules": null` made every `GetSettings()` throw (13 settings fields exposed). `Helpers/JsonNullRepair.cs` now restores declared defaults in saved files (settings, playback state, recents, account session, chat, backup index, skins) and refuses incomplete network input (room chat, where `"chunkData": null` threw in the host chat loop; update manifest). `ShareRoomClient` reports a missing nonce clearly. Tests: `AxePower.Tests/Nullability/` (JSON files, network replies, wrong-typed registry values, sensors without readings, 42 tests) plus a portable-download test; `AnalyzerPolicyTests` now forbids any Core `NoWarn`, `null!`/`default!` and nullable opt-outs. The xUnit1031 warnings in two fuzz tests were fixed too. Clean Release rebuild 0 warnings; full suite 1640 passed / 1 skipped, twice; settings.json unchanged; self-test OK (2026-10-06) | Not run on real hardware: the sensor tests use fakes of LibreHardwareMonitor's interfaces. Refresh-rate switching still goes through GDI exactly as before, but nobody has watched it since the change |
| 23 | Complete (code); owner check A4 open | `docs/engineering/DIAGNOSTICS.md`. Structured events (level, category, event ID, correlation ID, call site) as JSON Lines in `%LOCALAPPDATA%\AxePower\Diagnostics`, one lock for appends, rolled at 2 MB, kept 14 days or 20 MB (`Services/Diagnostics/DiagnosticsStore`); fixed IDs for app lifetime, export and security refusals (LAN Drop lockout, refused update, which was not logged at all before); correlation for LAN requests, update check/download, Software Hub actions and uninstall batches. `LogRedactor` extended: user profile to %USERPROFILE%, other profiles, user and computer name, URL credentials, whole Cookie headers, private-key blocks, GitHub/Supabase/OpenAI/Anthropic/Stripe/Slack/AWS/Google/Discord key formats. LAN Drop logs transferred files by kind and size only. The raw unredacted startup trace (command-line arguments) is gone; crash handlers use fixed IDs; unobserved task exceptions are logged. Settings › Diagnostics & Privacy: summary, Open… (every bundle file with full text, optional redacted settings and uninstall logs; Save writes exactly the preview to a chosen .zip), Delete all (asks first). Old AxePower.log files deleted on first start; no upload code (test-enforced). `new LogService()` is in-memory and is the container default, so tests and --self-test no longer write the real log (the old one held 53,000 lines of test noise). Bugs found while testing: files ordered by write time read back out of order, and rolled parts were reused after retention. Tests: `AxePower.Tests/Diagnostics/` (secret formats 45, error paths, store, bundle, view models, render in 3 skins), 76 new. Clean Release rebuild 0 warnings; full suite 1716 passed / 1 skipped; settings.json and the old logs unchanged by tests; self-test read-only (2026-10-06) | Owner check A4: the card and window in the real app, and first-start removal of the old logs. Call sites without a fixed event ID (most of them) are identified by call site only |
| 24 | Not started | | |
| 25 | Not started | | |
| 26 | Not started | | |
| 27 | Not started | | |
| 28 | Not started | | |
| 29 | Not started | | |
| 30 | Not started | | |
| 31 | Not started | | |
| 32 | Not started | | |
| 33 | Not started | | |
| 34 | Not started | | |
| 35 | Not started | | |

## Definition of complete

AxePower should be considered production-ready only when:

- release binaries and installer are signed and independently verifiable;
- application updates fail closed on any signature, publisher, hash, host, or version mismatch;
- no known critical/high dependency or application-security issue remains;
- CI consistently passes build, tests, analyzers, vulnerability scanning, and secret scanning;
- destructive actions are previewed, bounded, recorded, and reversible where promised;
- LAN/remote-control risks are mitigated and accurately disclosed;
- public privacy/security claims match actual network and storage behavior;
- install, upgrade, uninstall, offline, accessibility, and recovery paths have been verified on clean Windows systems;
- users have a clear support and vulnerability-reporting path;
- remaining limitations are documented honestly.
