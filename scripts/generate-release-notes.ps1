<#
.SYNOPSIS
    Generates draft release notes from CHANGELOG.md for a given version.
.DESCRIPTION
    Extracts the relevant version section from CHANGELOG.md and appends
    artifact checksums, SBOM reference, and signature verification guidance.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$TargetVersion = "2.1.0",

    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "artifacts/release/release-notes.md",

    [Parameter(Mandatory = $false)]
    [string]$ChecksumsPath = "artifacts/release/SHA256SUMS"
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"

if (-not (Test-Path $changelogPath)) {
    throw "CHANGELOG.md not found at: $changelogPath"
}

Write-Host "Extracting release notes for version $TargetVersion from CHANGELOG.md..." -ForegroundColor Cyan

$lines = Get-Content $changelogPath
$inTarget = $false
$extractedLines = [System.Collections.Generic.List[string]]::new()

$cleanVersion = $TargetVersion.TrimStart('v')

foreach ($line in $lines) {
    if ($line -match "^##\s+\[(.*?)\]") {
        $secVer = $matches[1].Trim()
        if ($secVer -eq $cleanVersion -or ($secVer -eq "Unreleased" -and $cleanVersion -eq "Unreleased")) {
            $inTarget = $true
            continue
        } else {
            if ($inTarget) {
                break
            }
        }
    }

    if ($inTarget) {
        $extractedLines.Add($line)
    }
}

# If not found specifically, fallback to Unreleased or top section
if ($extractedLines.Count -eq 0) {
    Write-Warning "Version [$cleanVersion] not found in CHANGELOG.md, checking [Unreleased]..."
    $inTarget = $false
    foreach ($line in $lines) {
        if ($line -match "^##\s+\[Unreleased\]") {
            $inTarget = $true
            continue
        } elseif ($line -match "^##\s+\[") {
            if ($inTarget) { break }
        }

        if ($inTarget) {
            $extractedLines.Add($line)
        }
    }
}

$body = ($extractedLines -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($body)) {
    $body = "Performance improvements, security hardening, and reliability updates."
}

$notesBuilder = [System.Text.StringBuilder]::new()
$notesBuilder.AppendLine("# AxePower v$cleanVersion") | Out-Null
$notesBuilder.AppendLine("") | Out-Null
$notesBuilder.AppendLine($body) | Out-Null
$notesBuilder.AppendLine("") | Out-Null
$notesBuilder.AppendLine("---") | Out-Null
$notesBuilder.AppendLine('## Security & Verification') | Out-Null
$notesBuilder.AppendLine('- **Authenticode**: All executables and installers are signed with SHA-256 and RFC 3161 timestamps.') | Out-Null
$notesBuilder.AppendLine('- **SBOM**: CycloneDX 1.5 Software Bill of Materials is provided in `sbom.cdx.json`.') | Out-Null
$notesBuilder.AppendLine('- **Provenance**: Machine-readable build provenance is provided in `provenance.json`.') | Out-Null
$notesBuilder.AppendLine('') | Out-Null

if (Test-Path $ChecksumsPath) {
    $notesBuilder.AppendLine('### SHA-256 Checksums') | Out-Null
    $notesBuilder.AppendLine('```text') | Out-Null
    $notesBuilder.AppendLine((Get-Content $ChecksumsPath -Raw).Trim()) | Out-Null
    $notesBuilder.AppendLine('```') | Out-Null
    $notesBuilder.AppendLine('') | Out-Null
}

$notesBuilder.AppendLine('### Verify Release Artifacts') | Out-Null
$notesBuilder.AppendLine('```powershell') | Out-Null
$notesBuilder.AppendLine('# Verify file checksums') | Out-Null
$notesBuilder.AppendLine("Get-FileHash -Algorithm SHA256 AxePower-v$cleanVersion-Setup.exe") | Out-Null
$notesBuilder.AppendLine('') | Out-Null
$notesBuilder.AppendLine('# Verify Authenticode digital signature') | Out-Null
$notesBuilder.AppendLine("Get-AuthenticodeSignature AxePower-v$cleanVersion-Setup.exe") | Out-Null
$notesBuilder.AppendLine('```') | Out-Null

if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $resolvedOut = Join-Path $repoRoot $OutputPath
} else {
    $resolvedOut = $OutputPath
}

$outDir = [System.IO.Path]::GetDirectoryName($resolvedOut)
if (-not (Test-Path $outDir)) {
    [System.IO.Directory]::CreateDirectory($outDir) | Out-Null
}

[System.IO.File]::WriteAllText($resolvedOut, $notesBuilder.ToString(), [System.Text.Encoding]::UTF8)
Write-Host "Draft release notes written to: $resolvedOut" -ForegroundColor Green
