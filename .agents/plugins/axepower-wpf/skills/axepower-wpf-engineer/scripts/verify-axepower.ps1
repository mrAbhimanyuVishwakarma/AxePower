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
dotnet restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[Stage 3] Building Debug configuration"
Write-Host "-----------------------------------------"
dotnet build -c Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[Stage 4] Building Release configuration"
Write-Host "-----------------------------------------"
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[Stage 5] Running Tests"
Write-Host "-----------------------------------------"
if (Test-Path "AxePower.Tests\AxePower.Tests.csproj") {
    dotnet test -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    Write-Host "No test project found at AxePower.Tests\AxePower.Tests.csproj. Skipping tests."
}

Write-Host "`n========================================="
Write-Host "All verification stages passed successfully!"
Write-Host "========================================="
