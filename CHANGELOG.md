# Changelog

All notable changes to AxePower are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Cryptographically verified application updates requiring pinned ECDSA P-256 signatures, SHA-256 digest validation, and Authenticode publisher checks.
- Hardened .NET 8 Desktop Runtime bootstrap in installer preventing arbitrary execution, open redirects, and unsigned packages.
- Release candidate pipeline generating CycloneDX SBOM, SHA-256 checksums, and machine-readable provenance.

### Changed
- Removed insecure MZ-only header checks across all update and prerequisite execution paths.
- Enforced fail-closed release gates in production mode.

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
