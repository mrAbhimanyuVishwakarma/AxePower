<#
.SYNOPSIS
    Builds and opens AxePower from the current Git checkout.
.PARAMETER BuildOnly
    Builds and validates the executable without opening the app.
#>
[CmdletBinding()]
param([switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location -LiteralPath $repoRoot
try {
    $branch = git rev-parse --abbrev-ref HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the current AxePower branch.' }
    $commit = git log -1 --oneline
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the current AxePower commit.' }
    $sourceVersion = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.ProductVersion
    Write-Host "AxePower v$sourceVersion | $branch | $commit"

    if (-not $BuildOnly -and (Get-Process -Name AxePower,AxePower.App -ErrorAction SilentlyContinue)) {
        throw 'Exit AxePower from its system tray menu before opening the checkout build.'
    }

    $artifactPath = Join-Path $repoRoot 'build/dev'
    dotnet build AxePower.App/AxePower.App.csproj -c Release --artifacts-path $artifactPath -p:RestoreLockedMode=true --nologo
    if ($LASTEXITCODE -ne 0) { throw 'AxePower build failed.' }

    $exe = Get-Item -LiteralPath (Join-Path $artifactPath 'bin/AxePower.App/release/AxePower.exe')
    if ($exe.VersionInfo.FileVersion -ne "$sourceVersion.0") {
        throw "The built executable's version does not match v$sourceVersion."
    }
    Write-Host "Built $($exe.VersionInfo.ProductVersion) at $($exe.FullName)"

    if (-not $BuildOnly) {
        Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName
    }
}
finally {
    Pop-Location
}
