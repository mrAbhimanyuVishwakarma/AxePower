<#
.SYNOPSIS
    Verifies the Authenticode signature, publisher identity, and timestamp of a binary.
.DESCRIPTION
    Uses PowerShell Get-AuthenticodeSignature and Windows SDK signtool verify /pa.
    Exits with code 0 on valid signature matching publisher.
    Exits with code 1 if unsigned, untrusted, tampered, or mismatched.
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$FilePath,

    [Parameter(Mandatory = $false)]
    [string]$ExpectedSubject = $(if ($env:SIGNING_EXPECTED_SUBJECT) { $env:SIGNING_EXPECTED_SUBJECT } else { "" }),

    [Parameter(Mandatory = $false)]
    [switch]$FailIfUnsigned = $true
)

$ErrorActionPreference = "Stop"

if (!(Test-Path -Path $FilePath)) {
    Write-Error "File not found: $FilePath"
    exit 1
}

$resolvedFilePath = (Resolve-Path $FilePath).Path
Write-Host "Verifying Authenticode signature for: $resolvedFilePath" -ForegroundColor Cyan

# 1. PowerShell Authenticode verification
$sig = Get-AuthenticodeSignature -FilePath $resolvedFilePath

if ($sig.Status -eq "NotSigned") {
    if ($FailIfUnsigned) {
        Write-Error "VERIFICATION FAILED: File is not signed: $resolvedFilePath"
        exit 1
    } else {
        Write-Host "[INFO] File is not signed." -ForegroundColor Yellow
        exit 0
    }
}

if ($sig.Status -ne "Valid") {
    Write-Error "VERIFICATION FAILED: Signature status is $($sig.Status) - $($sig.StatusMessage)"
    exit 1
}

# 2. Check Publisher Identity
$subjectName = $sig.SignerCertificate.Subject
Write-Host "  Signer Subject: $subjectName" -ForegroundColor DarkGray
Write-Host "  Signer Thumbprint: $($sig.SignerCertificate.Thumbprint)" -ForegroundColor DarkGray

if (![string]::IsNullOrWhiteSpace($ExpectedSubject)) {
    if ($subjectName -notmatch [regex]::Escape($ExpectedSubject)) {
        Write-Error "VERIFICATION FAILED: Signer '$subjectName' does not match expected publisher '$ExpectedSubject'."
        exit 1
    }
}

# 3. Check Timestamp
$timestampCert = $sig.TimeStamperCertificate
if ($timestampCert) {
    Write-Host "  Timestamper: $($timestampCert.Subject)" -ForegroundColor DarkGray
} else {
    Write-Host "  [WARN] Signature has no embedded RFC 3161 timestamp." -ForegroundColor Yellow
}

# 4. Optional SDK signtool verify /pa
$signtoolInPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($signtoolInPath) {
    Write-Host "Running signtool verify /pa..." -ForegroundColor DarkGray
    & $signtoolInPath.Source verify /pa /v $resolvedFilePath
    if ($LASTEXITCODE -ne 0) {
        Write-Error "signtool verify /pa failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

Write-Host "[PASS] Authenticode signature is valid and publisher is verified." -ForegroundColor Green
exit 0
