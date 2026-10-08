# AxePower Code Signing Architecture & Pipeline

## Overview
AxePower produces native Windows executables (`AxePower.exe`), managed/native DLLs (`AxePower.Core.dll`, `AxePower.dll`), and an Inno Setup installer (`AxePower-v*-Setup.exe`). Because AxePower performs low-level hardware communication, power plan manipulation, and system service interactions, trusted Authenticode signing is required to protect users against tampering, satisfy Windows SmartScreen reputation, and prevent execution warnings on clean Windows installations.

This document describes the code signing architecture, fail-closed release gating, local developer behavior, verification commands, and the external configuration required to activate production signing.

---

## 1. Single External Action Required By Owner

> [!IMPORTANT]
> **Action Required**: The repository owner must acquire an Authenticode Code Signing Certificate (or enroll in Azure Trusted Signing) and register the credentials as repository environment secrets. **No certificates or keys are stored in source control.**

To configure production signing:
1. **Acquire a Code Signing Certificate**:
   - Obtain an EV or standard Code Signing Certificate from a Microsoft-trusted Certificate Authority (e.g., DigiCert, Sectigo, GlobalSign) or configure an [Azure Trusted Signing](https://learn.microsoft.com/en-us/azure/trusted-signing/) account.
2. **Configure CI Secrets in GitHub**:
   In GitHub Repository Settings -> Secrets and variables -> Actions (scoped to a protected `release` environment):
   - `SIGNING_CERT_BASE64`: The PFX certificate bundle exported as a Base64-encoded string (without line breaks), **OR** configure an HSM / Azure Trusted Signing action.
   - `SIGNING_CERT_PASSWORD`: The private key password protecting the PFX.
   - `SIGNING_EXPECTED_SUBJECT`: The exact or regex publisher name (e.g., `CN=Abhimanyu Vishwakarma` or company legal entity).
   - *(Optional)* `SIGNING_RFC3161_URL`: Custom RFC 3161 timestamp server URL (defaults to `http://timestamp.digicert.com`).

---

## 2. Signing Architecture & Artifact Scope

The signing pipeline signs the following artifacts in sequence:
1. **Application Executable & Assemblies**:
   - `publish/AxePower.exe`
   - `publish/AxePower.Core.dll`
   - `publish/AxePower.dll`
2. **Inno Setup Uninstaller**:
   - Inno Setup's `SignedUninstaller=yes` and `SignTool=powershell ... scripts/sign-file.ps1 ...` directive signs `unins000.exe` when creating the installer package.
3. **Inno Setup Installer**:
   - The compiled installer `Installer/Output/AxePower-v*-Setup.exe` is signed using `scripts/sign-file.ps1`.

### Digest & Timestamping Standard
- **Digest Algorithm**: SHA-256 (`/fd SHA256`). SHA-1 is prohibited.
- **Timestamp Standard**: RFC 3161 timestamping (`/tr <url> /td SHA256`).
- **Default Timestamp Server**: `http://timestamp.digicert.com` (configurable via `SIGNING_RFC3161_URL` or `-TimestampServer`).

---

## 3. Local Developer Behavior (Zero-Config Graceful Skip)

Developers building AxePower locally do **not** need a code signing certificate.

- **Developer Builds (`AXEPOWER_RELEASE_MODE=0` or unset)**:
  - If no certificate thumbprint or PFX environment variable is detected, `scripts/sign-file.ps1` emits an informational yellow notice:
    ```text
    [SKIP] No signing certificate configured. Proceeding with unsigned development build.
    ```
  - The script exits cleanly with code `0`.
  - Inno Setup compiles without error even when `SignTool` is not configured.
- **Testing Signing Locally**:
  - Developers with a test self-signed certificate can test the pipeline by setting:
    ```powershell
    $env:SIGNING_CERT_PATH = "C:\path\to\dev-test.pfx"
    $env:SIGNING_CERT_PASSWORD = "test-password"
    pwsh scripts/sign-file.ps1 -FilePath "publish/AxePower.exe"
    ```

---

## 4. Production Release Gate (Fail-Closed)

In production release mode (`AXEPOWER_RELEASE_MODE=1` or when invoked with `-RequireSigned`):
- `scripts/sign-file.ps1` **fails closed** (exit code `1`) if signing credentials are missing or signing fails.
- `scripts/release-gate.ps1` evaluates all candidate release artifacts:
  1. Verifies that the required artifacts (`AxePower.exe`, `AxePower.dll`, `AxePower.Core.dll`, and installer `.exe`) exist.
  2. Verifies that each artifact has a valid Authenticode signature.
  3. Verifies that the signer subject matches `SIGNING_EXPECTED_SUBJECT` (if specified).
  4. Verifies that an RFC 3161 timestamp certificate is present.
  5. Runs `signtool verify /pa` where Windows SDK tools are available.
  6. Aborts the release and exits with code `1` if any file is unsigned or invalid.

---

## 5. CI / Release Secret Boundaries & Least Privilege

To ensure maximum security:
- **No Secrets in Source Control**: Git policies reject `.pfx`, `.p12`, `.cer`, `.pem`, and `.key` files.
- **Protected Environment**: Release jobs run in a GitHub Actions environment named `release` requiring manual approval by repository maintainers.
- **Ephemeral Credentials**: If Base64 PFX is passed via `SIGNING_CERT_BASE64`, `scripts/sign-file.ps1` creates a temporary file in `$env:TEMP` with restricted access, uses it to sign, and securely deletes and overwrites the file in a `finally` block.
- **Read-Only Default Permissions**: Pull request and CI workflows run with `permissions: contents: read` and cannot access release environment secrets.

---

## 6. Verification Commands

### PowerShell `Get-AuthenticodeSignature`
To inspect and verify an artifact signature:
```powershell
# Single file verification
$sig = Get-AuthenticodeSignature -FilePath "publish/AxePower.exe"
$sig.Status             # Expected: 'Valid'
$sig.SignerCertificate  # Displays Subject, Issuer, Thumbprint
$sig.TimeStamperCertificate # Displays Timestamp CA

# Automated script verification
pwsh scripts/verify-signature.ps1 -FilePath "publish/AxePower.exe" -ExpectedSubject "Abhimanyu Vishwakarma"
```

### Windows SDK `signtool verify`
To perform strict WinVerifyTrust / Authenticode validation:
```cmd
signtool verify /pa /v "publish\AxePower.exe"
signtool verify /pa /v "Installer\Output\AxePower-v2.1.1-Setup.exe"
```

### Running the Release Gate
```powershell
# Enforce full release validation on published outputs
pwsh scripts/release-gate.ps1 -PublishDir "publish" -InstallerDir "Installer/Output" -RequireSigned
```

---

## 7. Pipeline Script Reference

| Script | Purpose |
|---|---|
| [`scripts/sign-file.ps1`](file:///d:/Projects/AxePower/scripts/sign-file.ps1) | Signs a single file using `signtool.exe` or `Set-AuthenticodeSignature` with SHA-256 + RFC 3161 timestamping. Fails closed when `-RequireSigned` is set. |
| [`scripts/verify-signature.ps1`](file:///d:/Projects/AxePower/scripts/verify-signature.ps1) | Verifies Authenticode status, publisher identity, timestamp, and runs `signtool verify /pa`. |
| [`scripts/release-gate.ps1`](file:///d:/Projects/AxePower/scripts/release-gate.ps1) | Release gate that iterates through all release binaries and installers, failing closed if any artifact fails signature validation. |
