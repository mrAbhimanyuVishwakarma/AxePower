---
name: battery
description: Battery page and 24-hour history — live status via GetSystemPowerStatus, capacity/cycle count/power draw from WMI (health % currently synthetic), the powercfg battery-report seeded history file, and Lenovo charging toggles (conservation, rapid charge).
---

# Battery

The Battery page shows live status, health and capacity details, a 24-hour charge trend (also drawn on the Dashboard), and Lenovo charging toggles.

## Where it lives

| Piece | File |
|---|---|
| Service | [BatteryService.cs](../AxePower.Core/Services/BatteryService.cs) |
| View / VM | [BatteryView.xaml](../AxePower.App/Views/BatteryView.xaml), [BatteryViewModel.cs](../AxePower.App/ViewModels/BatteryViewModel.cs) |
| Models | [PowerStatusInfo.cs](../AxePower.Core/Models/PowerStatusInfo.cs), [BatteryHistoryPoint.cs](../AxePower.Core/Models/BatteryHistoryPoint.cs) |
| Dashboard trend | `DashboardViewModel.BatteryTrendPoints` ([DashboardViewModel.cs](../AxePower.App/ViewModels/DashboardViewModel.cs)) |
| Started at boot | `batteryService.StartHistoryCollection()` in [App.xaml.cs](../AxePower.App/App.xaml.cs) |

## Live status (`GetCurrentStatus`)
- `GetSystemPowerStatus`:
  - AC line status gives `IsPluggedIn`.
  - The percentage gives `BatteryPercentage`.
  - `BatteryFlag` 128/255 means no battery (`HasBattery=false`).
  - Charging means AC plus flag 8.
  - The remaining time is used when it is plausible.
- Details come from WMI:
  - `root\CIMV2 Win32_Battery`: name, chemistry, design voltage, estimated run time.
  - `root\wmi BatteryFullChargedCapacity`: full-charge capacity.
  - `root\wmi BatteryCycleCount`: cycle count.
  - `root\wmi BatteryStatus`: voltage, charge and discharge rate (shown as current power usage in W), remaining capacity.
  - Battery temperature (`HighPrecisionTemperature` / `Temperature`), with a thermal-zone fallback.
- The Battery page shows "--" for any field the firmware does not report.

> **Health % is not real.** The code does not read design capacity (`BatteryStaticData.DesignedCapacity` would be the source). It sets `DesignCapacity = FullChargedCapacity × 1.02`, so health always comes out at about 98 % ("Excellent") whatever the battery's actual wear. Fix this in `BatteryService` (around the `BatteryFullChargedCapacity` query) by reading `root\wmi BatteryStaticData.DesignedCapacity`.

## 24-hour history
- **File:** `%LOCALAPPDATA%\AxePower\BatteryHistory\history.json`.
- **Polling:** one point every **60 s**. Points older than 24 h are pruned (at most about 1,440 points).
- **First run seeding** (`EnsureBaselineHistoryAsync`):
  1. Runs `powercfg /batteryreport /xml /output %TEMP%\AxePower_batteryreport_<pid>.xml` and parses the real 24 h of recent usage, the same data behind the Windows Settings battery graph.
  2. If that fails (no battery, empty report), falls back to `GenerateSeedHistory`, a **synthetic** curve so the chart is not empty.
- `GetHistoryAsync()` returns a copy. `ClearHistoryAsync()` wipes both memory and the file.

## Lenovo charging toggles
`SetConservationMode`, `SetRapidCharge`, `SetOvernightCharging`, `SetFnLock` and `SetTouchpad` write **Lenovo Vantage's own registry settings**:
- `HKCU\Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin` → `BatteryChargeMode` = `Storage` (conservation), `Quick` (rapid) or `Normal`.
- `HKLM\SOFTWARE\Lenovo\PWRMTRV\Data` (and the WOW6432Node copy) → `ConservationMode` 0/1.

The toggle states persist in `AppSettings` (`conservationModeEnabled`, `rapidChargeEnabled`, `overnightBatteryChargingEnabled`, `fnLockEnabled`, `touchpadEnabled`).

> These toggles take effect only if Lenovo's own service reads the registry values back. AxePower does not call the firmware (ACPI/WMI) method that actually changes the charge limit. On machines without Lenovo software, the toggle changes nothing. The Lenovo Legion Toolkit source in `LenovoLegionToolkit/` shows the direct WMI route if you want to implement it properly.

## Related
- The Dashboard battery pill (`MainViewModel.ShowBatteryPill`) is visible only on the Dashboard.
- Low-battery switching lives in [auto-switch.md](auto-switch.md).
