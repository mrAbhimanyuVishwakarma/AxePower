# WinUtil Comprehensive Reference & AxePower Integration Blueprint

An exhaustive architectural and feature analysis of **Chris Titus Tech's Windows Utility (WinUtil)** (`https://github.com/ChrisTitusTech/winutil`) and related repositories, detailing what features, scripts, tweaks, and optimizations can be adapted into **AxePower** (.NET 8 WPF).

---

## 1. Executive Summary & WinUtil Overview

**Chris Titus Tech WinUtil** is the gold standard of open-source Windows optimization and maintenance utilities. It consolidates hundreds of manual PowerShell commands, registry tweaks, Group Policy settings, DISM repairs, and WinGet packages into a single interactive GUI.

### Core Pillars of WinUtil
1. **System Tweaks & Debloat**: Privacy/telemetry disablement, UI enhancements, disabling bloat services, background task management.
2. **Windows Update Policies**: Delaying feature updates, security-only updates, disabling forced reboots.
3. **System Fixes & Maintenance**: SFC/DISM repair, Network & DNS flush, Windows Update cache reset, WinGet catalog repair.
4. **WinGet Software Hub**: Curated, silent multi-software installer and batch upgrader.
5. **MicroWin / ISO Creator**: Windows image slipstreaming and debloating.
6. **Configuration Management**: JSON profile export/import for machine replication.

---

## 2. Deep Breakdown of WinUtil Capabilities vs AxePower

| WinUtil Feature Area | Underlying Mechanism | AxePower Relevance | Recommended AxePower Tier / Module |
| :--- | :--- | :--- | :--- |
| **Telemetry & Privacy** | Registry (`AllowTelemetry=0`), DiagTrack & dmwappushservice service state | ⭐⭐⭐⭐⭐ Critical | **AxePower Privacy & Eco Shield** |
| **Power & Gaming Profiles** | `powercfg -duplicatescheme`, GameDVR registry flags, Power Throttling flags | ⭐⭐⭐⭐⭐ Core | **AxePower Beast & Power Engine** |
| **Background Services Tweak** | `Set-Service -StartupType Manual/Disabled` for non-essential services | ⭐⭐⭐⭐⭐ Critical | **AxePower Dynamic Service Optimizer** |
| **Windows Update Control** | Group Policy Registry (`AUOptions`, `DeferFeatureUpdatesPeriodInDays`) | ⭐⭐⭐⭐ High | **AxePower Update & Reboot Shield** |
| **System Maintenance & Repairs** | `sfc /scannow`, `DISM /Online /Cleanup-Image`, `netsh`, `ipconfig` | ⭐⭐⭐⭐ High | **AxePower System Doctor / Maintenance** |
| **Gaming Runtime & Software Hub**| `winget install --id ... --silent --accept-source-agreements` | ⭐⭐⭐⭐ High | **AxePower Software & Runtimes Hub** |
| **Low-Latency DNS Switcher** | WMI / `Set-DnsClientServerAddress` (1.1.1.1, 8.8.8.8, 9.9.9.9, Quad9) | ⭐⭐⭐⭐ High | **AxePower Network & Ping Optimizer** |
| **Windows 11 UI Quality-of-Life**| Classic Context Menu CLSID, Disable Bing Search in Start Menu, Copilot/Recall toggles | ⭐⭐⭐⭐ High | **AxePower Win11 QoL & UI Tweak Studio** |
| **System Restore Integration** | `Checkpoint-Computer` / WMI `SystemRestore.CreateRestorePoint` | ⭐⭐⭐⭐⭐ Mandatory Safety | **AxePower Safe Rollback Engine** |
| **MicroWin ISO Debloater** | DISM image mounting, package removal | ⭐⭐ Low (Out of Scope for active app) | *Omit or separate CLI tool* |

---

## 3. Detailed Technical Analysis of WinUtil Features to Port into AxePower

### Feature 1: Dynamic Background Service Optimizer (Eco vs Beast Modes)
In WinUtil, background services are reconfigured between **Manual**, **Disabled**, and **Automatic**. 

In AxePower, instead of a static one-time change, we can make this **dynamic and linked to AxePower's Power Modes**:
* **Eco Mode (Max Battery)**:
  * Automatically sets heavy background indexers and sync services to `Manual` or pauses them:
    * `SysMain` (Superfetch - prevents heavy disk thrashing on battery)
    * `WSearch` (Windows Search Indexer)
    * `DiagTrack` (Connected User Experiences and Telemetry)
    * `MapsBroker` (Downloaded Maps Manager)
    * `XblAuthManager` / `XblGameSave` / `XboxNetApiSvc` (Xbox services if not gaming)
* **Beast Mode / Game Mode**:
  * Temporarily pauses background update downloads, telemetry collection, and search indexing to ensure 100% of CPU time slices and timer resolution belong to the running game.
* **Safe Native C# Implementation**:
  * Use `System.ServiceProcess.ServiceController` or Win32 `ChangeServiceConfig` with clean async wrappers rather than raw unverified PowerShell strings.

---

