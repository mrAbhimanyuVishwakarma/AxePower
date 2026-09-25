# AxePower: Intelligent Power Management & Gaming Optimizer for Windows

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20(1809+)%20%7C%20Windows%2011-0078D4?logo=windows&logoColor=white)
![Framework](https://img.shields.io/badge/Framework-.NET%208%20WPF-512BD4?logo=dotnet&logoColor=white)
![Language](https://img.shields.io/badge/Language-C%23%2012-239120?logo=csharp&logoColor=white)
![Architecture](https://img.shields.io/badge/Architecture-MVVM%20%7C%20DDD-blue)
![License](https://img.shields.io/badge/License-MIT-green)
![Version](https://img.shields.io/badge/Version-2.0.7-blueviolet)

**Intelligently optimizes your system's power usage, display refresh rates, and background processes to deliver the perfect balance between performance, battery life, and cooling—automatically.**

[Official Website](https://axepower.vercel.app/) • [Download Latest Release](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases) • [Help Center](https://axepower.vercel.app/help.html) • [Report Issue](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues) • [Developer Profile](https://github.com/mrAbhimanyuVishwakarma)

</div>

## 🚀 Key Features

- 🎵 **Integrated Music Player & Media Hub**: Built-in high-fidelity audio engine supporting local libraries and YouTube Music streaming. Features an aesthetic Header Player matching the dashboard design and automatically follows default Windows audio output device switches.
- 🖥️ **Share Screen & GameStream**: Low-latency desktop and game display sharing directly to friends in their web browser without requiring them to install third-party clients.
- 🗑️ **Next-Gen Uninstaller Pro**: Deep leftover scanner with confidence scoring, registry backup manifests, broken uninstaller recovery, and PowerShell execution safety nets.
- 🖥️ **Built-In Terminal (CMD & PowerShell)**: A persistent interactive shell inside the app, and the single place every command AxePower runs reports to — Software Hub installs and System Doctor diagnostics stream their live output here, with a **Stop** button that cancels the running task.
- 🛡️ **Administrator Mode Without the UAC Nagging**: A **Run as administrator** setting (on by default) plus a title-bar **Relaunch with Admin** button. The app ships an `asInvoker` manifest and registers its logon task at `HighestAvailable`, so it starts elevated at boot with **no UAC prompt** — and the toggle can still be switched off.
- 🌡️ **Real CPU & GPU Temperatures (Driver-Free)**: A three-tier sensor stack — Lenovo's ACPI-WMI firmware interface, MSI Afterburner's shared memory, then LibreHardwareMonitor — so readings keep working on machines with **Memory Integrity / the vulnerable-driver blocklist enabled**, where MSR-only tools silently return nothing.
- 💾 **One-Click System Restore Point**: Create a Windows restore point straight from the Software Hub before you change anything, with the 24-hour creation throttle lifted so the checkpoint is actually written.
- 🪟 **Glass Theme (Mica & Acrylic)**: True Windows 11 DWM backdrops with an Acrylic blur-behind fallback for Windows 10, alongside System / Dark / Light.
- ⚡ **Intelligent Power Modes**: Seamlessly switch between **Eco**, **Smart**, **Beast**, and **Game** modes tailored for battery savings or extreme performance.
- 🎮 **Game Mode Overhaul & Multi-Platform Library Scanner**: Comprehensive automatic scanner for **Steam**, **Epic Games**, **Xbox / PC Game Pass**, **Ubisoft Connect**, **EA App**, and **GOG Galaxy**. Dynamic high-resolution poster art (**SteamGridDB**) with aspect-ratio adaptive cards and responsive hover zoom.
- 🕹️ **Controller Navigation (Xbox & PlayStation DualSense)**: Full gamepad-to-mouse emulation supporting Xbox (360 / One / Series X|S) and PlayStation (DualShock 4 / DualSense) controllers with in-app visual shortcut guides.
- ⚡ **GPU Control Companion Integration**: Seamless 1-click companion launcher and auto-detection for **Lenovo Legion Toolkit**, **Lenovo Vantage**, **ASUS Armoury Crate**, and **MSI Center**.
- 📊 **Live Hardware & Task Manager Performance Monitor**: Real-time 2x2 performance grid tracking **CPU** clock, **GPU** 3D engine utilization & temperature, **RAM** allocation, and **Internet** transfer speeds with precise zero-baseline sparkline graphs.
- 🔋 **24-Hour Battery History**: Dedicated full-width battery diagnostics tracking 24-hour battery percentage trends with time-axis markers (`24h ago`, `12h ago`, `Now`).
- ⚙️ **Smart Performance Defaults**: Out-of-the-box defaults set to **Smart Mode** for AC power and **Eco Mode** on battery, with custom dropdown configuration in Settings.
- 📌 **Steam-Style Taskbar Context Menu**: Dark Steam slate aesthetic with full-width dividers, direct 1-click power mode switching (Eco, Smart, Beast, Game Mode), and quick launcher shortcuts.
- 📜 **Steam Update Banner & Scrollable Patch Notes**: Steam Cyan/Blue bottom banner with complete scrollable changelog feed, reaction bar (`👍 Rate Up`, `👎`, `💬 Discuss`, `🔗 Share`), and smooth scroll arrows.
- 🚀 **Silent Background Auto-Updater**: Native update engine that checks for latest releases, streams installer downloads in the background with zero disruption, and provides 1-click "Restart & Install".
- 🛍️ **Software Hub & Ninite Multi-Installer**:
  - **Ninite Integration**: Bundled local multi-installer + online custom package builder for zero-click multi-app setups.
  - **Expanded Suite**: Audacity, OBS Studio, DaVinci Resolve, GIMP, Canva, Blender, HandBrake, PowerToys, ShareX, Peace Equalizer, Dolby Access, SteelSeries GG Sonar, and hardware diagnostic utilities.
- 📱 **Phone Control (All-in-One Mobile Suite)**:
  - 🖥️ **SpaceDesk Second Monitor**: Turn any Android tablet, iPad, or browser into an extended PC display with 1-click driver installation and QR code downloads *(Credit to DATRONICSOFT)*.
  - 🔗 **Microsoft Phone Link**: Instant launch integration and companion QR pairing modals for Link to Windows on Android and iOS.
  - 📂 **LAN Drop (Wi-Fi Quick Share)**: Instant offline file drop and clipboard note sync between PC and phone via dynamic QR code without requiring apps on the phone *(Credit to yadavnikhil03)*.
  - 📱 **Android Screen Mirroring (scrcpy)**: Ultra-low latency device mirroring, stay-awake / screen-off power toggles, resolution & FPS presets, and 1-click APK sideloading *(Credit to Genymobile)*.
- 📖 **Dedicated Help & Documentation Center**: Direct in-app `? Help` title bar button and "Learn More" links connecting to the official [AxePower Help Center](https://axepower.vercel.app/help.html).
- 🔄 **Smart Auto-Switch**: Automatically applies power plans and adjusts display refresh rates (e.g. 60Hz on battery vs 144Hz/165Hz/240Hz on AC power) whenever power states change.
- 🔄 **Update PC Utility & BIOS Checks**: Elevated one-click driver and Windows update management via Windows Package Manager (`winget`) and BIOS/firmware version checks.
- 🧹 **Win11Debloat Integration**: One-click elevated execution of Windows optimization and telemetry removal scripts *(Credit to Raphire)*.
- 📊 **MSI Afterburner Companion**: Fast-launch and focus integration for GPU tuning, fan curves, and in-game statistics *(Credit to MSI / Guru3D)*.
- 🔒 **100% Local & Privacy-First**: Zero telemetry, zero analytics tracking, and zero remote data collection.

---

## 🖥️ User Interface & Navigation

AxePower provides a centralized desktop hub with a dedicated sidebar:

| Section | Icon | Purpose |
| :--- | :---: | :--- |
| **CONTROL** | | |
| **Dashboard** | 📊 | Real-time system monitoring, power mode switching (Eco, Smart, Beast, Game Mode), live 2x2 performance grid (CPU, GPU, RAM, Internet), and 24h battery history. |
| **Auto Switch** | 🔄 | Configure automated rules for AC (Plugged In) and DC (Battery) states and dynamic refresh rate switching. |
| **Game Mode** | 🎮 | Multi-platform game library scanner, SteamGridDB vertical posters, GPU control companion, background app suspension, and Xbox / PS5 Controller Navigation. |
| **Battery** | 🔋 | Comprehensive battery diagnostics, health percentages, and charging trends with custom icon cards. |
| **Music** | 🎵 | Integrated music playback, local library scanning, YouTube Music streaming, and a sleek header player. |
| **CONNECTION** | | |
| **📱 Phone Control** | 📱 | All-in-one mobile suite: SpaceDesk wireless display, LAN Drop Wi-Fi share, and Android screen mirroring *(scrcpy)*. |
| **🖥️ Share Screen** | 🖥️ | Low-latency display and gameplay streaming to any browser with audio. |
| **MAINTENANCE** | | |
| **⚡ Optimizer** | ⚡ | Safe Windows debloat, telemetry removal, and system performance optimizations. |
| **🩺 Doctor** | 🩺 | Windows system health repair (SFC/DISM), shader cache cleaner, and DNS benchmark. |
| **🖥️ Terminal** | 🖥️ | Built-in CMD / PowerShell session and the shared output console for every command AxePower runs. |
| **UTILITIES** | | |
| **✈️ MSI Afterburner** | ✈️ | Dedicated GPU overclocking and OSD launcher with built-in guide modal *(Credit to MSI)*. |
| **🗑️ Uninstaller Pro** | 🗑️ | Comprehensive app discovery, batch uninstallation, and leftover cleaner. |
| **🔄 Update PC** | 🔄 | Elevated one-click driver and package updates. |
| **🧹 Debloat** | 🧹 | Win11Debloat by Raphire integration. |
| **FOOTER (PINNED)** | | |
| **⚙️ Settings** | ⚙️ | Application preferences, performance defaults, theme (System / Dark / Light / Glass), **Run as administrator**, startup toggles, and notification settings. |
| **ℹ️ About** | ℹ️ | License details, system compatibility, update checker, and official website links. |

---

## 🏛️ High-Level Architecture

AxePower is built on a Domain-Driven Design (DDD) layered architecture using `CommunityToolkit.Mvvm`:

```mermaid
flowchart TD
    subgraph Presentation Layer [AxePower.App]
        Views[WPF Views & Fluent XAML]
        VMs[CommunityToolkit MVVM ViewModels]
        Tray[Hardcodet System Tray & ContextMenu]
        DI[Microsoft.Extensions.DependencyInjection]
        
        Views <--> VMs
        VMs --> DI
        Tray --> VMs
    end

    subgraph Core Business Layer [AxePower.Core]
        OS_SVC[Orchestration Services]
        HW_INT[Hardware & Native Integrations]
        UNINST[Uninstaller Pro Engine]
        MOBILE[Phone Control & LAN Drop Engine]
        WINGET[WinGet Hub & Software Catalog]
        DATA[Configuration & Logging]
        UPD[Auto-Update Service]
        
        OS_SVC --> HW_INT
        OS_SVC --> UNINST
        OS_SVC --> MOBILE
        OS_SVC --> WINGET
        OS_SVC --> DATA
        UPD --> GitHubAPI[(GitHub Releases API)]
    end

    VMs --> Core Business Layer

    OS_SVC --> AutoSwitch[AutoSwitchService]
    OS_SVC --> AppMonitor[AppMonitorService]
    OS_SVC --> GameScanner[GameScannerService]
    OS_SVC --> Debloat[DebloatService]
    OS_SVC --> DriverUpdate[DriverUpdateService]
    OS_SVC --> Msi[MsiAfterburnerService]

    MOBILE --> LanDrop[LanDropService (TcpListener & Embedded Web Portal)]
    MOBILE --> AndroidCtrl[AndroidControlService (scrcpy & ADB Bridge)]

    WINGET --> WinGetHub[WinGetHubService (3-Stage Resilient Installer)]

    UNINST --> AppDiscovery[ApplicationDiscoveryService]
    UNINST --> BatchUninstall[BatchUninstallService]
    UNINST --> LeftoverScanner[LeftoverScannerService]
    UNINST --> RegBackup[RegistryBackupService]

    HW_INT --> PowerCfg[PowerPlanService (powercfg)]
    HW_INT --> Battery[BatteryService (Win32 API)]
    HW_INT --> Display[DisplayService (ChangeDisplaySettingsEx)]
    HW_INT --> AppKiller[AppKillerService (Process/NT Suspend)]
    HW_INT --> HwMon[HardwareMonitorService (3-tier sensor fallback)]

    HwMon --> LenovoWmi[LenovoSensorReader (ACPI-WMI, driver-free)]
    HwMon --> Afterburner[AfterburnerSensorReader (MAHM shared memory)]
    HwMon --> LHM[LibreHardwareMonitor (MSR via kernel driver)]

    OS_SVC --> Term[TerminalService (persistent shell & shared output bus)]
    OS_SVC --> Elev[ElevationService (asInvoker + runas relaunch)]
    OS_SVC --> Restore[RestorePointService (Checkpoint-Computer)]

    DATA --> Settings[SettingsService (Encrypted JSON)]
    DATA --> Log[LogService]
```

---

## 🛠️ Auto-Update Flow

```mermaid
sequenceDiagram
    participant User
    participant AboutView as AboutView / MainView
    participant Vm as AboutViewModel
    participant Svc as UpdateService
    participant GH as GitHub Releases API
    participant OS as Windows Shell

    User->>AboutView: Click "Check for updates" (or silent on startup)
    AboutView->>Vm: CheckForUpdatesCommand
    Vm->>Svc: CheckForUpdatesAsync()
    Svc->>GH: GET /repos/mrAbhimanyuVishwakarma/AxePower_Download/releases/latest
    GH-->>Svc: Latest Release JSON (v2.0.0, Assets: Setup.exe)
    Svc-->>Vm: UpdateCheckResult (HasUpdate = true)
    Vm->>AboutView: Display "Update Available" Modal Dialog
    
    User->>AboutView: Click "Update Now"
    AboutView->>Vm: DownloadAndApplyUpdateCommand
    Vm->>Svc: DownloadUpdateAsync(DownloadUrl, progress)
    loop Stream Download Chunks
        Svc->>Vm: Report progress (0% -> 100%)
        Vm->>AboutView: Update ProgressBar & Percentage
    end
    Svc-->>Vm: Downloaded to %TEMP%\AxePower_Update\Setup.exe
    Vm->>Svc: ApplyUpdate(installerPath)
    Svc->>OS: Process.Start("Setup.exe")
    Vm->>OS: Application.Current.Shutdown()
```

---

## 💻 Development & Build Guide

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or Visual Studio 2022 v17.8+)
- Windows 10 (Build 1809+) or Windows 11 (64-bit)

### Running Locally
```bash
# Clone the repository
git clone https://github.com/mrAbhimanyuVishwakarma/AxePower.git
cd AxePower

# Restore and build the solution
dotnet build

# Run the WPF application
dotnet run --project AxePower.App/AxePower.App.csproj
```

### Running Unit Tests
```bash
dotnet test
```

### Publishing a Release Build
```bash
dotnet publish AxePower.App/AxePower.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false
```

### Creating the Inno Setup Installer
1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php).
2. Publish first (the script packages `AxePower.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`).
3. Open `Installer/AxePower.iss` — the full script, which also bootstraps the .NET 8 Desktop Runtime and removes older installs.
4. Press `Ctrl + F9` to compile the installer into `Installer/Output/AxePower-v<version>-Setup.exe`.

---

## 📜 Third-Party Credits & Acknowledgements

AxePower proudly integrates with and acknowledges tools created by the open-source and enthusiast community:

- **[spacedesk](https://www.spacedesk.net/)**: Multi-monitor virtual display network extension engine by **datronicsoft Technology GmbH**.
- **[scrcpy](https://github.com/Genymobile/scrcpy)**: High-performance Android screen mirroring and control engine by **Genymobile**.
- **[LAN-DropSpot](https://github.com/yadavnikhil03/LAN-DropSpot.git)**: Wi-Fi local file drop & sharing inspiration by **Nikhil Yadav (`yadavnikhil03`)**.
- **[Win11Debloat](https://github.com/Raphire/Win11Debloat)**: Windows debloating and telemetry optimization scripts created by **Raphire**.
- **[MSI Afterburner & RTSS](https://www.msi.com/Landing/afterburner/graphics-cards)**: Hardware statistics and GPU monitoring by **MSI** and **Alexey Nicolaychuk (Guru3D / RivaTuner)**.
- **[DaVinci Resolve](https://www.blackmagicdesign.com/products/davinciresolve)**: Professional video editing and color grading by **Blackmagic Design**.
- **[Hardcodet WPF TaskbarNotification](https://github.com/hardcodet/wpf-notifyicon)**: System tray icon integration for WPF by **Philipp Sumi**.
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)**: Modern, high-performance MVVM library by the **.NET Foundation & Microsoft**.
- **Windows Package Manager (`winget`) & USOClient**: Package and driver update orchestration by **Microsoft Corporation**.
- **[Bulk Crap Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)**: Uninstaller Pro adapts parts of its uninstaller detection, process-tree waiting and leftover-evidence model, by **Marcin Szeniak** (Apache-2.0).

Full license texts and notices for bundled and adapted components are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

> *Notice: All trademarks, product names, and brand logos belong to their respective owners. AxePower functions as an independent, open-source orchestration companion. If you are an author or copyright holder of any included component and have questions, concerns, or requests for modification/removal, please open an issue and we will gladly accommodate.*

---

## 👤 Author & Support

- **Developed by**: [Abhimanyu Vishwakarma](https://github.com/mrAbhimanyuVishwakarma)
- **Official Download Repo**: [AxePower_Download](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download)
- **Issue Tracker & Support**: [GitHub Issues](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues)

*If you find AxePower helpful, please consider starring the repository on GitHub!*
