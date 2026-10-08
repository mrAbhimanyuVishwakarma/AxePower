# Release decision: AxePower 2.1.2

**For:** the project owner, who makes and signs off the decision.
**Assessed:** 2026-10-08 (Prompt 35), on the candidate in
[RELEASE_CANDIDATE_REPORT.md](RELEASE_CANDIDATE_REPORT.md). No code was changed for this
assessment.

## Decision: **NO-GO**

The production installer and updates are not Authenticode-signed. The release rules make that an
automatic NO-GO, whatever else passes. Two further gaps would each also block a public release:

- none of the required manual Windows tests has been signed off;
- the candidate was built from uncommitted changes.

The code itself is in good shape:

- every automated gate except signing passes;
- no Critical issue is known, and the only High is the signing gap.

## Evidence

| Area | Status | Evidence |
|---|---|---|
| Build, tests, coverage | Pass | 1,904 passed / 1 skipped; coverage 60.9% lines, 50.9% branches; 0 warnings with warnings as errors ([RC report](RELEASE_CANDIDATE_REPORT.md)) |
| Unresolved defects by severity | Critical 0; **High 1 (H1)**; Medium 0 open (2 fixed, 2 accepted); Low 0 open (1 fixed, 5 accepted) | [FINAL_SECURITY_REVIEW.md](../security/FINAL_SECURITY_REVIEW.md) |
| Dependency and secret scans | Pass | No vulnerable packages (transitive); no secrets tracked or in history. TruffleHog runs in CI and was not run locally |
| Signing and update verification | **Fail: signing** / Pass: update verification | Unsigned AxePower.exe and Setup.exe; the release gate refuses them. Updates: signed manifest (ECDSA P-256, pinned), size and SHA-256 checked, re-checked under a lock at launch (M1) |
| Install, upgrade, uninstall | **Not tested** | RC report R1–R7 not run |
| Privacy and legal consistency | Pass | `PrivacyClaimsTests`, `TrustInventoryTests`, `CertificationClaimsTests`; LICENSE, SECURITY, PRIVACY and notices now ship ([RC report](RELEASE_CANDIDATE_REPORT.md)) |
| Accessibility sign-off | **Open** | Automated checks pass ([ACCESSIBILITY.md](../engineering/ACCESSIBILITY.md)); manual matrix (owner check A8) not run |
| Usability sign-off | **Open** | No independent session yet ([USABILITY_TEST_PLAN.md](../review/USABILITY_TEST_PLAN.md), [REVIEW_FINDINGS.md](../review/REVIEW_FINDINGS.md)) |
| Performance budgets | Partly shown | Tray, Settings, start-up and page switches within budget in the test host; Dashboard CPU not demonstrated; real-app check A9 open ([PERFORMANCE.md](../engineering/PERFORMANCE.md)) |
| Support and vulnerability reporting | Ready | [SECURITY.md](../../SECURITY.md): private email, acknowledgement in 3 business days, triage in 7 |
| Backup and rollback | Pass | Change journal and rollback; uninstaller backups; 202 destructive-operation safety tests pass ([SAFE_CHANGES.md](../engineering/SAFE_CHANGES.md)) |
| Known limitations | Documented | Accepted risks M3, M4, L1, L2, L4–L6 with owner and review date; remote access disabled in code |
| Incident response | **Missing** | There is no written plan for a bad release: how to pull a release, revoke an update manifest, and tell users |

None of the destructive-operation safety tests fails. A failure there would also be an automatic
NO-GO.

## Conditions for GO

Each condition belongs to the project owner. The owner confirmed the deadlines on 2026-10-08.

**Owner's note on C1 (2026-10-08):** no budget for a certificate yet; one will be bought once
AxePower earns. Until then any release is unsigned. By this document's rules that stays NO-GO
for a public release, and users will see Windows' "Unknown publisher" warning.

| # | Condition | Proposed deadline |
|---|---|---|
| C1 | Buy an OV/EV code-signing certificate. Then: <ul><li>set `SignToolCommand` in the release pipeline;</li><li>turn on `RequireAuthenticode` with the certificate subject as `ExpectedPublisher`;</li><li>align AxePower.exe's CompanyName with that subject;</li><li>re-run the release gate until it passes (RC report R10).</li></ul> | 2026-11-15 |
| C2 | Commit the Prompt 32–35 changes, tag the candidate, and re-run `package-release.ps1` from the tag in a clean worktree, updating the RC report | Before C3 |
| C3 | Run and sign off the manual tests R1–R9 (clean install, first run, upgrade from 2.1.0, repair, uninstall, rollback, standard user, offline, services blocked) on clean Windows 10 and 11 VMs | 2026-11-22 |
| C4 | Run owner checks A8–A11 (accessibility matrix, real-app idle cost, error messages, first run) and record the results in `OWNER_CHECKS.md` | 2026-11-22 |
| C5 | Write a short incident-response plan in `docs/release/INCIDENT_RESPONSE.md`. It should cover: <ul><li>who decides to pull a release;</li><li>how to withdraw `update-manifest.json` so installed copies stop offering it;</li><li>how to publish a fixed version;</li><li>how to tell users;</li><li>the security-report path from SECURITY.md.</li></ul> | 2026-11-15 |
| C6 | Compare the production Supabase project (Auth rate limits, tables, policies, functions) with `supabase/migrations` (FINAL_SECURITY_REVIEW L6) | 2026-11-15 |

**Recommended, not blocking:**

- an independent usability round and a security assessment, using the packages in `docs/review`;
- the deferred patch notes, help, website and download-repo updates.

## Residual risks the owner must accept before publishing

Once C1–C6 are met, these remain. The owner must sign below to accept them.

- **M3:** Debloat runs third-party scripts from the internet as administrator, after a high-risk
  confirmation.
- **M4:** LAN Drop is plain HTTP on Private networks.
- **L1, L2, L4, L5, L6:** the low risks listed in FINAL_SECURITY_REVIEW.
- **No independent review yet:** findings may exist that a self-review cannot see.
- **Unmeasured Dashboard CPU:** its cost on lower-end hardware has not been measured.

| Accepted by | Date | Signature |
|---|---|---|
| | | |

Until this table is signed and C1–C6 are met, the decision stays **NO-GO**.
