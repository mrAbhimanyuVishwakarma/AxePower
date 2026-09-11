# Installs the Microsoft .NET 8 Desktop Runtime that AxePower needs.
# Launched by AxePower Setup in a visible window so the user can see progress,
# and closes itself when finished.

$ErrorActionPreference = 'Stop'

try { $Host.UI.RawUI.WindowTitle = 'AxePower Setup - Installing .NET 8 Desktop Runtime' } catch { }

$url = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
$out = Join-Path $env:TEMP 'axepower-windowsdesktop-runtime-8-win-x64.exe'

Write-Host ''
Write-Host '  ===============================================================' -ForegroundColor Cyan
Write-Host '    AxePower Setup - installing a required component' -ForegroundColor Cyan
Write-Host '  ===============================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host '  AxePower needs the Microsoft .NET 8 Desktop Runtime.'
Write-Host '  This window closes by itself, then Setup continues.'
Write-Host ''

try {
    Write-Host '  [1/2] Downloading from Microsoft (about 58 MB)...' -ForegroundColor Yellow
    Write-Host ''

    # curl.exe ships with Windows 10 1803+ and renders a live progress bar
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & curl.exe -L --progress-bar -o $out $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit code $LASTEXITCODE)." }
    } else {
        Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
    }

    if (-not (Test-Path $out)) { throw 'The runtime installer was not downloaded.' }

    # A CDN error page would also save as a file, so confirm this is a real executable
    $sig = [System.IO.File]::ReadAllBytes($out)[0..1]
    if ([char]$sig[0] -ne 'M' -or [char]$sig[1] -ne 'Z') {
        throw 'The downloaded file is not a valid Windows installer.'
    }

    Write-Host ''
    Write-Host '  [2/2] Installing the runtime, please wait...' -ForegroundColor Yellow
    Write-Host ''

    $proc = Start-Process -FilePath $out -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru

    # 3010 = success, reboot required later
    if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
        Write-Host '  .NET 8 Desktop Runtime installed successfully.' -ForegroundColor Green
    } else {
        throw "The runtime installer returned exit code $($proc.ExitCode)."
    }

    Write-Host ''
    Write-Host '  Returning to AxePower Setup...' -ForegroundColor Cyan
    Start-Sleep -Seconds 3
    exit 0
}
catch {
    Write-Host ''
    Write-Host "  Could not install the runtime: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Write-Host '  You can install it manually from:' -ForegroundColor Yellow
    Write-Host '    https://dotnet.microsoft.com/download/dotnet/8.0'
    Write-Host '  (choose "Desktop Runtime" for x64)'
    Write-Host ''
    Write-Host '  This window closes in 15 seconds...'
    Start-Sleep -Seconds 15
    exit 1
}
finally {
    if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
}
