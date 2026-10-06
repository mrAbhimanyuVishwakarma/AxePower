<#
.SYNOPSIS
    Creates AxePower's update-signing key pair. Run once.

.DESCRIPTION
    Saves the PRIVATE key outside the repository (default: %USERPROFILE%\AxePower-update-signing\)
    and pins the PUBLIC key in AxePower.Core\Services\Update\UpdateSigningKey.cs. Commit that file
    and rebuild: from then on the app trusts only update manifests signed with this key.

    Keep the private key secret and back it up (a password manager). Losing it means installed
    copies can no longer be updated automatically; leaking it lets someone else sign updates.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\new-update-signing-key.ps1
#>
param(
    [string]$KeyFile = (Join-Path $env:USERPROFILE "AxePower-update-signing\update-signing.key.pem"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$toolArgs = @("new-key", "--key-file", $KeyFile, "--repo", $repo)
if ($Force) { $toolArgs += "--force" }

dotnet run --project (Join-Path $repo "tools\AxePower.UpdateSigning") -c Release -- @toolArgs
if ($LASTEXITCODE -ne 0) { throw "Key generation failed (exit code $LASTEXITCODE)." }
