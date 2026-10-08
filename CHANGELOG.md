# Changelog

All notable changes to AxePower are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.1.3] - 2026-10-08

AxePower 2.1.3 is about trust and polish: it asks before it changes anything, explains every
change and every error in plain words, uses less of your PC when you are not looking at it, and
works better with a keyboard and a screen reader. It includes everything from the unreleased 2.1.1
and 2.1.2 builds.

### New
- **Welcome screen on a fresh install.** AxePower now asks before it starts with Windows, runs as
  administrator, sets up its power plans or checks for updates. Nothing happens until you click
  Continue. Existing users keep their settings and won't see it.
- **Trust & Permissions page.** See what AxePower can access, what is listening on your network,
  what it stores, and turn any of it off or delete it in one place.
- **Change preview and history.** Optimizations and Game Mode service changes show exactly what
  will change before you approve, and can be rolled back later from Settings › Change history.
- **Clear confirmations.** Risky actions (uninstalling, debloat scripts, DNS changes, update cache
  reset) now say what will happen, whether it can be undone and whether a restart is needed.
  High-risk ones need a tick before the button works.
- **Diagnostics & Privacy** in Settings: see, export or delete AxePower's logs. Logs never contain
  passwords, tokens, links, emails or your user and computer name.
- **Windows High Contrast and "Animation effects" are followed.** High Contrast switches AxePower to
  its High Contrast skin; with animations off, nothing slides or spins.

### Improved
- **Plain error messages.** Errors say what went wrong and what to do (offline, permission needed,
  file in use…) instead of technical text. A Details button shows and copies the technical part.
  Errors always show, even with notifications turned off, and cancelling is never shown as an
  error.
- **Software Hub** says exactly which apps were installed and which weren't, and why.
- **Lighter in the background.** CPU and network sampling, the Dashboard and Battery refresh only
  run while their page is on screen; nothing runs while AxePower sits in the tray. The Terminal's
  PowerShell starts only when you open the Terminal.
- **Faster Game Mode page**: switching back to it is now instant.
- **Screen readers**: every button, switch and box has a proper name, and keyboard focus is always
  visible. Enter plays a song in Music.
- **Readable colours** in every skin: muted and status text meet contrast guidelines.
- **Clean exit**: AxePower now closes everything it started and removes its tray icon right away.
- **Installer** asks to create a desktop shortcut (ticked by default), and installs the license,
  security policy and privacy policy alongside the app.
- The **update bar** closes itself after 10 seconds.

### Security
- Updates are verified against a signed manifest (pinned key), their size and SHA-256, and the
  downloaded installer is checked again right before it runs.
- The Share Screen viewer can no longer be sent to other websites or open pop-ups.
- Phone Hub / LAN Drop hardened: authenticated sessions, size limits, lockout after wrong tries,
  and a warning on Public networks.
- Remote access for AxePower Mobile is turned off until its connection is private to your account.
- The privacy policy now lists every connection AxePower makes, including the Dashboard's latency
  ping.

### Fixed
- A freeze that could happen when AxePower logged while closing.
- "Install update" no longer closes AxePower if the installer could not be started.
- A fresh install no longer switches your power plan or removes other plans before you agree.

### Known
- Releases are not code-signed yet, so Windows may show "Unknown publisher" or SmartScreen when
  you install.

## [2.1.0] - 2026-10-04

### Added
- Centralized `Directory.Build.props` establishing authoritative versioning, deterministic build flags, and .NET analyzers.
- Deterministic package lock files (`packages.lock.json`) across all production and test projects.
- Automated dependency auditing script (`scripts/audit-dependencies.ps1`) and Dependabot version monitoring.
- Windows CI pipeline (`.github/workflows/ci.yml`) enforcing pinned actions, secret scanning, and warning prevention.
- Code-signing pipeline (`scripts/sign-file.ps1`, `scripts/verify-signature.ps1`, `scripts/release-gate.ps1`) with RFC 3161 SHA-256 timestamping and fail-closed release gating.

### Changed
- Standardized product versioning across `AxePower.App`, `AxePower.Core`, `AxePower.Tests`, and Inno Setup installer.
- Corrected stale `2.0.3` updater fallback versions.

### Security
- Added `SECURITY.md` establishing vulnerability disclosure policy and supported versions.
- Added `LICENSE` (MIT) and `CONTRIBUTING.md`.
- Documented legacy compiler and analyzer warnings in `docs/engineering/WARNINGS_DEBT.md`.

## [2.0.0] - 2026-09-15

### Added
- Unified System Care dashboard and auto-switching power profiles.
- Integrated music player, lyrics service, and system optimization tools.
