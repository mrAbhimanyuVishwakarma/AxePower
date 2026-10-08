<#
.SYNOPSIS
    Writes the signed update-manifest.json for a release installer.

.DESCRIPTION
    Records the installer's file name, exact size and SHA-256, signs them with the update-signing
    private key, checks the signature against the public key pinned in the app, and writes
    update-manifest.json. Upload it to the same GitHub release as the installer; AxePower offers
    the update only when the manifest verifies and the downloaded installer matches it.

    The private key comes from -KeyFile (default %USERPROFILE%\AxePower-update-signing\
    update-signing.key.pem) or the UPDATE_SIGNING_KEY environment variable (PEM text).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\sign-update-manifest.ps1 `
        -Installer Installer\Output\AxePower-v2.1.2-Setup.exe -Version 2.1.2
#>
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$KeyFile = (Join-Path $env:USERPROFILE "AxePower-update-signing\update-signing.key.pem"),
    [string]$OutFile
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$toolArgs = @("sign", "--installer", (Resolve-Path $Installer).Path, "--version", $Version, "--repo", $repo)
if (-not $env:UPDATE_SIGNING_KEY) { $toolArgs += @("--key-file", $KeyFile) }
if ($OutFile) { $toolArgs += @("--out", $OutFile) }

dotnet run --project (Join-Path $repo "tools\AxePower.UpdateSigning") -c Release -- @toolArgs
if ($LASTEXITCODE -ne 0) { throw "Signing the update manifest failed (exit code $LASTEXITCODE)." }
