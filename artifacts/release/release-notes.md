# AxePower v2.1.2

### Added
- Cryptographically verified application updates requiring pinned ECDSA P-256 signatures, SHA-256 digest validation, and Authenticode publisher checks.
- Hardened .NET 8 Desktop Runtime bootstrap in installer preventing arbitrary execution, open redirects, and unsigned packages.
- Release candidate pipeline generating CycloneDX SBOM, SHA-256 checksums, and machine-readable provenance.

### Changed
- Removed insecure MZ-only header checks across all update and prerequisite execution paths.
- Enforced fail-closed release gates in production mode.

---
## Security & Verification
- **Authenticode**: All executables and installers are signed with SHA-256 and RFC 3161 timestamps.
- **SBOM**: CycloneDX 1.5 Software Bill of Materials is provided in `sbom.cdx.json`.
- **Provenance**: Machine-readable build provenance is provided in `provenance.json`.

### SHA-256 Checksums
```text
6daa3272c365a90557937a2426291ffe4b2e9d66ddd9b63bed2880b4b1eff096 *AxePower-v2.1.2-Setup.exe
6ba23093fdfc540922552896523f747c8cc19e276c5d665cadf35b94e0d98980 *update-manifest.json
8c3c303694dd0b00de83ef2c096372290b638c4e6477fe693259aeb89060e99f *AxePower.exe
8c0337edc60fe690c37cf184ce5705a843706d1ec5ce843f693267a43f927c83 *sbom.cdx.json
```

### Verify Release Artifacts
```powershell
# Verify file checksums
Get-FileHash -Algorithm SHA256 AxePower-v2.1.2-Setup.exe

# Verify Authenticode digital signature
Get-AuthenticodeSignature AxePower-v2.1.2-Setup.exe
```
