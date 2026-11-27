# AxePower Release Candidate Pipeline

## 1. Overview
The AxePower Release Candidate Pipeline automates the compilation, testing, signing, SBOM generation, checksumming, and provenance verification of release artifacts.

To guarantee security and supply-chain integrity:
- **Clean Tagged Builds**: Build a production candidate from a tag such as `v2.1.5` that matches `Directory.Build.props`. The workflow can also be dispatched manually with the declared version.
- **Deterministic Settings**: Built with `-p:ContinuousIntegrationBuild=true -p:Deterministic=true` and locked dependencies (`dotnet restore --locked-mode`).
- **Verifiable Artifacts**: Generates CycloneDX 1.5 SBOM, SHA-256 checksums, and machine-readable provenance.
- **Fail-Closed Release Gate**: The production workflow requires valid Authenticode signatures and the existing key used to sign AxePower's update manifest.
- **Publication Gate**: Releases are created as **DRAFT** releases only. Final public publication requires explicit maintainer review and manual approval.

---

## 2. Pipeline Architecture

```mermaid
graph TD
    A[Git Tag Push: v* or workflow_dispatch] --> B[Clean Checkout & Diagnostics]
    B --> C[Restore Dependencies: dotnet restore --locked-mode]
    C --> D[Deterministic Build: ContinuousIntegrationBuild=true]
    D --> E[Run Full Test Suite: AxePower.Tests]
    E --> F[Publish x64 Application to a fresh build directory]
    F --> G[Authenticode Sign Application Binaries]
    G --> H[Build Inno Setup Installer: ISCC.exe]
    H --> I[Authenticode Sign Inno Setup Installer]
    I --> W[Sign update-manifest.json for the final installer]
    W --> J[Generate CycloneDX 1.5 SBOM: sbom.cdx.json]
    J --> K[Compute SHA-256 Checksums: SHA256SUMS]
    K --> L[Generate Machine-Readable Provenance: provenance.json]
    L --> M[Generate Draft Release Notes from CHANGELOG.md]
    M --> N[Release Gate: verify-signature.ps1 on all artifacts]
    N -->|Failure| X[Fail Closed: Abort Workflow]
    N -->|Passed| O[Upload Actions Artifact Bundle]
    O --> P[Create DRAFT GitHub Release: draft=true, prerelease=true]
    P --> Q[Manual Maintainer Review & Approval]
    Q --> R[Official Public Release Published]
```

---

## 3. Generated Artifact Manifest

Every release candidate generates the following verifiable bundle in `artifacts/release/`:

| Artifact | Purpose | Standard / Format |
|---|---|---|
| `AxePower-v*-Setup.exe` | Production installer executable | Inno Setup 6, Authenticode Signed |
| `update-manifest.json` | Required companion asset for in-app updates | ECDSA signature over version, installer name, size, and SHA-256 |
| `AxePower.exe` | Application executable in the Windows x64 publish directory | .NET 10 Desktop Runtime, framework-dependent, Authenticode signed for production |
| `sbom.cdx.json` | Software Bill of Materials | CycloneDX 1.5 JSON |
| `provenance.json` | Machine-readable build provenance | SLSA-inspired JSON |
| `SHA256SUMS` | Cryptographic digests of all outputs | Standard GNU/BSD sha256 checksum format |
| `release-notes.md` | Release highlights & verification guide | Extracted from `CHANGELOG.md` |

---

## 4. Verification Commands

### Checksum Verification
```powershell
# Verify all release files match the SHA256SUMS file
Get-FileHash -Algorithm SHA256 (Get-ChildItem -Path artifacts/release -Exclude SHA256SUMS,*.md)
```

### Signature Verification
```powershell
# Verify Authenticode on installer and application
pwsh scripts/verify-signature.ps1 -FilePath "artifacts/release/AxePower-v2.1.5-Setup.exe" -ExpectedSubject "Abhimanyu Vishwakarma"
pwsh scripts/verify-signature.ps1 -FilePath "artifacts/release/AxePower.exe" -ExpectedSubject "Abhimanyu Vishwakarma"
```

### Run the Release Gate
```powershell
pwsh scripts/release-gate.ps1 -Artifacts (Get-ChildItem artifacts/release/*.exe).FullName -RequireSigned
```

---

## 5. Local Developer Dry-Run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Inno Setup 6](https://jrsoftware.org/isinfo.php) to run the release packaging pipeline locally:

```powershell
# Run local packaging using the checkout's declared version
powershell -ExecutionPolicy Bypass -File scripts\package-release.ps1
```
In permissive dev mode:
- Dependencies are restored and built deterministically.
- Tests are executed.
- The script publishes the x64 application to a fresh directory under `build/release-publish/` and passes that path to Inno Setup. The validated installer from `artifacts/release/` is copied to `Installer/Output/`.
- CycloneDX SBOM, SHA-256 checksums, provenance, and release notes are generated.
- Unsigned binaries emit a `[SKIP]` notice and allow developers to inspect the output package in `artifacts/release/`.

To rebuild the same version, archive the existing installer copy and pass a fresh path with `-OutputDir`. The script rejects existing installers or nonempty output directories to protect earlier builds. Keep generated outputs local.

## 6. Publish an in-app update

1. Finish the installer first, including Authenticode signing if applicable. Moving the file is fine. Renaming, signing, or rebuilding it afterward requires a new update manifest.
2. On the release machine, use the existing private key that matches the public key pinned in AxePower. The script reads the configured key and verifies the signature before writing the manifest. See [Signed Updates](../security/SIGNED_UPDATES.md).

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\sign-update-manifest.ps1 `
       -Installer .\Installer\Output\AxePower-v2.1.5-Setup.exe `
       -Version 2.1.5 `
       -OutFile .\Installer\Output\update-manifest.json
   ```

3. In the [AxePower_Download releases repository](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases), create one public stable release tagged `v2.1.5`. Attach **`AxePower-v2.1.5-Setup.exe`** and **`update-manifest.json`** to that same release. Use the [GitHub release description](../release/v2.1.5-github-release.md) and set the release as latest. The draft created by CI in the source repository is a review candidate.
4. Put only one installer executable in the public assets. Earlier app versions may select the first `.exe` returned by GitHub. SBOM, provenance, and an installer checksum are optional. Setup obtains the runtime when needed.
5. Check the public GitHub release and website download. In a supported Windows test installation of v2.1.4, open **Settings → Updates & Maintenance → Check for updates**, verify that v2.1.5 downloads, then apply the update and check the installed version.

Manual Setup remains available for copies built without the matching pinned key. Keep the signing private key on the release machine or in the protected CI secret `UPDATE_SIGNING_KEY`.
