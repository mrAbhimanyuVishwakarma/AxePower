<#
.SYNOPSIS
    Generates machine-readable SLSA-inspired build provenance for AxePower releases.
.DESCRIPTION
    Captures exact Git commit, tag, .NET SDK version, dependency lock hashes,
    CI workflow identity, and artifact digests.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputPath = "artifacts/release/provenance.json",

    [Parameter(Mandatory = $false)]
    [string[]]$ArtifactPaths = @()
)

$ErrorActionPreference = 'Stop'

$scriptRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptRoot "..")).Path

Write-Host "Generating machine-readable release provenance..." -ForegroundColor Cyan

# 1. Commit and Git metadata
$commitSha = ""
try {
    $commitSha = (& git -C $repoRoot rev-parse HEAD).Trim()
} catch {
    $commitSha = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { "UNKNOWN" }
}

$gitRef = ""
try {
    $gitRef = (& git -C $repoRoot describe --tags --always).Trim()
} catch {
    $gitRef = if ($env:GITHUB_REF_NAME) { $env:GITHUB_REF_NAME } else { "HEAD" }
}

# 2. .NET SDK Version
$dotnetVersion = (& dotnet --version).Trim()

# 3. Dependency Lock Hash
$appLock = Join-Path $repoRoot "AxePower.App/packages.lock.json"
$coreLock = Join-Path $repoRoot "AxePower.Core/packages.lock.json"
$combinedLockBytes = [System.Collections.Generic.List[byte]]::new()
if (Test-Path $appLock) { $combinedLockBytes.AddRange([System.IO.File]::ReadAllBytes($appLock)) }
if (Test-Path $coreLock) { $combinedLockBytes.AddRange([System.IO.File]::ReadAllBytes($coreLock)) }

$sha = [System.Security.Cryptography.SHA256]::Create()
$lockHash = [System.BitConverter]::ToString($sha.ComputeHash($combinedLockBytes.ToArray())).Replace('-', '').ToLowerInvariant()
$sha.Dispose()

# 4. Workflow Identity
$workflowIdentity = @{
    system = if ($env:GITHUB_ACTIONS -eq "true") { "GitHub Actions" } else { "Local / Offline Environment" }
    workflow = if ($env:GITHUB_WORKFLOW) { $env:GITHUB_WORKFLOW } else { "LocalBuild" }
    runId = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { "local" }
    runAttempt = if ($env:GITHUB_RUN_ATTEMPT) { $env:GITHUB_RUN_ATTEMPT } else { "1" }
    repository = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { "mrAbhimanyuVishwakarma/AxePower" }
    actor = if ($env:GITHUB_ACTOR) { $env:GITHUB_ACTOR } else { $env:USERNAME }
}

# 5. Catalog Artifacts
$artifactEntries = @()
foreach ($ap in $ArtifactPaths) {
    if (Test-Path $ap) {
        $item = Get-Item $ap
        $hash = (Get-FileHash -Path $ap -Algorithm SHA256).Hash.ToLowerInvariant()
        $artifactEntries += @{
            name = $item.Name
            sizeBytes = $item.Length
            sha256 = $hash
        }
    }
}

$provenance = @{
    schemaVersion = "https://axepower.app/provenance/v1"
    timestamp = [System.DateTime]::UtcNow.ToString("o")
    buildDefinition = @{
        buildType = "https://github.com/mrAbhimanyuVishwakarma/AxePower/pipeline/v1"
        deterministic = $true
        source = @{
            repository = "https://github.com/mrAbhimanyuVishwakarma/AxePower"
            commit = $commitSha
            ref = $gitRef
        }
        environment = @{
            dotnetSdkVersion = $dotnetVersion
            runtime = ".NET 8.0 (win-x64)"
            os = if ($env:RUNNER_OS) { $env:RUNNER_OS } else { [System.Environment]::OSVersion.ToString() }
            dependencyLockSha256 = $lockHash
        }
        workflow = $workflowIdentity
    }
    subject = $artifactEntries
}

if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $resolvedOut = Join-Path $repoRoot $OutputPath
} else {
    $resolvedOut = $OutputPath
}

$outDir = [System.IO.Path]::GetDirectoryName($resolvedOut)
if (-not (Test-Path $outDir)) {
    [System.IO.Directory]::CreateDirectory($outDir) | Out-Null
}

$json = $provenance | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($resolvedOut, $json, [System.Text.Encoding]::UTF8)

Write-Host "Release provenance recorded to: $resolvedOut" -ForegroundColor Green