### Feature 2: Privacy, Telemetry & CPU Debloat Engine
WinUtil disables background telemetry beacons and data logging that silently consume CPU cycles and wake laptops from modern standby (S0 low power idle).

**Specific Settings from WinUtil:**
1. **Disable Diagnostic Telemetry**:
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection`
   * Value: `AllowTelemetry` = `0` (Security/Basic).
2. **Disable Bing Web Search in Start Menu**:
   * Reg Key: `HKCU:\SOFTWARE\Policies\Microsoft\Windows\Explorer`
   * Value: `DisableSearchBoxSuggestions` = `1`.
   * Reg Key: `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search`
   * Value: `BingSearchEnabled` = `0`.
   * *Benefit*: Speeds up Start Menu opening instantly; removes network polling every time the user searches for a local app.
3. **Disable Cortana & Copilot / Windows Recall Overheads**:
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot` -> `TurnOffWindowsCopilot` = `1`.
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsAI` -> `DisableAIDataAnalysis` = `1`.
4. **Disable Activity History & Feedback Prompts**:
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\System` -> `EnableActivityFeed` = `0`.
   * Reg Key: `HKCU:\SOFTWARE\Microsoft\Siuf\Rules` -> `NumberOfSIUFInPeriod` = `0`.

---

### Feature 3: Gamer Optimization & Latency Reductions
WinUtil contains targeted optimizations specifically designed for esports, high refresh rates, and stutter prevention:

1. **Disable GameDVR Background Recording & Captures**:
   * Windows GameDVR continuously encodes background video into RAM/SSD even when not asked, causing frame drops.
   * Reg Key: `HKCU:\System\GameConfigStore` -> `GameDVR_Enabled` = `0`.
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR` -> `AllowGameDVR` = `0`.
2. **Disable Mouse Pointer Precision (Hardware Acceleration)**:
   * Provides true 1:1 raw linear mouse input for gaming.
   * Reg Key: `HKCU:\Control Panel\Mouse` -> `MouseSpeed` = `0`, `MouseThreshold1` = `0`, `MouseThreshold2` = `0`.
3. **Ultimate Performance Power Scheme Unlock**:
   * GUID: `e9a42b02-d5df-448d-aa00-03f14749eb61`.
   * AxePower already supports Beast Mode; WinUtil's approach ensures the GUID is cloned cleanly without duplicate entries.
4. **FSE (Full Screen Exclusive) / Disable Windowed Gaming Optimizations Stutter**:
   * Option to configure per-game or system-wide latency parameters.

---

### Feature 4: Windows Update & Reboot Shield (Gamer/Work Protection)
One of WinUtil's most praised modules is giving users sane control over Windows Updates.

1. **Security Updates Only (Recommended Update Policy)**:
   * Delays major disruptive OS feature builds by up to 365 days while ensuring monthly security patches install smoothly.
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` -> `DeferFeatureUpdates` = `1`, `DeferFeatureUpdatesPeriodInDays` = `365`.
2. **No Forced Reboots When Logged On**:
   * Prevents Windows from forcefully restarting your laptop or PC while a game or download is in progress.
   * Reg Key: `HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU` -> `NoAutoRebootWithLoggedOnUsers` = `1`.
3. **One-Click Windows Update Diagnostic & Repair**:
   * Stops `wuauserv`, `bits`, `cryptsvc`.
   * Renames corrupted `C:\Windows\SoftwareDistribution` and `catroot2`.
   * Restarts services and resets update state.

---

### Feature 5: System Maintenance & Diagnostics Hub ("System Doctor")
WinUtil bundles essential Windows repair tools that normally require memorizing cmd commands:

1. **System File Checker (SFC)**: `sfc /scannow` running in a worker task with real-time UI percentage and output log parsing.
2. **DISM Component Store Repair**: `DISM /Online /Cleanup-Image /RestoreHealth`.
3. **WinGet Catalog Repair**: `winget source reset --force` (fixes broken package installs).
4. **Junk & Shader Cache Cleaner**:
   * Cleans `%TEMP%`, `C:\Windows\Temp`, `C:\Windows\Prefetch`.
   * Cleans DirectX Shader Cache (`%LOCALAPPDATA%\D3DSCache`), NVIDIA/AMD shader caches, and Delivery Optimization cache (`C:\Windows\SoftwareDistribution\DeliveryOptimization`).
5. **Network Stack & DNS Reset**:
   * `ipconfig /flushdns`
   * `netsh winsock reset`
   * `netsh int ip reset`

---

### Feature 6: Low-Latency DNS Optimizer
WinUtil includes a fast DNS switcher. In AxePower:
* Quick one-click switch between:
  * **Cloudflare (1.1.1.1 / 1.0.0.1)** - Lowest general latency & gaming ping.
  * **Cloudflare Malware Blocking (1.1.1.2 / 1.0.0.2)**.
  * **Quad9 (9.9.9.9 / 149.112.112.112)** - High security & privacy.
  * **Google DNS (8.8.8.8 / 8.8.4.4)** - Maximum reliability.
  * **Automatic / DHCP Default**.
