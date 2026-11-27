$ErrorActionPreference = 'Stop'

Write-Host "========================================="
Write-Host "AxePower Verification Script"
Write-Host "========================================="

# Safely locate the repository root
$repoRoot = $PSScriptRoot
while (-not (Test-Path (Join-Path $repoRoot "AxePower.sln"))) {
    $parent = Split-Path $repoRoot -Parent
    if (-not $parent -or $parent -eq $repoRoot) {
        Write-Error "Could not locate AxePower.sln in any parent directory."
        exit 1
    }
    $repoRoot = $parent
}

Set-Location $repoRoot
Write-Host "Repository root located at: $repoRoot"

Write-Host "`n[Stage 1] .NET Info"
Write-Host "-----------------------------------------"
dotnet --info

Write-Host "`n[Stage 2] Restoring solution"
Write-Host "-----------------------------------------"
dotnet restore AxePower.sln --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[Stage 3] Building Debug configuration"
Write-Host "-----------------------------------------"
dotnet build AxePower.sln -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[Stage 4] Building Release configuration"
Write-Host "-----------------------------------------"
dotnet build AxePower.sln -c Release --no-restore -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[Stage 5] Running Tests"
Write-Host "-----------------------------------------"
dotnet test AxePower.Tests/AxePower.Tests.csproj -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n========================================="
Write-Host "All verification stages passed successfully!"
Write-Host "========================================="
