<#
.SYNOPSIS
    Generates a CycloneDX 1.5 Software Bill of Materials (SBOM) for AxePower.
.DESCRIPTION
    Inspects locked dependencies in packages.lock.json across production projects
    and outputs a compliant CycloneDX JSON SBOM containing components, purls, and hashes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "artifacts/release/sbom.cdx.json",

    [Parameter(Mandatory = $false)]
    [string]$ProductVersion = "2.1.0"
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path

Write-Host "Generating CycloneDX SBOM for AxePower v$ProductVersion..." -ForegroundColor Cyan

$lockFiles = @(
    (Join-Path $repoRoot "AxePower.App/packages.lock.json"),
    (Join-Path $repoRoot "AxePower.Core/packages.lock.json")
)

$components = [System.Collections.Generic.Dictionary[string, hashtable]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($lf in $lockFiles) {
    if (-not (Test-Path $lf)) {
        Write-Warning "Lock file not found: $lf"
        continue
    }

    $lockJson = Get-Content $lf -Raw | ConvertFrom-Json
    if ($null -eq $lockJson.dependencies) { continue }

    foreach ($tfm in $lockJson.dependencies.PSObject.Properties) {
        foreach ($pkg in $tfm.Value.PSObject.Properties) {
            $pkgName = $pkg.Name
            $pkgData = $pkg.Value
            $version = $pkgData.resolved

            if ([string]::IsNullOrWhiteSpace($version)) { continue }

            $key = "$pkgName@$version"
            if (-not $components.ContainsKey($key)) {
                $hashes = @()
                if (-not [string]::IsNullOrWhiteSpace($pkgData.contentHash)) {
                    try {
                        $hashBytes = [System.Convert]::FromBase64String($pkgData.contentHash)
                        $sha512Hex = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
                        $hashes += @{
                            alg = "SHA-512"
                            content = $sha512Hex
                        }
                    } catch { }
                }

                $component = @{
                    type = "library"
                    name = $pkgName
                    version = $version
                    purl = "pkg:nuget/$pkgName@$version"
                    scope = "required"
                }

                if ($hashes.Count -gt 0) {
                    $component["hashes"] = $hashes
                }

                $components[$key] = $component
            }
        }
    }
}

$bom = @{
    '$schema' = "http://cyclonedx.org/schema/bom-1.5.schema.json"
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    serialNumber = "urn:uuid:" + [System.Guid]::NewGuid().ToString()
    version = 1
    metadata = @{
        timestamp = [System.DateTime]::UtcNow.ToString("o")
        tools = @(
            @{
                vendor = "AxePower"
                name = "generate-sbom"
                version = "1.0.0"
            }
        )
        component = @{
            type = "application"
            name = "AxePower"
            version = $ProductVersion
            description = "Windows Power Optimization & Management Suite"
        }
    }
    components = @($components.Values)
}

$targetDir = [System.IO.Path]::GetDirectoryName((Resolve-Path -Path "." -ErrorAction SilentlyContinue).Path)
if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $resolvedOut = Join-Path $repoRoot $OutputPath
} else {
    $resolvedOut = $OutputPath
}

$outDir = [System.IO.Path]::GetDirectoryName($resolvedOut)
if (-not (Test-Path $outDir)) {
    [System.IO.Directory]::CreateDirectory($outDir) | Out-Null
}

$json = $bom | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($resolvedOut, $json, [System.Text.Encoding]::UTF8)

Write-Host "CycloneDX SBOM successfully written to: $resolvedOut" -ForegroundColor Green
Write-Host "Total cataloged components: $($components.Count)" -ForegroundColor Green
