<#
.SYNOPSIS
    Signs an executable or library using Authenticode with SHA-256 and RFC 3161 timestamping.
.DESCRIPTION
    Supports certificate PFX files, Base64 environment variables (CI), or Windows Certificate Store thumbprints.
    Fails closed in release mode (-RequireSigned or AXEPOWER_RELEASE_MODE=1) if signing fails or is unavailable.
    Skips gracefully for local development when no certificate is configured.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [Parameter(Mandatory = $false)]
    [string]$CertPath = $env:SIGNING_CERT_PATH,

    [Parameter(Mandatory = $false)]
    [string]$CertBase64 = $env:SIGNING_CERT_BASE64,

    [Parameter(Mandatory = $false)]
    [string]$CertPassword = $env:SIGNING_CERT_PASSWORD,

    [Parameter(Mandatory = $false)]
    [string]$CertThumbprint = $env:SIGNING_CERT_THUMBPRINT,

    [Parameter(Mandatory = $false)]
    [string]$TimestampServer = $(if ($env:SIGNING_RFC3161_URL) { $env:SIGNING_RFC3161_URL } else { "http://timestamp.digicert.com" }),

    [Parameter(Mandatory = $false)]
    [string]$Description = "AxePower",

    [Parameter(Mandatory = $false)]
    [switch]$RequireSigned = $(if ($env:AXEPOWER_RELEASE_MODE -in @("1", "true", "True")) { $true } else { $false })
)

$ErrorActionPreference = "Stop"

if (!(Test-Path -Path $FilePath)) {
    Write-Error "Target file to sign does not exist: $FilePath"
    exit 1
}

$resolvedFilePath = (Resolve-Path $FilePath).Path
Write-Host "Evaluating signing for: $resolvedFilePath" -ForegroundColor Cyan

# Determine if a signing identity is configured
$hasSigningIdentity = (![string]::IsNullOrWhiteSpace($CertThumbprint)) -or
                      (![string]::IsNullOrWhiteSpace($CertPath)) -or
                      (![string]::IsNullOrWhiteSpace($CertBase64))

if (!$hasSigningIdentity) {
    if ($RequireSigned) {
        Write-Error "FAIL-CLOSED: Production release mode requires Authenticode signing, but no signing certificate or thumbprint is configured."
        exit 1
    }

    Write-Host "[SKIP] No signing certificate configured. Proceeding with unsigned development build." -ForegroundColor Yellow
    exit 0
}

# Find signtool.exe if available
function Find-SignTool {
    $signtoolInPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($signtoolInPath) {
        return $signtoolInPath.Source
    }

    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    )

    foreach ($root in $sdkRoots) {
        if (Test-Path $root) {
            $latest = Get-ChildItem -Path $root -Directory |
                Where-Object { $_.Name -match "^\d+\.\d+\.\d+\.\d+$" } |
                Sort-Object Name -Descending |
                Select-Object -First 1

            if ($latest) {
                $candidate = Join-Path $latest.FullName "x64\signtool.exe"
                if (Test-Path $candidate) {
                    return $candidate
                }
            }
        }
    }

    return $null
}

$signtoolPath = Find-SignTool
$tempCertPath = $null

try {
    # If Base64 certificate is provided, decode into an ephemeral secure file
    if ([string]::IsNullOrWhiteSpace($CertPath) -and ![string]::IsNullOrWhiteSpace($CertBase64)) {
        $tempCertDir = Join-Path ([System.IO.Path]::GetTempPath()) ("axepower-sign-" + [System.Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tempCertDir -Force | Out-Null
        $tempCertPath = Join-Path $tempCertDir "signing_cert.pfx"

        $certBytes = [System.Convert]::FromBase64String($CertBase64.Trim())
        [System.IO.File]::WriteAllBytes($tempCertPath, $certBytes)
        $CertPath = $tempCertPath
    }

    # Execute signing
    if ($signtoolPath) {
        Write-Host "Using signtool: $signtoolPath" -ForegroundColor DarkGray
        $argsList = @("sign", "/fd", "sha256", "/tr", $TimestampServer, "/td", "sha256", "/d", $Description)

        if (![string]::IsNullOrWhiteSpace($CertThumbprint)) {
            $argsList += @("/sha1", $CertThumbprint)
        } elseif (![string]::IsNullOrWhiteSpace($CertPath)) {
            $argsList += @("/f", $CertPath)
            if (![string]::IsNullOrWhiteSpace($CertPassword)) {
                $argsList += @("/p", $CertPassword)
            }
        }

        $argsList += $resolvedFilePath
        & $signtoolPath @argsList

        if ($LASTEXITCODE -ne 0) {
            Write-Error "signtool.exe failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
        }
    } else {
        # Fallback to Set-AuthenticodeSignature
        Write-Host "Using Set-AuthenticodeSignature with RFC 3161 timestamp..." -ForegroundColor DarkGray
        $cert = $null
        if (![string]::IsNullOrWhiteSpace($CertThumbprint)) {
            $cert = Get-ChildItem -Path "Cert:\CurrentUser\My", "Cert:\LocalMachine\My" |
                Where-Object { $_.Thumbprint -eq $CertThumbprint } |
                Select-Object -First 1
        } elseif (![string]::IsNullOrWhiteSpace($CertPath)) {
            $securePass = if (![string]::IsNullOrWhiteSpace($CertPassword)) {
                ConvertTo-SecureString $CertPassword -AsPlainText -Force
            } else { $null }

            $cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
                $CertPath,
                $CertPassword,
                [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet
            )
        }

        if (!$cert) {
            Write-Error "Failed to load signing certificate."
            exit 1
        }

        $sigResult = Set-AuthenticodeSignature -FilePath $resolvedFilePath -Certificate $cert -HashAlgorithm "SHA256" -TimestampServer $TimestampServer
        if ($sigResult.Status -ne "Valid") {
            Write-Error "Set-AuthenticodeSignature failed: $($sigResult.StatusMessage)"
            exit 1
        }
    }

    # Verify signature immediately after signing
    $sig = Get-AuthenticodeSignature -FilePath $resolvedFilePath
    if ($sig.Status -ne "Valid") {
        Write-Error "Post-signing verification failed for $resolvedFilePath. Status: $($sig.Status)"
        exit 1
    }

    Write-Host "[SUCCESS] Authenticode signature verified for: $resolvedFilePath" -ForegroundColor Green
    exit 0
}
finally {
    # Scrub ephemeral certificate file
    if ($tempCertPath -and (Test-Path $tempCertPath)) {
        Remove-Item -Path $tempCertPath -Force -ErrorAction SilentlyContinue
        $parent = Split-Path $tempCertPath -Parent
        if ($parent -and (Test-Path $parent)) {
            Remove-Item -Path $parent -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
