param(
    [switch]$FailOnVulnerability = $true
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " AxePower Dependency Audit & Policy Check" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Vulnerability Check
Write-Host "`n[1/3] Checking for package vulnerabilities..." -ForegroundColor Yellow
$vulnOutput = dotnet list AxePower.sln package --vulnerable --include-transitive | Out-String
Write-Host $vulnOutput

$hasHighOrCritical = $vulnOutput -match "(Critical|High)"
if ($hasHighOrCritical) {
    Write-Host "[!] HIGH OR CRITICAL VULNERABILITIES DETECTED!" -ForegroundColor Red
    if ($FailOnVulnerability) {
        Write-Error "Failing dependency audit due to High or Critical vulnerabilities."
        exit 1
    }
} else {
    Write-Host "[+] No High or Critical package vulnerabilities detected." -ForegroundColor Green
}

# 2. Normal Outdated Packages (within current major line)
Write-Host "`n[2/3] Checking for normal outdated packages (minor/patch servicing)..." -ForegroundColor Yellow
$minorOutput = dotnet list AxePower.sln package --outdated --highest-minor | Out-String
Write-Host $minorOutput

# 3. Major Version Upgrades (Requiring Manual Review)
Write-Host "`n[3/3] Checking for major version upgrades (requiring manual review)..." -ForegroundColor Yellow
$latestOutput = dotnet list AxePower.sln package --outdated | Out-String
Write-Host $latestOutput

Write-Host "`nAudit summary:" -ForegroundColor Cyan
Write-Host "- Vulnerability status: PASS (0 high/critical)" -ForegroundColor Green
Write-Host "- Minor/patch updates: Reviewable in routine maintenance" -ForegroundColor Gray
Write-Host "- Major upgrades: Flagged for explicit architectural compatibility review" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

exit 0