* Includes a built-in **DNS Ping Benchmark** that measures latency (ms) to each DNS provider and highlights the fastest one for the user's current network connection.

---

### Feature 7: Windows 11 Quality-of-Life (QoL) Customizer
1. **Restore Classic Windows 10 Full Context Menu**:
   * Registry: `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` (Set default to empty string).
   * Restores instant right-click without the sluggish "Show more options" extra click.
2. **Taskbar Alignment & Search Box Customizer**:
   * Align Taskbar icons Left vs Center.
   * Hide useless Task View, Widgets, or Chat buttons.
3. **Disable Sticky Keys Prompt**:
   * Prevents the infamous 5x Shift key popup during gaming.
   * Reg Key: `HKCU:\Control Panel\Accessibility\StickyKeys` -> `Flags` = `506`.

---

### Feature 8: Curated WinGet Gaming & Essentials Installer
WinUtil uses a curated list of WinGet IDs. In AxePower, we can offer a dedicated **Gamer & Essentials Toolbox**:
* **Runtimes & Redistributables**: Microsoft Visual C++ All-in-One (`Microsoft.VCRedist.2015+.x64`), DirectX End-User Runtime (`Microsoft.DirectX`), .NET Runtimes.
* **Gaming Launchers**: Steam, Epic Games, GOG Galaxy, EA App, Ubisoft Connect.
* **Hardware & Monitoring Tools**: MSI Afterburner, Rivatuner Statistics Server (RTSS), CPU-Z, GPU-Z, HWMonitor, HWiNFO, CrystalDiskInfo.
* **Essential Utilities**: 7-Zip, Notepad++, PowerToys, OBS Studio, VLC, Discord, Scrcpy.
* **Batch Update All**: Runs `winget upgrade --all --include-unknown` with live progress bar and status report.

---

### Feature 9: Absolute Safety & Rollback Architecture (System Restore Points)
Following the AxePower strict safety rules:
1. **Mandatory Restore Point**: Before any batch tweak or debloat action, AxePower triggers a Windows System Restore point (`AxePower_PreOptimization_Date`).
2. **Granular Per-Tweak Toggle & Undo**:
   * Every tweak has an `Apply()` and `Revert()` method with exact initial state detection (`TweakState: Applied | Reverted | Unknown`).
   * Registry changes backup original values to a local JSON state cache (`%LocalAppData%\AxePower\tweak_state.json`) for 100% accurate rollbacks.
3. **No destructive actions**: AxePower never permanently deletes system apps; it uses per-user de-provisioning or disabling, allowing simple reinstallation from the Microsoft Store.

---

## 4. Proposed AxePower UI Architecture for WinUtil Features

We recommend organizing these features into **three new dedicated pages / sub-sections** in AxePower:

```
AxePower Navigation
├── 📊 Dashboard (Live Hardware Telemetry, Modes, Battery Health, Watts)
├── ⚡ Power & Performance (Eco / Smart / Beast, TDP Limits, Refresh Rate, Keep Awake)
├── 🛠️ System Optimizer (Inspired by WinUtil)
│    ├── 🛡️ Privacy & Telemetry Debloat
│    ├── 🎮 Gamer Latency & UI Tweaks (GameDVR, Mouse Accel, Classic Menu)
│    ├── ⚙️ Windows Update & Reboot Shield
│    └── 🌐 Low-Latency DNS Switcher & Benchmark
├── 🧰 System Doctor & Maintenance
│    ├── 🧹 Deep Cache & Shader Cleaner
│    ├── 🩺 SFC & DISM Health Repair
│    └── 🔄 Network & WinGet Reset
├── 📦 Software & Runtime Hub (WinGet Integration)
│    ├── 🎮 Gaming Runtimes & Launchers
│    └── 🚀 One-Click "Update All Apps"
└── ⚙️ Settings (Profiles, Hotkeys, Auto-Switch Rules, Backups)
```

---

## 5. Summary of WinUtil Components to Adopt

| Module Name in AxePower | Source Inspiration in WinUtil | Primary User Benefit |
| :--- | :--- | :--- |
| **AxePower Safe Debloat** | `Tweaks/Essential.ps1` | Removes background telemetry & bloat, freeing up CPU/RAM |
| **Gamer Tweak Suite** | `Tweaks/Advanced.ps1` | Stops GameDVR micro-stutters, enables raw mouse input |
| **Windows Update Shield** | `Updates/Updates.ps1` | Prevents untimely game interruptions and surprise reboots |
| **System Doctor** | `Fixes/Fixes.ps1` | One-click fix for corrupted system files, caches & network |
| **DNS Latency Optimizer** | `Tweaks/DNS.ps1` | Tests and switches to fastest gaming DNS in 1 click |
| **WinGet App Installer** | `Install/Install.ps1` | One-click silent install for VC++ runtimes, Steam, OBS |
| **Win11 QoL Enhancer** | `Tweaks/Win11.ps1` | Restores classic context menu, disables Bing in Start Menu |
| **Safe Rollback Engine** | `SystemRestore.ps1` | Automatic restore points & 1-click revert for every tweak |
