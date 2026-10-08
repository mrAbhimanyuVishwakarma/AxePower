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
d7f1c4b758e87ba24d9b683f1d3a00d743fc5f0ff22d9c0c72b5e1a7926cf30f *AxePower-v2.1.2-Setup.exe
a60a291ee9d39eb87ce1aad28da8759deee1e62efe5b0f838469f4b0784852da *update-manifest.json
17fff5ae6c613eeddd66e968c34a195e3e6a45ea7adb5c05009eceebd460b651 *AxePower.exe
f26b75e1ba850fa6fb318a25f2c56aac6d8a24453ffd029d1f3e86fa1f51107d *sbom.cdx.json
```

### Verify Release Artifacts
```powershell
# Verify file checksums
Get-FileHash -Algorithm SHA256 AxePower-v2.1.2-Setup.exe

# Verify Authenticode digital signature
Get-AuthenticodeSignature AxePower-v2.1.2-Setup.exe
```
