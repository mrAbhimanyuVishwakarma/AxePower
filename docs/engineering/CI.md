# Continuous Integration (CI) Architecture & Local Verification

## 1. Overview

AxePower uses GitHub Actions for continuous integration. The CI workflow is defined in [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) and is designed with:
- **Reproducibility**: Pinned Windows runner (`windows-2022`) and pinned .NET SDK (`8.0.403`).
- **Least-Privilege Security**: Global and job-level permissions explicitly restricted to `contents: read`.
- **Zero Secrets**: No repository secrets, signing certificates, or deployment tokens are used in CI.
- **Fail-Closed Gates**: Automated failure on high/critical package vulnerabilities, version drift, build errors, or test failures.
- **Observability**: Uploads standardized `.trx` test execution logs and Cobertura XML code coverage artifacts.
- **Secret Scanning**: Pinned TruffleHog containerized analysis ensuring no credentials or private tokens are committed.

---

## 2. Maintained Branch Triggers

The workflow automatically triggers on `push` and `pull_request` events targeting:
- `main`
- `master`
- Version/release branches matching pattern `v*` (e.g., `v2.0`, `v2.1`)
- Current active release branch `v2.1.1---monitization-features`
- Manual trigger via `workflow_dispatch`

---

## 3. Locally Equivalent Commands

Every step performed in CI can be run directly on a local developer machine in PowerShell from the repository root (`D:\Projects\AxePower`):

### 1. Environment Diagnostics
```powershell
dotnet --info
```

### 2. NuGet Vulnerability Audit (Fail on High/Critical)
```powershell
$vulnerable = dotnet list AxePower.sln package --vulnerable --include-transitive | Out-String
Write-Host $vulnerable

if ($vulnerable -match "(Critical|High)") {
    Write-Error "High or Critical package vulnerabilities detected in AxePower.sln!"
    exit 1
}
```

### 3. Deprecated Package Audit
```powershell
dotnet list AxePower.sln package --deprecated --include-transitive
```

### 4. Restore Dependencies
```powershell
if (Test-Path "packages.lock.json") {
    dotnet restore AxePower.sln --locked-mode
} else {
    dotnet restore AxePower.sln
}
```

### 5. Build in Release Mode
```powershell
dotnet build AxePower.sln -c Release --no-restore
```

### 6. Run Complete Test Suite with Isolated State & Code Coverage
```powershell
$env:AXEPOWER_TEST_TEMP = "$pwd\artifacts\test-temp"
if (!(Test-Path "$env:AXEPOWER_TEST_TEMP")) {
    New-Item -ItemType Directory -Path "$env:AXEPOWER_TEST_TEMP" -Force | Out-Null
}

dotnet test AxePower.Tests\AxePower.Tests.csproj `
    -c Release `
    --no-build `
    --collect:"XPlat Code Coverage" `
    --results-directory ./artifacts/coverage `
    --logger "trx;LogFileName=test-results.trx"
```

### 7. Local Secret Scanning
Using [TruffleHog](https://github.com/trufflesecurity/trufflehog) or [Gitleaks](https://github.com/gitleaks/gitleaks):
```bash
# Using TruffleHog:
trufflehog git file://. --only-verified

# Or using Gitleaks:
gitleaks detect --verbose
```

---

## 4. Test and Coverage Artifacts

When executed in GitHub Actions, test logs and coverage results are archived and available for download on each run:
- **`test-results`**: Contains `test-results.trx` (Visual Studio Test Results XML).
- **`code-coverage`**: Contains `coverage.cobertura.xml` (standardized cross-platform coverage format).
