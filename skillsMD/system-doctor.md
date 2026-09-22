---
name: system-doctor
description: System Doctor — SFC, DISM, winget source reset and network stack reset with live output and cancel; cache cleaner (Windows temp, DirectX shader cache, crash dumps, Delivery Optimization); DNS latency benchmark and one-click DNS switch/DHCP reset.
---

# System Doctor

System Doctor has three tabs: repair tools, cache cleaning and DNS. The repair tools stream their output to the shared Terminal, whose **Stop** button cancels a running task.

## Where it lives

| Piece | File |
|---|---|
| View / VM | [SystemDoctorView.xaml](../AxePower.App/Views/SystemDoctorView.xaml), [SystemDoctorViewModel.cs](../AxePower.App/ViewModels/SystemDoctorViewModel.cs) |
| Repair tasks | [SystemDoctorService.cs](../AxePower.Core/Services/SystemDoctorService.cs) ([ISystemDoctorService.cs](../AxePower.Core/Services/ISystemDoctorService.cs)) |
| Cache cleaner | [CacheCleanerService.cs](../AxePower.Core/Services/CacheCleanerService.cs) ([ICacheCleanerService.cs](../AxePower.Core/Services/ICacheCleanerService.cs)) |
| DNS | [DnsOptimizerService.cs](../AxePower.Core/Services/DnsOptimizerService.cs) ([IDnsOptimizerService.cs](../AxePower.Core/Services/IDnsOptimizerService.cs)) |
| Models | [SystemDoctorModels.cs](../AxePower.Core/Models/SystemDoctorModels.cs) |

Sidebar: MAINTENANCE → **Doctor** (`Navigate("Doctor")`; `Maintenance` also lands here).

## Repair tools (`DiagnosticTaskType`)

| Task | VM command | What runs |
|---|---|---|
| `SfcScan` | `RunSfcScanAsync` | `sfc.exe /scannow` |
| `DismRepair` | `RunDismRepairAsync` | `dism.exe /Online /Cleanup-Image /RestoreHealth` |
| `WinGetReset` | `RunWinGetResetAsync` | `winget.exe source reset --force` |
| `NetworkReset` | `RunNetworkResetAsync` | `netsh winsock reset`, then `netsh int ip reset`, then `ipconfig /flushdns` |

- Output streams line by line through `IProgress<string>` into the Terminal page (see [terminal.md](terminal.md)).
- **Cancel:** the VM registers its `CancellationTokenSource` as `TerminalService.ActiveExternalCancel`, so either the Terminal's Stop button or `CancelDiagnostic` stops the task.
- All four need **admin**. The network reset needs a reboot to take full effect.

## Cache cleaner

| Category | Path |
|---|---|
| Windows temp | `%WINDIR%\Temp` |
| DirectX shader cache | `%LOCALAPPDATA%\D3DSCache` |
| Crash dumps | `%LOCALAPPDATA%\CrashDumps` |
| Delivery Optimization | `%WINDIR%\SoftwareDistribution\DeliveryOptimization` |

- `AnalyzeCachesAsync()` sizes each category. `CleanCachesAsync(selected)` deletes the files it can and skips locked ones, then returns a `CacheCleanResult`.
- Clearing the shader cache is safe, but games recompile shaders on their next launch, which causes a one-time stutter.

## DNS optimizer
- **Providers:**

| Provider | IPv4 | IPv6 |
|---|---|---|
| Cloudflare | `1.1.1.1` / `1.0.0.1` | `2606:4700:4700::1111` / `::1001` |
| Quad9 | `9.9.9.9` / `149.112.112.112` | `2620:fe::fe` / `::9` |
| Google | `8.8.8.8` / `8.8.4.4` | `2001:4860:4860::8888` / `::8844` |
| OpenDNS | `208.67.222.222` / `208.67.220.220` | `2620:119:35::35` / `2620:119:53::53` |

- **Benchmark** (`BenchmarkDnsProvidersAsync`) sends 3 ICMP pings (1.2 s timeout) to each primary IPv4 address and averages the successful ones. This measures network round-trip, not DNS resolution time.
- **Apply** (`ApplyDnsProviderAsync`) runs `netsh` against the active adapter (`GetActiveAdapterNameAsync`). **Reset** (`ResetToDhcpAsync`) returns the adapter to DHCP.
- Changing DNS needs admin.

## Tests
[SystemDoctorTests.cs](../AxePower.Tests/SystemDoctorTests.cs)
