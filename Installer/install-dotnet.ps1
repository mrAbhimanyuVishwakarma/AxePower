<#
.SYNOPSIS
    Securely downloads, verifies, and installs the Microsoft .NET 10 Desktop Runtime.
.DESCRIPTION
    Enforces HTTPS-only downloads with restricted redirect validation to Microsoft domains.
    Enforces Authenticode digital signature and Microsoft publisher verification before execution.
    Pins the official x64 package SHA-512 and supports an additional SHA-256 check.
    Never relies on MZ header alone to authorize execution.
    Cleans up all temporary staging files in a secure directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Url,

    [Parameter(Mandatory = $false)]
    [string]$PinnedSha256 = $env:DOTNET_RUNTIME_SHA256,

    [Parameter(Mandatory = $false)]
    [switch]$SkipExecution = $false,

    [Parameter(Mandatory = $false)]
    [string[]]$CustomAllowedHosts = @(),

    [Parameter(Mandatory = $false)]
    [switch]$AllowInsecureHttp = $false # Only for local loopback test server harness
)

$ErrorActionPreference = 'Stop'

# Microsoft release feed: https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
$officialUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe'
$officialSha512 = 'f838960962172f1a769c49fa600e6f60f56f8d08a28e3cb878890ffc2e3a7a2e1b65697588e23efa767b8b8e8523ec01f55e875120507dde16d9a0869102dab3'
if ([string]::IsNullOrWhiteSpace($Url)) { $Url = $officialUrl }

Add-Type -AssemblyName System.Net.Http

try { $Host.UI.RawUI.WindowTitle = 'AxePower Setup - Installing .NET 10 Desktop Runtime' } catch { }

$allowedHosts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$defaultAllowedHosts = @(
    "aka.ms",
    "download.visualstudio.microsoft.com",
    "download.microsoft.com",
    "builds.dotnet.microsoft.com",
    "dotnetcli.azureedge.net",
    "dotnetbuilds.azureedge.net",
    "dotnet.microsoft.com",
    "dotnetcli.blob.core.windows.net"
)
foreach ($h in $defaultAllowedHosts) { [void]$allowedHosts.Add($h) }
foreach ($ch in $CustomAllowedHosts) { [void]$allowedHosts.Add($ch) }

Write-Host ''
Write-Host '  ===============================================================' -ForegroundColor Cyan
Write-Host '    AxePower Setup - Installing Required Component' -ForegroundColor Cyan
Write-Host '  ===============================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host '  AxePower requires the Microsoft .NET 10 Desktop Runtime (x64).'
Write-Host '  This window will verify and install the runtime securely.'
Write-Host ''

$tempGuid = [System.Guid]::NewGuid().ToString('N')
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "AxePower_DotNet_$tempGuid"
$installerPath = Join-Path $tempDir "windowsdesktop-runtime-win-x64.exe"

function Validate-DownloadUrl {
    param([Uri]$uri)

    if ($uri.Scheme -ne [System.Uri]::UriSchemeHttps) {
        if (-not ($AllowInsecureHttp -and $uri.IsLoopback)) {
            throw "Security violation: Insecure scheme '$($uri.Scheme)' is prohibited. Only HTTPS is allowed."
        }
    }

    if (-not $allowedHosts.Contains($uri.Host)) {
        if (-not ($AllowInsecureHttp -and $uri.IsLoopback)) {
            throw "Security violation: Host '$($uri.Host)' is not an authorized Microsoft download host."
        }
    }
}

