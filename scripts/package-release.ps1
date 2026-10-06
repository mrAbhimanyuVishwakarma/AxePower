<#
.SYNOPSIS
    Orchestrates building, signing, SBOM generation, checksumming, provenance, and release-gating.
.DESCRIPTION
    Creates a verifiable AxePower Release Candidate package without publishing it.
    Can be run locally by developers (with graceful skip when unsigned) or in CI (fails closed).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "artifacts/release",

    [Parameter(Mandatory = $false)]
    [switch]$RequireSigned = $(if ($env:AXEPOWER_RELEASE_MODE -in @("1", "true", "True")) { $true } else { $false }),

    [Parameter(Mandatory = $false)]
    [switch]$SkipTests = $false,

    [Parameter(Mandatory = $false)]
    [switch]$SkipRestore = $false,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path

# 1. Resolve Target Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    if (Test-Path $propsPath) {
        $propsContent = Get-Content $propsPath -Raw
        if ($propsContent -match "<ProductVersion>(.*?)</ProductVersion>") {
            $Version = $matches[1].Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "2.1.0"
    }
}

Write-Host "===============================================================" -ForegroundColor Cyan
Write-Host " AxePower Release Candidate Pipeline - v$Version" -ForegroundColor Cyan
Write-Host " Signing Mode: $(if ($RequireSigned) { 'ENFORCED (Fail-Closed)' } else { 'PERMISSIVE (Development)' })" -ForegroundColor Yellow
Write-Host "===============================================================" -ForegroundColor Cyan

# 2. Prepare Output Directory
$resolvedOut = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }
if (Test-Path $resolvedOut) {
    Remove-Item -LiteralPath $resolvedOut -Recurse -Force -ErrorAction SilentlyContinue
}
[System.IO.Directory]::CreateDirectory($resolvedOut) | Out-Null

# 3. Restore Dependencies (Strict Lock Enforcement)
if (-not $SkipRestore) {
    Write-Host "`n[1/7] Restoring dependencies with lock file enforcement..." -ForegroundColor Yellow
    & dotnet restore (Join-Path $repoRoot "AxePower.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore with --locked-mode failed." }
} else {
    Write-Host "`n[1/7] Skipping dependency restore (-SkipRestore specified)." -ForegroundColor DarkGray
}

# 4. Deterministic Build
if (-not $SkipBuild) {
    Write-Host "`n[2/7] Building solution with deterministic compiler flags..." -ForegroundColor Yellow
    & dotnet build (Join-Path $repoRoot "AxePower.sln") -c Release --no-restore -p:ContinuousIntegrationBuild=true -p:Deterministic=true
    if ($LASTEXITCODE -ne 0) { throw "Deterministic Release build failed." }
} else {
    Write-Host "`n[2/7] Skipping solution build (-SkipBuild specified)." -ForegroundColor DarkGray
}

# 5. Run Tests
if (-not $SkipTests) {
    Write-Host "`n[3/7] Running complete test suite..." -ForegroundColor Yellow
    & dotnet test (Join-Path $repoRoot "AxePower.Tests/AxePower.Tests.csproj") -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Test suite failed." }
} else {
    Write-Host "`n[3/7] Skipping test suite (-SkipTests specified)." -ForegroundColor DarkGray
}

# 6. Publish x64 Application
Write-Host "`n[4/7] Publishing x64 application binaries..." -ForegroundColor Yellow
$publishDir = Join-Path $repoRoot "publish/win-x64"
if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}
& dotnet publish (Join-Path $repoRoot "AxePower.App/AxePower.App.csproj") -c Release -r win-x64 --no-build -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 7. Sign Application Binaries
Write-Host "`n[5/7] Signing application binaries..." -ForegroundColor Yellow
$binariesToSign = @("AxePower.exe", "AxePower.dll", "AxePower.Core.dll")
foreach ($bin in $binariesToSign) {
    $p = Join-Path $publishDir $bin
    if (Test-Path $p) {
        & (Join-Path $scriptRoot "sign-file.ps1") -FilePath $p -RequireSigned:$RequireSigned
        if ($LASTEXITCODE -ne 0 -and $RequireSigned) {
            throw "Signing failed for $bin."
        }
    }
}

# 8. Build Inno Setup Installer (if ISCC available)
$installerOutput = Join-Path $repoRoot "Installer/Output/AxePower-v$Version-Setup.exe"
$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $defaultIscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    if (Test-Path $defaultIscc) {
        $iscc = Get-Item $defaultIscc
    }
}

