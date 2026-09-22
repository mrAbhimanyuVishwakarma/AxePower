---
name: cpu-gpu-telemetry
description: Live CPU/GPU/RAM/network/disk telemetry — the 1-second sampler, the three-tier driver-free temperature stack (Lenovo ACPI-WMI → MSI Afterburner shared memory → LibreHardwareMonitor), dGPU/iGPU switching, and the dashboard sparklines.
---

# CPU & GPU Telemetry

This powers the Dashboard's 2x2 performance grid (CPU, GPU, RAM, Internet), its sparklines and disk bars. Most of the work goes into temperatures: getting a real CPU die temperature on current Windows 11 is harder than it looks.

## Where it lives

| Piece | File |
|---|---|
| 1 s sampler, all metrics | [SystemTelemetryService.cs](../AxePower.Core/Services/SystemTelemetryService.cs) |
| Sensor backend selector | [HardwareMonitorService.cs](../AxePower.Core/Services/HardwareMonitorService.cs) |
| Tier 1: Lenovo firmware | [LenovoSensorReader.cs](../AxePower.Core/Services/LenovoSensorReader.cs) |
| Tier 2: Afterburner shared memory | [AfterburnerSensorReader.cs](../AxePower.Core/Services/AfterburnerSensorReader.cs) |
| Tier 3: LibreHardwareMonitor | NuGet `LibreHardwareMonitor`, used inside `HardwareMonitorService` |
| °C / °F formatting | [TemperatureHelper.cs](../AxePower.Core/Helpers/TemperatureHelper.cs) |
| Consumer | [DashboardViewModel.cs](../AxePower.App/ViewModels/DashboardViewModel.cs) (`OnTelemetryUpdated`) |
| Pause when hidden | [MainWindow.xaml.cs](../AxePower.App/MainWindow.xaml.cs): `IsVisible` → `Pause()` / `Resume()` |

## Data flow

```
Timer (200 ms delay, then every 1 s)
  └─ SystemTelemetryService.CollectAllMetrics()
       ├─ CPU %       GetSystemTimes deltas (idle vs kernel+user)
       ├─ CPU temp/clock  SampleCpuSensors()  → every 5th tick
       │     └─ HardwareMonitorService.Sample()
       │           1. LenovoSensorReader       (LENOVO_GAMEZONE_DATA, root\WMI, via MI/CIM)
       │           2. AfterburnerSensorReader  (MAHMSharedMemory)
       │           3. LibreHardwareMonitor     (MSR via kernel driver)
       │     └─ fallback: ACPI thermal zone perf counter (flagged NOT die temp)
       ├─ RAM         GlobalMemoryStatusEx
       ├─ Network     NetworkInterface.GetIPStatistics deltas + ping 1.1.1.1
       ├─ GPU load    Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine (engtype_3D, per LUID)
       ├─ GPU clk/temp  sensor snapshot, or nvidia-smi every 5th tick
       └─ Disks       DriveInfo, every 30th tick
  └─ event TelemetryUpdated(SystemTelemetrySnapshot)
       └─ DashboardViewModel pushes into CpuHistory/GpuHistory/MemoryHistory/NetworkHistory
```

## The temperature stack (why three tiers)
The usual way to read die temperature is a kernel driver that reads the thermal MSRs. LibreHardwareMonitor, HWiNFO and Afterburner all do this. That driver (WinRing0) is on Microsoft's **vulnerable driver blocklist**, so with Memory Integrity (HVCI) enabled, the default on new machines, it silently fails to load and every reading comes back empty.

1. **Lenovo ACPI-WMI** (`LENOVO_GAMEZONE_DATA`): no driver, unaffected by HVCI. Present on Legion, LOQ and IdeaPad Gaming laptops. It must go through the MI/CIM stack (`Microsoft.Management.Infrastructure`); `System.Management.InvokeMethod` returns a bare "Not found" against this provider. Values below 1 mean "not reported". The reader gives up for the session after 3 consecutive failures.
2. **MSI Afterburner shared memory** (`MAHMSharedMemory`, signature `'MAHM'`): free to read, with no elevation needed, but only while Afterburner is running. Rows are matched by name: "CPU temperature" / "CPU1 temperature", "GPU temperature", "GPU usage", "Core clock", "CPU clock", "CPU power".
3. **LibreHardwareMonitor**: needs elevation. CPU package temperature comes from the "Package" sensor on Intel or "Tctl/Tdie" on AMD, falling back to the maximum core temperature. The clock is the average of "CPU Core" clocks. If the library loads but reports no CPU temperature, `IsUnavailable` is set and polling stops.

When Tier 1 succeeds, `MergeGpuDetail` still asks LibreHardwareMonitor for the GPU clock and load, because the firmware only reports temperatures.

**Last resort:** `Win32_PerfFormattedData_Counters_ThermalZoneInformation` (Kelvin, readable without admin). This is a chassis/skin sensor, which measured about 28 °C under full load. It sets `IsCpuTempFromDie = false`, and the UI must not present it as the CPU temperature.

## GPU: which adapter is shown
- Engine counters report the iGPU and dGPU separately, keyed by LUID. The dashboard follows the busiest adapter.
- Whether the discrete GPU counts as active is latched with hysteresis in `EvaluateDedicatedGpuState`. It turns on at ≥ 10 % load and turns off only after 3 consecutive fresh samples (about 15 s) below 3 %, holding its state in between. This stops the readout flipping every tick.
- The dGPU temperature is always carried, even while the iGPU is the active adapter.
- `nvidia-smi` is spawned only when the sensor stack did not already supply GPU data, and it disables itself if it never returns a valid line.

## Display rules
- A missing reading renders as `—`, never as `0°C` or `0.00 GHz`, which would look like a real measurement.
- The CPU card reads `"{GHz} GHz  {load}%  {temp}"`; the GPU card uses the same shape.
- The unit follows `AppSettings.TemperatureUnit` (Celsius or Fahrenheit).
- The network meter is scaled against the negotiated link speed (100 Mbps if unknown). Its label is `WiFi`, `LAN`, or `NET` when several links are up.
- Disk colors: green below 70 % used, yellow from 70 to 85 %, red above 85 %.

## Requirements
- **Administrator rights** for Tiers 1 and 3. This is the main reason `RunAsAdministrator` defaults to on (see [settings-and-startup.md](settings-and-startup.md)). Without elevation, `HardwareMonitorService` sets `UnavailableReason = "Administrator rights are required…"`.
- Tier 2 needs Afterburner running (see [msi-afterburner.md](msi-afterburner.md)).

## Gotchas
- `LibreHardwareMonitor.Computer` is not thread-safe. All access goes through `_lock`, so keep it that way.
- Sampling is paused while the main window is hidden. Code that needs telemetry while the app sits in the tray must call `Resume()` itself.
- The CPU clock fallback (base MHz × `PercentProcessorPerformance`) is only an approximation on hybrid P/E-core CPUs.

## Tests
[GpuActiveStateTests.cs](../AxePower.Tests/GpuActiveStateTests.cs) covers the hysteresis state machine.
