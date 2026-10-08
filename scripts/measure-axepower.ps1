<#
.SYNOPSIS
  Measures a running AxePower for docs/engineering/PERFORMANCE.md (Prompt 29, owner check A9).

.DESCRIPTION
  Samples the AxePower process for -Seconds and prints its average CPU (ms per second and % of one
  core), the handle range, threads, private memory and any child processes. Read-only: it only
  reads process counters. Run it once per state: Dashboard open, Settings open, minimised to the
  tray.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\measure-axepower.ps1 -Label Dashboard
#>
param(
    [string]$Label = 'now',
    [int]$Seconds = 60
)

$p = Get-Process AxePower -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Host 'AxePower is not running.'; exit 1 }

$cpu0 = $p.TotalProcessorTime
$minH = [int]::MaxValue; $maxH = 0
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $p.Refresh()
    $minH = [Math]::Min($minH, $p.HandleCount); $maxH = [Math]::Max($maxH, $p.HandleCount)
}
$p.Refresh()
$msPerSecond = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds / $Seconds
$children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" | ForEach-Object Name

'{0,-10} CPU {1,6:0.0} ms/s ({2:0.00}% of one core); handles {3}-{4}; threads {5}; private {6:0} MB; working set {7:0} MB; children: {8}' -f `
    $Label, $msPerSecond, ($msPerSecond / 10), $minH, $maxH, $p.Threads.Count, ($p.PrivateMemorySize64 / 1MB), ($p.WorkingSet64 / 1MB),
    ($(if ($children) { $children -join ', ' } else { 'none' }))