if ($iscc) {
    Write-Host "`nCompiling Inno Setup installer using $iscc..." -ForegroundColor Yellow
    & $iscc (Join-Path $repoRoot "Installer/AxePower.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

    # Sign the installer executable
    if (Test-Path $installerOutput) {
        & (Join-Path $scriptRoot "sign-file.ps1") -FilePath $installerOutput -RequireSigned:$RequireSigned
    }
} else {
    Write-Host "`n[WARN] Inno Setup compiler (ISCC.exe) not found in PATH or standard location." -ForegroundColor Yellow
    Write-Host "       Installer packaging step skipped for this environment." -ForegroundColor DarkGray
}

# Copy candidate release artifacts to $resolvedOut
$releaseArtifacts = [System.Collections.Generic.List[string]]::new()

if (Test-Path $installerOutput) {
    $destInstaller = Join-Path $resolvedOut ([System.IO.Path]::GetFileName($installerOutput))
    Copy-Item $installerOutput $destInstaller -Force
    $releaseArtifacts.Add($destInstaller)
}

# Signed update manifest: installed copies offer this release as an update only when it verifies.
$defaultUpdateKey = Join-Path $env:USERPROFILE "AxePower-update-signing\update-signing.key.pem"
if ((Test-Path $installerOutput) -and ($env:UPDATE_SIGNING_KEY -or (Test-Path $defaultUpdateKey))) {
    $manifestPath = Join-Path $resolvedOut "update-manifest.json"
    & (Join-Path $scriptRoot "sign-update-manifest.ps1") -Installer $destInstaller -Version $Version -OutFile $manifestPath
    $releaseArtifacts.Add($manifestPath)
} else {
    Write-Host "[WARN] No update-signing key found: update-manifest.json not written, so installed copies will not offer this release as an update." -ForegroundColor Yellow
}

$mainExe = Join-Path $publishDir "AxePower.exe"
if (Test-Path $mainExe) {
    $destExe = Join-Path $resolvedOut "AxePower.exe"
    Copy-Item $mainExe $destExe -Force
    $releaseArtifacts.Add($destExe)
}

# 9. Generate CycloneDX SBOM
Write-Host "`n[6/7] Generating CycloneDX SBOM..." -ForegroundColor Yellow
$sbomPath = Join-Path $resolvedOut "sbom.cdx.json"
& (Join-Path $scriptRoot "generate-sbom.ps1") -OutputPath $sbomPath -ProductVersion $Version
$releaseArtifacts.Add($sbomPath)

# 10. Generate SHA-256 Checksums
Write-Host "Computing SHA-256 checksums..." -ForegroundColor Yellow
$checksumsPath = Join-Path $resolvedOut "SHA256SUMS"
$checksumLines = [System.Collections.Generic.List[string]]::new()
foreach ($art in $releaseArtifacts) {
    $hash = (Get-FileHash -Path $art -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = [System.IO.Path]::GetFileName($art)
    $checksumLines.Add("$hash *$name")
}
[System.IO.File]::WriteAllLines($checksumsPath, $checksumLines)
Write-Host "Checksums written to: $checksumsPath" -ForegroundColor Green

# 11. Generate Provenance
Write-Host "Generating release provenance..." -ForegroundColor Yellow
$provPath = Join-Path $resolvedOut "provenance.json"
& (Join-Path $scriptRoot "generate-provenance.ps1") -OutputPath $provPath -ArtifactPaths $releaseArtifacts

# 12. Generate Draft Release Notes
Write-Host "Generating draft release notes..." -ForegroundColor Yellow
$notesPath = Join-Path $resolvedOut "release-notes.md"
& (Join-Path $scriptRoot "generate-release-notes.ps1") -TargetVersion $Version -OutputPath $notesPath -ChecksumsPath $checksumsPath

# 13. Execute Release Gate
Write-Host "`n[7/7] Executing production release gate..." -ForegroundColor Yellow
& (Join-Path $scriptRoot "release-gate.ps1") -Artifacts $releaseArtifacts -RequireSigned:$RequireSigned
if ($LASTEXITCODE -ne 0) {
    throw "Release gate rejected one or more release candidate artifacts."
}

Write-Host "`n===============================================================" -ForegroundColor Green
Write-Host " [SUCCESS] Release Candidate v$Version ready in $resolvedOut" -ForegroundColor Green
Write-Host " Artifacts produced:" -ForegroundColor Green
Get-ChildItem -Path $resolvedOut | ForEach-Object {
    Write-Host "  - $($_.Name) ($([math]::Round($_.Length / 1KB, 1)) KB)" -ForegroundColor DarkGray
}
Write-Host "===============================================================" -ForegroundColor Green
