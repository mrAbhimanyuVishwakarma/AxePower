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
    [string]$PublishDir,

    [Parameter(Mandatory = $false)]
    [string]$DotnetPath,

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
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$versionProperty = ([xml](Get-Content -LiteralPath $propsPath -Raw)).Project.PropertyGroup.ProductVersion
$declaredVersion = ([string]$versionProperty).Trim()
if ($declaredVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props must define a numeric ProductVersion before packaging."
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $declaredVersion }
if ($Version -ne $declaredVersion) {
    throw "Requested version '$Version' does not match the project's version '$declaredVersion'."
}

Write-Host "===============================================================" -ForegroundColor Cyan
Write-Host " AxePower Release Candidate Pipeline - v$Version" -ForegroundColor Cyan
Write-Host " Signing Mode: $(if ($RequireSigned) { 'ENFORCED (Fail-Closed)' } else { 'PERMISSIVE (Development)' })" -ForegroundColor Yellow
Write-Host "===============================================================" -ForegroundColor Cyan

# 2. Verify required tools and prepare fresh output directories.
if ($RequireSigned -and -not ($env:SIGNING_CERT_PATH -or $env:SIGNING_CERT_BASE64 -or $env:SIGNING_CERT_THUMBPRINT)) {
    throw "FAIL-CLOSED: Production mode requires a configured Authenticode signing certificate."
}
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
} else {
    $dotnet = (Get-Item -LiteralPath $DotnetPath).FullName
}

$isccCommand = Get-Command "ISCC.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$iscc = if ($isccCommand) {
    $isccCommand.Source
} else {
    @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $iscc) { throw "Inno Setup compiler (ISCC.exe) was not found." }

function Get-ReleaseDirectory([string]$Path) {
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $repoRoot $Path }
    return [System.IO.Path]::GetFullPath($Path)
}

function Initialize-EmptyDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container) -or
            (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)) {
            throw "Output path is not an empty directory: $Path. Choose a fresh path."
        }
    } else {
        [System.IO.Directory]::CreateDirectory($Path) | Out-Null
    }
}

