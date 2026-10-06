<#
.SYNOPSIS
    Release gate that verifies all production artifacts are signed and valid.
.DESCRIPTION
    Validates Authenticode signatures, publisher identities, and timestamps on release binaries
    (AxePower.exe, core/app DLLs, Inno Setup installer).
    Fails closed (exit code 1) in release mode if any artifact is unsigned, invalid, tampered, or missing.
    Allows graceful inspection in local development mode when -AllowUnsigned is explicitly specified.
#>
param(
    [Parameter(Mandatory = $false)]
    [string[]]$Artifacts,

    [Parameter(Mandatory = $false)]
    [string]$PublishDir = "publish",

    [Parameter(Mandatory = $false)]
    [string]$InstallerDir = "Installer/Output",

    [Parameter(Mandatory = $false)]
    [string]$ExpectedSubject = $(if ($env:SIGNING_EXPECTED_SUBJECT) { $env:SIGNING_EXPECTED_SUBJECT } else { "" }),

    [Parameter(Mandatory = $false)]
    [switch]$RequireSigned = $(if ($env:AXEPOWER_RELEASE_MODE -in @("0", "false", "False") -or $env:AXEPOWER_RELEASE_MODE -eq $null) { $true } else { $true }),

    [Parameter(Mandatory = $false)]
    [switch]$AllowUnsigned = $false
)

$ErrorActionPreference = "Stop"

$scriptRoot = $PSScriptRoot
$verifyScript = Join-Path $scriptRoot "verify-signature.ps1"
if (!(Test-Path $verifyScript)) {
    Write-Error "verify-signature.ps1 not found at $verifyScript"
    exit 1
}

$effectiveRequireSigned = $RequireSigned -and (!$AllowUnsigned)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " AxePower Production Release Gate" -ForegroundColor Cyan
Write-Host " Mode: $(if ($effectiveRequireSigned) { 'ENFORCING SIGNATURES (Fail-Closed)' } else { 'PERMISSIVE (Development/Unsigned Allowed)' })" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Cyan

$candidateArtifacts = [System.Collections.Generic.List[string]]::new()

if ($Artifacts -and $Artifacts.Count -gt 0) {
    foreach ($item in $Artifacts) {
        if (Test-Path $item) {
            $candidateArtifacts.Add((Resolve-Path $item).Path)
        } else {
            Write-Error "Specified release artifact does not exist: $item"
            exit 1
        }
    }
} else {
    # Scan standard publish directory
    if (Test-Path $PublishDir) {
        $keyFiles = @("AxePower.exe", "AxePower.dll", "AxePower.Core.dll")
        foreach ($kf in $keyFiles) {
            $p = Join-Path $PublishDir $kf
            if (Test-Path $p) {
                $candidateArtifacts.Add((Resolve-Path $p).Path)
            }
        }
    }

    # Scan installer directory
    if (Test-Path $InstallerDir) {
        $setupFiles = Get-ChildItem -Path $InstallerDir -Filter "*Setup*.exe" -File
        foreach ($sf in $setupFiles) {
            $candidateArtifacts.Add($sf.FullName)
        }
    }
}

if ($candidateArtifacts.Count -eq 0) {
    if ($effectiveRequireSigned) {
        Write-Error "RELEASE GATE FAILED: No release artifacts found to verify in '$PublishDir' or '$InstallerDir'."
        exit 1
    } else {
        Write-Host "[WARN] No release artifacts found in standard locations. Release gate skipped for dev." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Found $($candidateArtifacts.Count) artifact(s) to verify:" -ForegroundColor Cyan
foreach ($artifact in $candidateArtifacts) {
    Write-Host " - $artifact" -ForegroundColor DarkGray
}

$failedCount = 0
$passedCount = 0

foreach ($artifact in $candidateArtifacts) {
    Write-Host "`n--> Verifying $([System.IO.Path]::GetFileName($artifact))..." -ForegroundColor Cyan
    try {
        & $verifyScript -FilePath $artifact -ExpectedSubject $ExpectedSubject -FailIfUnsigned:$effectiveRequireSigned
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[FAIL] Verification script exited with code $LASTEXITCODE for $artifact" -ForegroundColor Red
            $failedCount++
        } else {
            $passedCount++
        }
    } catch {
        Write-Host "[FAIL] $($_.Exception.Message)" -ForegroundColor Red
        $failedCount++
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Release Gate Summary" -ForegroundColor Cyan
Write-Host " Total artifacts: $($candidateArtifacts.Count)"
Write-Host " Passed: $passedCount" -ForegroundColor Green
Write-Host " Failed: $failedCount" -ForegroundColor $(if ($failedCount -eq 0) { "Green" } else { "Red" })
Write-Host "========================================" -ForegroundColor Cyan

if ($failedCount -gt 0) {
    Write-Error "RELEASE GATE FAILED: $failedCount artifact(s) failed Authenticode verification. Production release aborted."
    exit 1
}

Write-Host "[SUCCESS] All production artifacts passed the release gate." -ForegroundColor Green
exit 0
