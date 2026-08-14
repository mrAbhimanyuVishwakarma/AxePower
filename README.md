# AxePower: Intelligent Power Management & Gaming Optimizer for Windows

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20(1809+)%20%7C%20Windows%2011-0078D4?logo=windows&logoColor=white)
![Framework](https://img.shields.io/badge/Framework-.NET%208%20WPF-512BD4?logo=dotnet&logoColor=white)
![Language](https://img.shields.io/badge/Language-C%23%2012-239120?logo=csharp&logoColor=white)
![Architecture](https://img.shields.io/badge/Architecture-MVVM%20%7C%20DDD-blue)
![License](https://img.shields.io/badge/License-MIT-green)
![Version](https://img.shields.io/badge/Version-1.3.0-blueviolet)

**Intelligently optimizes your system's power usage, display refresh rates, and background processes to deliver the perfect balance between performance, battery life, and cooling—automatically.**

[Official Website](https://axepower.vercel.app/) • [Download Latest Release](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases) • [Report Issue](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues) • [Developer Profile](https://github.com/mrAbhimanyuVishwakarma)

</div>

## 🚀 Key Features

- ⚡ **Intelligent Power Modes**: Seamlessly switch between **Eco**, **Smart**, **Beast**, and **Game** modes tailored for battery savings or extreme performance.
- ⚙️ **Smart Performance Defaults**: Out-of-the-box defaults set to **Smart Mode** for both AC and Battery power, with custom dropdown configuration in Settings.
- 🔄 **Remember Last Selected Mode**: Optional toggle to persist and automatically restore your last manually selected power mode across application restarts.
- 📱 **Android Control (scrcpy)**: Ultra-low latency screen mirroring and full keyboard/mouse control for Android devices via USB or Wi-Fi *(Credit to Genymobile)*.
- 🔄 **Smart Auto-Switch**: Automatically applies power plans and adjusts display refresh rates (e.g. 60Hz on battery vs 144Hz/165Hz/240Hz on AC power) whenever power states change.
- 🎮 **Game Booster & Scanner**: Automatically detects foreground gaming sessions, forces Beast Mode, and suspends heavy background processes to eliminate frame drops.
- 🔋 **Battery Diagnostics & Health**: Real-time 3x battery indicator, discharge tracking, estimated time remaining, charge cycle health, and trend insights.
- 🗑️ **Uninstaller Pro**: Deep application discovery, batch uninstallation, and verified leftover registry/filesystem cleaner with safety backups.
- 🔄 **Update PC Utility**: Elevated one-click driver and Windows update management via Windows Package Manager / USOClient.
- 🧹 **Win11Debloat Integration**: One-click elevated execution of Windows optimization and telemetry removal scripts *(Credit to Raphire)*.
- 📊 **MSI Afterburner Companion**: Fast-launch and focus integration for GPU tuning, fan curves, and in-game statistics *(Credit to MSI / Guru3D)*.
- 📖 **Interactive Tool Help & Guides**: Comprehensive in-app modal guides for all sidebar tools and `? Help` buttons with step-by-step instructions, shortcuts, and direct Readme links.
- 🚀 **GitHub Releases Auto-Updater**: Native update engine that checks for latest releases, streams installer downloads with live progress bars, and seamlessly updates the app.
- 🌐 **Modern Product Website**: Clean, dark, responsive single-page product website with dynamic zero-config GitHub release installer auto-discovery.
- 🗔 **Fluent Dark UI & System Tray**: Modern Windows 11 Fluent Dark interface with custom rounded tray context menu and minimize-to-tray capability.
- 🔒 **100% Local & Privacy-First**: Zero telemetry, zero analytics tracking, and zero remote data collection.

---

## 🖥️ User Interface & Navigation

AxePower provides a centralized desktop hub with a dedicated sidebar:

| Section | Icon | Purpose |
| :--- | :---: | :--- |
| **Dashboard** | 📊 | Real-time system monitoring, power mode switching (Eco, Smart, Beast, Game Mode), and live battery metrics. |
| **Auto Switch** | 🔄 | Configure automated rules for AC (Plugged In) and DC (Battery) states and dynamic refresh rate switching. |
| **Game Mode** | 🎮 | Manage background app suspension and automatic game process detection with custom cyber lion badge. |
| **Battery** | 🔋 | Comprehensive battery diagnostics, health percentages, and charging trends with custom icon cards. |
| **Settings** | ⚙️ | Application preferences, performance defaults, startup toggles, and notification settings. |
| **📱 Android Control** | 📱 | Direct Android device mirroring and control with built-in guide modal *(Credit to Genymobile)*. |
| **📊 MSI Afterburner** | ✈️ | Dedicated GPU overclocking and OSD launcher with built-in guide modal *(Credit to MSI)*. |
| **🗑️ Uninstaller Pro** | 🗑️ | Comprehensive app discovery, batch uninstallation, and leftover cleaner *(Credit to AxePower)*. |
| **🔄 Update PC** | 🔄 | Elevated hardware driver and Windows update launcher with built-in guide modal *(Credit to Microsoft)*. |
| **🧹 Debloat** | ⚡ | Win11Debloat PowerShell utility launcher with built-in guide modal *(Credit to Raphire)*. |
| **About** | ℹ️ | Device license info, version notes, and integrated GitHub Auto-Updater. |

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
        DATA[Configuration & Logging]
        UPD[Auto-Update Service]
        
        OS_SVC --> HW_INT
        OS_SVC --> UNINST
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

    UNINST --> AppDiscovery[ApplicationDiscoveryService]
    UNINST --> BatchUninstall[BatchUninstallService]
    UNINST --> LeftoverScanner[LeftoverScannerService]
    UNINST --> RegBackup[RegistryBackupService]

    HW_INT --> PowerCfg[PowerPlanService (powercfg)]
    HW_INT --> Battery[BatteryService (Win32 API)]
    HW_INT --> Display[DisplayService (ChangeDisplaySettingsEx)]
    HW_INT --> AppKiller[AppKillerService (Process/NT Suspend)]

    DATA --> Settings[SettingsService (Encrypted JSON)]
    DATA --> Log[LogService]
```

---

## 🖥️ User Interface & Navigation

AxePower provides a centralized desktop hub with a dedicated sidebar:

| Section | Icon | Purpose |
| :--- | :---: | :--- |
| **Dashboard** | 📊 | Real-time system monitoring, power mode switching (Eco, Smart, Beast), and battery metrics. |
| **Auto Switch** | 🔄 | Configure automated rules for AC (Plugged In) and DC (Battery) states and dynamic refresh rate switching. |
| **Game Mode** | 🎮 | Manage background app suspension and automatic game process detection. |
| **Battery** | 🔋 | Comprehensive battery diagnostics, health percentages, and charging trends. |
| **Settings** | ⚙️ | Application preferences, startup toggles, and notification settings. |
| **Logs** | 📝 | Real-time diagnostic logging and error tracking. |
| **Update PC** | 🔄 | Elevated hardware driver and Windows update launcher. |
| **Debloat** | 🧹 | Win11Debloat PowerShell utility launcher *(with attribution tooltip)*. |
| **🗑️ Uninstaller Pro** | 🗑️ | Comprehensive app discovery, batch uninstallation, and verified leftover cleaning. |
| **MSI Afterburner** | 📈 | Dedicated GPU overclocking and OSD launcher *(with attribution tooltip)*. |
| **About** | ℹ️ | Version information, developer profile, release notes, and integrated GitHub Auto-Updater. |

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
    GH-->>Svc: Latest Release JSON (v1.2.1, Assets: Setup.exe)
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
2. Open `Installer/AxePowerSetup.iss`.
3. Press `Ctrl + F9` to compile the installer into `Installer/Output/AxePower-Setup.exe`.

---

## 📜 Third-Party Credits & Acknowledgements

AxePower proudly integrates with and acknowledges tools created by the open-source and enthusiast community:

- **[scrcpy](https://github.com/Genymobile/scrcpy)**: High-performance Android screen mirroring and control engine by **Genymobile**.
- **[Win11Debloat](https://github.com/Raphire/Win11Debloat)**: Windows debloating and telemetry optimization scripts created by **Raphire**.
- **[MSI Afterburner & RTSS](https://www.msi.com/Landing/afterburner/graphics-cards)**: Hardware statistics and GPU monitoring by **MSI** and **Alexey Nicolaychuk (Guru3D / RivaTuner)**.
- **[Hardcodet WPF TaskbarNotification](https://github.com/hardcodet/wpf-notifyicon)**: System tray icon integration for WPF by **Philipp Sumi**.
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)**: Modern, high-performance MVVM library by the **.NET Foundation & Microsoft**.
- **Windows Package Manager (`winget`) & USOClient**: Package and driver update orchestration by **Microsoft Corporation**.

> *Notice: All trademarks, product names, and brand logos belong to their respective owners. AxePower functions as an independent, open-source orchestration companion. If you are an author or copyright holder of any included component and have questions, concerns, or requests for modification/removal, please open an issue and we will gladly accommodate.*

---

## 👤 Author & Support

- **Developed by**: [Abhimanyu Vishwakarma](https://github.com/mrAbhimanyuVishwakarma)
- **Official Download Repo**: [AxePower_Download](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download)
- **Issue Tracker & Support**: [GitHub Issues](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues)

*If you find AxePower helpful, please consider starring the repository on GitHub!*