$resolvedOut = Get-ReleaseDirectory $OutputDir
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = "build/release-publish/v$Version-$([guid]::NewGuid().ToString('N'))"
}
$publishPath = Get-ReleaseDirectory $PublishDir
$outputPrefix = $resolvedOut.TrimEnd('\') + '\'
$publishPrefix = $publishPath.TrimEnd('\') + '\'
if ($outputPrefix.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $publishPrefix.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "PublishDir and OutputDir must be separate directories."
}
Initialize-EmptyDirectory $resolvedOut
Initialize-EmptyDirectory $publishPath

$installerName = "AxePower-v$Version-Setup.exe"
$installerOutput = Join-Path $resolvedOut $installerName
$installerCopy = Join-Path $repoRoot "Installer/Output/$installerName"
if (Test-Path -LiteralPath $installerCopy) {
    throw "An installer already exists at $installerCopy. Move it aside before packaging the same version."
}

# 3. Restore Dependencies (Strict Lock Enforcement)
if (-not $SkipRestore) {
    Write-Host "`n[1/7] Restoring dependencies with lock file enforcement..." -ForegroundColor Yellow
    & $dotnet restore (Join-Path $repoRoot "AxePower.sln") --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore with --locked-mode failed." }
} else {
    Write-Host "`n[1/7] Skipping dependency restore (-SkipRestore specified)." -ForegroundColor DarkGray
}

# 4. Deterministic Build
if (-not $SkipBuild) {
    Write-Host "`n[2/7] Building solution with deterministic compiler flags..." -ForegroundColor Yellow
    & $dotnet build (Join-Path $repoRoot "AxePower.sln") -c Release --no-restore -p:ContinuousIntegrationBuild=true -p:Deterministic=true
    if ($LASTEXITCODE -ne 0) { throw "Deterministic Release build failed." }
} else {
    Write-Host "`n[2/7] Skipping solution build (-SkipBuild specified)." -ForegroundColor DarkGray
}

# 5. Run Tests
if (-not $SkipTests) {
    Write-Host "`n[3/7] Running complete test suite..." -ForegroundColor Yellow
    & $dotnet test (Join-Path $repoRoot "AxePower.Tests/AxePower.Tests.csproj") -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Test suite failed." }
} else {
    Write-Host "`n[3/7] Skipping test suite (-SkipTests specified)." -ForegroundColor DarkGray
}

# 6. Publish x64 Application
Write-Host "`n[4/7] Publishing x64 application binaries..." -ForegroundColor Yellow
# Framework-dependent publish targeting win-x64 so only Windows native binaries are included.
# Without -r win-x64 the output bundles ~47 MB of Linux/macOS/ARM/WASM SQLite and Mono natives.
# --no-build is omitted here because the solution build step targets AnyCPU; the publish step
# needs to resolve win-x64 assets itself. The compile is already cached so this adds only seconds.
& $dotnet publish (Join-Path $repoRoot "AxePower.App/AxePower.App.csproj") -c Release -r win-x64 --self-contained false -p:RestoreLockedMode=true -p:ContinuousIntegrationBuild=true -p:Deterministic=true -o $publishPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
$mainExe = Join-Path $publishPath "AxePower.exe"
if ((Get-Item -LiteralPath $mainExe).VersionInfo.FileVersion -ne "$Version.0") {
    throw "The published executable's file version does not match v$Version."
}

# 7. Sign Application Binaries
Write-Host "`n[5/7] Signing application binaries..." -ForegroundColor Yellow
$binariesToSign = @("AxePower.exe", "AxePower.dll", "AxePower.Core.dll")
foreach ($bin in $binariesToSign) {
    $p = Join-Path $publishPath $bin
    if (Test-Path $p) {
        & (Join-Path $scriptRoot "sign-file.ps1") -FilePath $p -RequireSigned:$RequireSigned
        if ($LASTEXITCODE -ne 0 -and $RequireSigned) {
            throw "Signing failed for $bin."
        }
    }
}

# 8. Build and sign the installer from this run's publish directory.
Write-Host "`nCompiling Inno Setup installer using $iscc..." -ForegroundColor Yellow
& $iscc "/O$resolvedOut" "/DPublishDir=$publishPath" (Join-Path $repoRoot "Installer/AxePower.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
$setupVersionInfo = (Get-Item -LiteralPath $installerOutput).VersionInfo
$setupVersion = [version]::new($setupVersionInfo.FileMajorPart, $setupVersionInfo.FileMinorPart,
    $setupVersionInfo.FileBuildPart, $setupVersionInfo.FilePrivatePart)
if ($setupVersion -ne [version]"$Version.0") {
    throw "The installer's file version does not match v$Version."
}
& (Join-Path $scriptRoot "sign-file.ps1") -FilePath $installerOutput -RequireSigned:$RequireSigned
if ($LASTEXITCODE -ne 0 -and $RequireSigned) { throw "Installer signing failed." }

$releaseArtifacts = [System.Collections.Generic.List[string]]::new()
$releaseArtifacts.Add($installerOutput)

# Signed update manifest: installed copies offer this release as an update only when it verifies.
$defaultUpdateKey = Join-Path $env:USERPROFILE "AxePower-update-signing\update-signing.key.pem"
if ($env:UPDATE_SIGNING_KEY -or (Test-Path -LiteralPath $defaultUpdateKey)) {
    $manifestPath = Join-Path $resolvedOut "update-manifest.json"
    & (Join-Path $scriptRoot "sign-update-manifest.ps1") -Installer $installerOutput -Version $Version -OutFile $manifestPath
    $releaseArtifacts.Add($manifestPath)
} else {
    Write-Host "[WARN] No update-signing key found: update-manifest.json not written, so installed copies will not offer this release as an update." -ForegroundColor Yellow
}

$destExe = Join-Path $resolvedOut "AxePower.exe"
[System.IO.File]::Copy($mainExe, $destExe)
$releaseArtifacts.Add($destExe)

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
& (Join-Path $scriptRoot "generate-provenance.ps1") -OutputPath $provPath -ArtifactPaths $releaseArtifacts -DotnetPath $dotnet

# 12. Generate Draft Release Notes
Write-Host "Generating draft release notes..." -ForegroundColor Yellow
$notesPath = Join-Path $resolvedOut "release-notes.md"
& (Join-Path $scriptRoot "generate-release-notes.ps1") -TargetVersion $Version -OutputPath $notesPath -ChecksumsPath $checksumsPath

# 13. Verify Authenticode on the installer and published binaries. JSON release
# metadata is covered by the checksums, and the update manifest has its own signature.
Write-Host "`n[7/7] Executing production release gate..." -ForegroundColor Yellow
$authenticodeArtifacts = @($installerOutput, $destExe)
foreach ($bin in $binariesToSign | Where-Object { $_ -ne 'AxePower.exe' }) {
    $authenticodeArtifacts += Join-Path $publishPath $bin
}
& (Join-Path $scriptRoot "release-gate.ps1") -Artifacts $authenticodeArtifacts -RequireSigned:$RequireSigned
if ($LASTEXITCODE -ne 0) {
    throw "Release gate rejected one or more release candidate artifacts."
}

[System.IO.Directory]::CreateDirectory((Split-Path -Parent $installerCopy)) | Out-Null
[System.IO.File]::Copy($installerOutput, $installerCopy)
Write-Host "Installer copy: $installerCopy" -ForegroundColor Green

Write-Host "`n===============================================================" -ForegroundColor Green
Write-Host " [SUCCESS] Release Candidate v$Version ready in $resolvedOut" -ForegroundColor Green
Write-Host " Artifacts produced:" -ForegroundColor Green
Get-ChildItem -Path $resolvedOut | ForEach-Object {
    Write-Host "  - $($_.Name) ($([math]::Round($_.Length / 1KB, 1)) KB)" -ForegroundColor DarkGray
}
Write-Host "===============================================================" -ForegroundColor Green
