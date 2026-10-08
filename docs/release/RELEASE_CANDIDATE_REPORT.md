# Release candidate report: AxePower 2.1.2

**For:** the owner deciding on release, and whoever runs the manual sign-offs.
**Date:** 2026-10-08 (Prompt 33). Nothing was published, tagged, pushed or uploaded.

## Candidate

- **Source:** branch `2.1.2-industry-standards-followup` at `5167682`, plus the uncommitted
  Prompt 32–33 changes:
  - the security fixes M1, M2 and L3;
  - lock files with `win-x64`;
  - the installer's version info and shipped documents;
  - a test timing fix.
- **How it was made:**
  - The working tree was frozen with `git stash create` as commit `71f1ac0`, which is not on any
    branch.
  - It was checked out into a separate clean worktree (`D:\Projects\AxePower-rc`), and the pipeline
    ran there.
  - The build stamps `2.1.2+71f1ac054461` into AxePower.exe.
- **Before publishing:** commit those changes, and repeat this report from the commit or tag. The
  candidate here proves the pipeline and the code, not a final tag.

## Automated gates

| Gate | Result | Evidence |
|---|---|---|
| Locked restore (`--locked-mode`) | **Pass** after a fix | It failed: see "Found and fixed" 1 and 2 |
| Release build, deterministic, warnings as errors | Pass | `package-release.ps1` step 2 |
| All tests | Pass: 1,904 passed, 1 skipped (a live uninstall test that needs a real app) | Step 3. A shutdown test was flaky in the first run; see "Found and fixed" 3 |
| Code coverage | Measured: 60.9% lines, 50.9% branches overall. AxePower.Core 67.8% / 58.4%; AxePower (app) 46.5% / 34.5% | `--collect:"XPlat Code Coverage"` in the worktree (cobertura). No minimum is enforced; the app's lower figure is mostly XAML code-behind and Windows-only paths |
| Analyzers | Pass | Built with `TreatWarningsAsErrors` (CI mode); 0 warnings |
| Vulnerability scan | Pass: no vulnerable packages, transitive included | `dotnet list package --vulnerable --include-transitive`; `audit-dependencies.ps1 -FailOnVulnerability` (0 high/critical) |
| Secret scan | Pass (pattern scan) | Regex scan of tracked files and history (FINAL_SECURITY_REVIEW). TruffleHog runs in CI only and was not run locally |
| Publish (win-x64, framework-dependent) | Pass | Step 4 |
| Installer build (Inno Setup) | Pass | `AxePower-v2.1.2-Setup.exe`, 20,405,620 bytes |
| SBOM | Pass | `sbom.cdx.json`, CycloneDX 1.5, 58 components |
| SHA-256 checksums | Pass | `SHA256SUMS`; recomputed for Setup.exe and AxePower.exe, and both match |
| Provenance | Pass | `provenance.json` (buildDefinition, subject, timestamp) |
| Signed update manifest | Pass | `update-manifest.json`: version 2.1.2, Setup.exe size and SHA-256, signed with the pinned key |
| **Authenticode signatures and timestamps** | **Fail (expected)** | AxePower.exe and Setup.exe are `NotSigned`. The release gate refuses both, fail-closed as designed. FINAL_SECURITY_REVIEW H1 |

## Versions and metadata

| Item | Value | Check |
|---|---|---|
| AxePower.exe FileVersion / ProductVersion | 2.1.2.0 / 2.1.2+commit | Matches `Directory.Build.props` |
| AxePower.Core.dll FileVersion | 2.1.2.0 | Matches |
| Setup.exe ProductVersion | 2.1.2 | Matches |
| Setup.exe FileVersion | Was blank; now 2.1.2 | Fixed in this prompt and rebuilt: see "Found and fixed" 4 |
| Publisher (installer, uninstall entry) | Abhimanyu Vishwakarma | Must equal the certificate subject once signed (`UpdateSecurityOptions.ExpectedPublisher`) |
| AxePower.exe CompanyName | AxePower | Differs from the installer's publisher. Harmless now; align both with the certificate subject when signing |
| Uninstall metadata | AppId stable, AppVersion 2.1.2, publisher, support and update URLs set | `Installer/AxePower.iss` |

## Shipped with the product

| Item | Where | Status |
|---|---|---|
| Third-party notices | `{app}\THIRD_PARTY_NOTICES.md` | Shipped |
| License | `{app}\LICENSE.txt` | **Added** in this prompt |
| Security policy and reporting | `{app}\SECURITY.md` | **Added** in this prompt |
| Privacy policy | `{app}\PRIVACY.md`; in-app Profile › Privacy Policy; website `privacy.html` | **Added** to the install folder in this prompt; in-app and website already present |
| Terms | In-app Profile › Terms; website | Present |

`InstallerContentsTests` keeps these listed.

## Found and fixed during verification

1. **Release publishing could not pass in release mode.** The pipeline publishes with
   `-r win-x64` under locked restore, but the lock files had no `win-x64` section (NU1004).
   `RuntimeIdentifiers=win-x64` was added to AxePower.App and AxePower.Core, and the lock files
   regenerated (additions only).
2. **CI's locked restore was already broken by the 2.1.2 version bump.** The test project's lock
   file still pinned AxePower.Core at 2.1.0. It was regenerated.
3. **A shutdown test was timing-sensitive.** `nvidia-smi` helpers from a sample in flight can take
   a few seconds to exit. The test now waits up to 10 s, and still fails on anything that stays.
4. **Setup.exe had no version resource.** Added (`VersionInfoVersion` and related keys).
5. **License, security policy and privacy policy were not installed.** Added.

## Manual sign-offs

These need clean Windows VMs or the owner's PC. None has been run, and none is signed off.

| # | Test | How | Result | Signed off by |
|---|---|---|---|---|
| R1 | Clean install | Fresh Windows 11 VM (and Windows 10 22H2), run Setup, launch | not run | |
| R2 | First run | On R1: the welcome appears first; no UAC, startup task, plan change or network before Continue (`OWNER_CHECKS` A11) | not run | |
| R3 | Upgrade from the latest public version | VM with 2.1.0 installed and used; install this build; settings kept, no welcome | not run | |
| R4 | Repair / reinstall | Run the same Setup again over the install | not run | |
| R5 | Uninstall | Apps & features › Uninstall; app files and firewall rule gone; user data kept as documented | not run | |
| R6 | Rollback after failed install | Cancel Setup mid-copy, or kill it; the previous version still runs | not run | |
| R7 | Standard-user launch | Standard account, "Run as administrator" off: the app runs, and admin features say why they need elevation | not run | |
| R8 | Offline smoke test | Network off: launch, Dashboard, Settings, check for updates (plain "offline" message, `OWNER_CHECKS` A10), Software Hub install (WinGet offline reason) | not run | |
| R9 | Optional services unavailable | Block `*.supabase.co`, `api.github.com`, `music.youtube.com` (hosts file): sign-in, update check and Music search each fail with their own plain message; nothing else breaks | not run | |
| R10 | Signed artifacts | After H1: Authenticode valid and timestamped on AxePower.exe, Setup.exe and the uninstaller; the release gate passes | blocked by H1 | |

## Verdict

- **The code and the pipeline are ready to produce a release:** every automated gate except
  signing passes, and the artifacts are consistent.
- **This candidate cannot be released as it stands:**
  - it is unsigned (H1, an automatic NO-GO in Prompt 35);
  - none of the manual Windows tests R1–R10 has been signed off;
  - Prompts 32–33's changes are not yet committed.