try {
    if ($Url -cne $officialUrl -and -not $SkipExecution) {
        throw "A custom runtime URL can only be used with -SkipExecution."
    }

    [System.IO.Directory]::CreateDirectory($tempDir) | Out-Null

    Write-Host '  [1/3] Connecting to Microsoft download service...' -ForegroundColor Yellow

    # Safe redirect-following loop using HttpClient with AutoRedirect disabled
    $initialUri = [System.Uri]::new($Url)
    Validate-DownloadUrl $initialUri

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [System.TimeSpan]::FromMinutes(5)
    $client.DefaultRequestHeaders.Add("User-Agent", "AxePower-Prerequisite-Installer")

    $currentUri = $initialUri
    $redirectCount = 0
    $maxRedirects = 5
    $response = $null

    while ($redirectCount -lt $maxRedirects) {
        $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $currentUri)
        $response = $client.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()

        $statusCode = [int]$response.StatusCode
        if ($statusCode -in @(301, 302, 303, 307, 308)) {
            $redirectCount++
            $location = $response.Headers.Location
            if ($null -eq $location) {
                throw "Redirect status $statusCode returned with missing Location header."
            }

            $nextUri = if ($location.IsAbsoluteUri) { $location } else { [System.Uri]::new($currentUri, $location) }
            Validate-DownloadUrl $nextUri

            $response.Dispose()
            $currentUri = $nextUri
            continue
        }

        break
    }

    if ($redirectCount -ge $maxRedirects) {
        throw "Too many redirects encountered while downloading runtime installer."
    }

    if (-not $response.IsSuccessStatusCode) {
        throw "Failed to download runtime installer: HTTP $($response.StatusCode) ($([int]$response.StatusCode))."
    }

    Write-Host '  [2/3] Downloading runtime package...' -ForegroundColor Yellow

    # Stream download directly to file
    $contentLength = $response.Content.Headers.ContentLength
    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $fileStream = [System.IO.FileStream]::new(
        $installerPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        64 * 1024)

    try {
        $stream.CopyTo($fileStream)
    } finally {
        $fileStream.Flush()
        $fileStream.Dispose()
        $stream.Dispose()
        $response.Dispose()
        $client.Dispose()
    }

    if (-not (Test-Path $installerPath)) {
        throw "Installer file not found on disk after download."
    }

    $fileSize = (Get-Item $installerPath).Length
    if ($fileSize -lt 64 * 1024) {
        throw "Downloaded payload is unexpectedly small ($fileSize bytes). Expected full installer binary."
    }
    if ($null -ne $contentLength -and $fileSize -ne $contentLength) {
        throw "The runtime download was incomplete ($fileSize of $contentLength bytes)."
    }

    if ($Url -ceq $officialUrl) {
        Write-Host '  Verifying Microsoft SHA-512 package hash...' -ForegroundColor Yellow
        $actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA512).Hash
        if ($actualHash -ne $officialSha512) {
            throw "The runtime does not match Microsoft's published SHA-512 hash."
        }
        Write-Host '  [OK] Microsoft SHA-512 hash verified.' -ForegroundColor Green
    }

    # 1. Pinned Hash Verification (if provided)
    if (-not [string]::IsNullOrWhiteSpace($PinnedSha256)) {
        Write-Host '  Verifying SHA-256 package hash...' -ForegroundColor Yellow
        $actualHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash
        if ($actualHash -ne $PinnedSha256.Trim().ToUpperInvariant()) {
            throw "Package SHA-256 hash mismatch!`nExpected: $PinnedSha256`nActual:   $actualHash"
        }
        Write-Host "  [OK] SHA-256 hash verified: $actualHash" -ForegroundColor Green
    }

    # 2. Authenticode & Microsoft Publisher Verification
    Write-Host '  [3/3] Verifying Microsoft Authenticode signature...' -ForegroundColor Yellow
    $sig = Get-AuthenticodeSignature -FilePath $installerPath

    if ($sig.Status -eq "NotSigned") {
        throw "The downloaded file is not digitally signed. MZ header check alone is not sufficient to execute."
    }

    if ($sig.Status -ne "Valid") {
        throw "The downloaded file is not digitally signed or trusted (Status: $($sig.Status) - $($sig.StatusMessage)). MZ header check alone is not sufficient to execute."
    }

    $signerCert = $sig.SignerCertificate
    if ($null -eq $signerCert) {
        throw "Unable to read Authenticode signer certificate."
    }

    $subject = $signerCert.Subject
    Write-Host "  Signer: $subject" -ForegroundColor DarkGray

    # Verify authorized Microsoft publisher subject
    $isMicrosoft = ($subject -match 'CN=Microsoft Corporation' -or
                    $subject -match 'O=Microsoft Corporation' -or
                    $subject -match 'CN=Microsoft Windows')

    if (-not $isMicrosoft) {
        throw "Security violation: The digital signature was issued to '$subject', not Microsoft Corporation."
    }

    Write-Host '  [OK] Cryptographic signature and Microsoft publisher verified.' -ForegroundColor Green

    if ($SkipExecution) {
        Write-Host '  [SKIP] Skipping execution flag requested (validation harness mode).' -ForegroundColor Cyan
        exit 0
    }

    Write-Host ''
    Write-Host '  Installing .NET 10 Desktop Runtime, please wait...' -ForegroundColor Yellow
    Write-Host ''

    $proc = Start-Process -FilePath $installerPath -ArgumentList '/install', '/quiet', '/norestart' -WindowStyle Hidden -Wait -PassThru

    # 0 = success, 3010 = success reboot required
    if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
        Write-Host '  .NET 10 Desktop Runtime installed successfully.' -ForegroundColor Green
    } else {
        throw "The runtime installer returned exit code $($proc.ExitCode)."
    }

    Write-Host ''
    Write-Host '  Returning to AxePower Setup...' -ForegroundColor Cyan
    Start-Sleep -Seconds 2
    exit 0
}
catch {
    Write-Host ''
    Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Write-Host '  AxePower cannot run without the official Microsoft .NET 10 Desktop Runtime.' -ForegroundColor Yellow
    Write-Host '  Please download and install it manually from:' -ForegroundColor Yellow
    Write-Host '    https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor White
    Write-Host '  (Select ".NET Desktop Runtime" -> "Windows x64")' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  Setup will now abort to protect your system from executing unverified packages.' -ForegroundColor Red
    Write-Host ''
    if (-not $SkipExecution) { Start-Sleep -Seconds 5 }
    exit 1
}
finally {
    if (Test-Path -LiteralPath $installerPath) {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $tempDir) {
        try { [System.IO.Directory]::Delete($tempDir) } catch { }
    }
}
