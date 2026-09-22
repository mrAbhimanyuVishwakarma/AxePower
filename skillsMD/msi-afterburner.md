---
name: msi-afterburner
description: MSI Afterburner companion — locate/launch minimized, auto-install via winget or MSI's official zip, first-run monitoring/OSD preset for Afterburner and RTSS, and its role as telemetry tier 2 via shared memory.
---

# MSI Afterburner Companion

This feature launches or installs MSI Afterburner (with RivaTuner Statistics Server) for GPU tuning and the in-game OSD. While Afterburner runs, it also becomes a temperature source for AxePower's dashboard.

## Where it lives

| Piece | File |
|---|---|
| Launch / install / config | [MsiAfterburnerService.cs](../AxePower.Core/Services/MsiAfterburnerService.cs) |
| Sensor read (telemetry tier 2) | [AfterburnerSensorReader.cs](../AxePower.Core/Services/AfterburnerSensorReader.cs) |
| Sidebar command | `MainViewModel.OpenMsiAfterburner()` ([MainViewModel.cs](../AxePower.App/ViewModels/MainViewModel.cs)) |
| Game Mode buttons | `GameModeViewModel.LaunchAfterburnerAsync`, `ConfigureOsd` |

Sidebar: UTILITIES → **MSI Afterburner**. It is a command, not a page.

## `LaunchOrInstallAsync()` → `MsiAfterburnerLaunchResult`
1. **Found** (`GetAfterburnerPath()` checks the registry and Program Files):
   - Already running: focus its window and return `AlreadyRunning`.
   - Otherwise: apply the monitoring preset if needed, start `MSIAfterburner.exe -min`, and start `RTSS.exe` minimized if it isn't running. Returns `Launched`.
2. **Not found:**
   - `winget install --id Guru3D.Afterburner --silent --disable-interactivity`.
   - If that fails, a PowerShell script downloads `https://download.msi.com/uti_exe/vga/MSIAfterburnerSetup.zip` and runs the installer.
   - On success it launches Afterburner and returns `InstalledAndLaunched`, otherwise `Failed`.

## Monitoring preset (`EnsureMonitoringConfigApplied`)
- Writes `Profiles\MSIAfterburner.cfg` **only if it is missing or smaller than 100 bytes**. A real user config is never overwritten. The preset turns on hardware monitoring and marks GPU temperature, usage, clocks, VRAM, CPU temperature, framerate and so on as `ShowInOSD=1`.
- Writes the RTSS `Profiles\Global` file only if it doesn't exist.
- Both writes go under Program Files, so they need admin and fail silently (with a log warning) otherwise.

## Link with telemetry
When Afterburner is running, it publishes its sensor table in the `MAHMSharedMemory` block. `HardwareMonitorService` reads that block as its second tier. This is the easiest way to get real CPU temperatures on non-Lenovo machines where Memory Integrity blocks the MSR driver. See [cpu-gpu-telemetry.md](cpu-gpu-telemetry.md).

## Credit
MSI / Guru3D (Afterburner) and Alexey Nicolaychuk (RivaTuner). The credit link is `OpenMsiCredit`.
