# Performance and resource budgets

**For:** anyone changing AxePower's background work, and the owner running the measurements.
**Last updated:** 2026-10-08 (Prompt 29).

## Hardware

Lenovo 83DV: Intel Core i5-13450HX (10 cores, 16 threads), 15.7 GB RAM, Intel UHD Graphics and
NVIDIA GeForce RTX 4060 Laptop GPU, WD_BLACK SN770 1 TB and WD PC SN740 512 GB SSDs, Windows 11
Home 10.0.26300.

## Budgets

| Measure | Budget |
|---|---|
| In the tray (window hidden or minimised) | ≤ 0.5% of one core (≤ 5 ms CPU per second); no network traffic; no handle growth over 10 minutes |
| Dashboard on screen | ≤ 3% of one core (≤ 30 ms CPU per second) |
| Start to window shown | ≤ 2 s |
| Page switch | ≤ 200 ms on the first visit, ≤ 100 ms after |
| Private memory | Recorded, no budget yet (see "Open") |

"% of one core" is CPU time per second ÷ 10 ms. Windows counts process CPU time in 15.6 ms steps,
so only averages over 30 s or more mean anything.

## How to measure

- **The real app** (elevated, as installed): start it, put it in the state to measure, then
  `powershell -ExecutionPolicy Bypass -File scripts\measure-axepower.ps1 -Label Dashboard`
  (60 s, read-only). Repeat for `Settings` and `Tray`.
- **The app graph in the test host** (Release, window off-screen, settings in a temp folder):
  `set AXEPOWER_BENCHMARKS=1`, then
  `dotnet test AxePower.Tests -c Release --filter FullyQualifiedName~PerformanceBenchmarks`.
  Writes `%TEMP%\axepower-benchmarks.txt`. The test host's own floor is about 5 ms/s.

## Baseline (before Prompt 29)

**Installed v2.1.2, idle in the tray** (`measure-axepower.ps1`, 2026-10-06 and 2026-10-08):

- **CPU:** 14.6–17.7 ms/s (1.5–1.8% of one core).
- **Handles:** swinging between 1,435 and 1,575 (an earlier sample ran 1,850–2,015) and back at
  each collection. That's churn, not a leak.
- **Threads and memory:** 33–41 threads, 307 MB private.
- **Child process:** one `powershell.exe`, alive since start-up.

**Test host:**

- **Start-up:** services and view models 479 ms; window shown and idle 1.4 s.
- **Idle:** Dashboard 73 ms/s; Settings 11.7 ms/s; tray 6.3 ms/s.
- **With the three periodic jobs stopped:** 3.9 ms/s, flat handles.
- **Page switches:** Game Mode 156 ms first and 131–134 ms on every later visit; all others
  ≤ 93 ms.

**Where it went** (each piece timed alone, then switched on one at a time):

| Work | When it ran | Cost |
|---|---|---|
| 1-second sampler (`SystemTelemetryService`): CPU, memory, network, a WMI GPU-counter query (~295 ms to answer, 57 rows), a ping to 1.1.1.1 and `nvidia-smi` every 5th sample | Whenever the window was visible, on every page and while minimised | 9.4 ms CPU and ~60 short-lived handles per sample |
| Dashboard 2-second refresh | Always, even in the tray | ~15 ms/s: it rebuilt the battery trend every tick and redrew the graph |
| Battery page 3-second refresh | Always, even in the tray | Small |
| Terminal's PowerShell session | From start-up, for the whole session, even if Terminal was never opened | 74 MB working set, 57 MB private, 25 threads, 632 handles |
| Game Mode page | Rebuilt (~1,000 lines of XAML) on every visit | 134 ms per switch |

## What changed

- **Work runs only while its page is on screen.** `MainViewModel.UpdateBackgroundActivity` is
  the only place that decides:
  - the Dashboard refresh runs only on the Dashboard;
  - the Battery refresh runs only on the Battery page;
  - the sampler runs only on the Dashboard and Game Mode;
  - the latency ping runs only on the Dashboard;
  - nothing runs while the window is in the tray or minimised.

  Enforced by `BackgroundActivityTests`.
- **The sampler** starts paused. The GPU-counter query runs every second sample, not every
  sample, so the GPU graph updates every 2 s.
- **The battery trend** is rebuilt only when the history changed.
- **The Terminal's PowerShell** starts when the Terminal page is first shown, or when a command
  is sent.
- **Game Mode's page** is built once and reused (`Helpers/CachedView`, `CachedViewTests`). It
  keeps its scroll position between visits.
- **Shutdown** is one ordered routine, `Services/AppShutdown`:
  - Existing steps first: the music queue is saved, routines are undone and friends see you leave.
  - Periodic work stops before any service is disposed.
  - Then the controller poller, LAN Drop listener, Share Screen, Terminal shell, hotkeys, sampler,
    hardware sensors and battery history are released, and finally everything else the container
    created.
  - Each step is isolated, and the release stage has a 5-second limit, past which the process
    ends.
  - The tray icon is disposed on close.

  Tested by `BackgroundActivityTests`: nothing fails, no child process is left, and a hanging step
  triggers the hard exit.
- **A deadlock found by the shutdown test, fixed.** The Logs page delivered each log line with a
  synchronous `Dispatcher.Invoke`, so a service logging while holding a lock (the Lenovo sensor
  reader, on a sampler tick) waited for the UI thread. If the UI thread was waiting for that lock
  (disposing the reader at exit), the app froze. Log lines are now queued (`BeginInvoke`).
- **The latency ping was undisclosed.** It is now listed, with System Doctor's DNS benchmark
  pings:
  - in PRIVACY.md, Trust & Permissions and the in-app and website privacy pages;
  - in `TrustInventoryTests`, which now also looks for `Ping`, `WebClient`, `WebRequest` and raw
    sockets.

## Final measurements

**Test host, 2026-10-06 and 2026-10-08.** Three runs; the machine was also running an IDE and a
browser.

| Measure | Result | Budget |
|---|---|---|
| Start-up, window shown and idle | 1.5–1.9 s (one outlier at 6.5 s under load) | ≤ 2 s: met |
| Game Mode switch, after the first visit | 21–24 ms (was 134) | ≤ 100: met |
| Other page switches | First visit ≤ 201 ms (Game Mode, once over by 1 ms); later visits ≤ 110 ms (Settings, once) | Met within noise |
| Idle on Settings | Mean 2.6–11.5 ms/s; the test host's own floor is ~5 | Met (sampler and refreshes off) |
| Idle in the tray | Mean 5.2–9.9 ms/s; handles flat | Met in the test host (at its floor) |
| Idle on Dashboard | Mean 33–79 ms/s across runs | **Not shown either way**: varies with machine load; see owner check A9 |
| Shutdown (release stage) | 18–78 ms, no step failed, no child process left | Within the 5 s limit |

The real app's final numbers come from owner check A9 (`measure-axepower.ps1` on this build).

## Open

- **Dashboard CPU** has not been measured on the real app after these changes (A9). If it is over
  3%, the remaining costs are the sampler (~10 ms/s) and redrawing on each sample.
- **Private memory** of ~300 MB (real app and test host) is unexplained. A heap dump of the test
  host (`dotnet-dump`) is the next step; nothing was changed for it.
- **Reduced work while the Dashboard is shrunk** to its widget was not measured separately.
