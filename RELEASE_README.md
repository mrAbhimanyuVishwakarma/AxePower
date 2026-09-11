# AxePower ⚡
### Intelligent Power Management & Gaming Optimizer for Windows

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/Framework-WPF%20%7C%20.NET%208-512BD4)
![License](https://img.shields.io/badge/License-MIT-green)
![Version](https://img.shields.io/badge/Version-1.7.0-blueviolet)

[Official Website](https://axepower.vercel.app/) • [Discord Community](https://discord.gg/WfD4wZFJ8d) • [Releases](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases) • [Help Center](https://axepower.vercel.app/help.html) • [Issues & Support](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues)

</div>

Welcome to AxePower! This is a simple, set-and-forget tool designed to automatically optimize your laptop's battery life, thermal envelope, and performance.
<img width="960" height="629" alt="axepower window pc dashboard" src="https://github.com/user-attachments/assets/1d91c230-afb2-4ac6-a685-bbf8a53f4c3b" />

---

## ⚡ What does it do?

AxePower manages your laptop's power settings and refresh rates based on whether your charger is plugged in or unplugged. 

Have you ever noticed your laptop running slowly when unplugged, or your battery draining too fast? AxePower solves this by offering dedicated modes:

- 🔋 **Eco Mode:** Saves battery so your laptop runs longer when you are away from the charger.
- ⚖️ **Smart Mode:** The perfect middle ground for everyday tasks like browsing and watching videos.
- 🚀 **Beast Mode:** Gives you maximum power and sustained clocks for gaming or heavy tasks when plugged in.
- 🎮 **Game Mode:** Automatically detects running games across any drive, suspends background bloat, locks maximum display refresh rate, and offers Controller Navigation (Gopher360).
- 📱 **Phone Control:** All-in-one mobile suite with offline **LAN Drop** Wi-Fi file sharing and **scrcpy** Android screen mirroring.
- 🛍️ **Software Hub & Updaters:** 1-click installer and launcher for gaming stores, hardware monitoring tools, Ninite multi-installer, drivers, and runtimes.

**The best part?** You can turn on **Auto Switch**, and AxePower will automatically switch between these modes for you. Out-of-the-box, it defaults to **Smart Mode** when plugged in and **Eco Mode** on battery for optimal balance, or you can choose your own defaults!

---

## 🌟 What's New in v1.7.0

- 🎮 **Game Mode Redesign & Game Library Scanner**:
  - **Comprehensive Multi-Platform Game Scanner**: Automatically detects and catalogs installed games across **Steam**, **Epic Games Launcher**, **Xbox / Microsoft Store (Game Pass)**, **Ubisoft Connect**, **EA App**, and **GOG Galaxy**.
  - **High-Resolution Poster & Box Art (SteamGridDB)**: Dynamic poster artwork integration with local caching and graceful fallback graphics for all discovered titles.
  - **Aspect-Ratio Game Cards**: Responsive grid presentation with fluid hover scaling, one-click launcher, and quick folder browsing.
- 🕹️ **Controller Navigation (Xbox & PlayStation)**:
  - Full gamepad navigation support for **Xbox (360 / One / Series X|S)** and **PlayStation (DualShock 4 / DualSense)** controllers.
  - In-app interactive visual controller guide with button mappings for mouse movement, clicking, scrolling, and system controls.
- ⚡ **GPU Control Companion Integration**:
  - Direct integration and quick launch support for OEM hardware suites: **Lenovo Legion Toolkit**, **Lenovo Vantage**, **ASUS Armoury Crate**, and **MSI Center**.
- 🛠️ **Performance & Automation Improvements**:
  - Enhanced background service suspension during active gaming sessions and dynamic mode automation tests.

---

## 🌟 What's New in v1.6.0

- 📊 **Task Manager Live 2x2 Performance Grid**:
  - Replaced legacy sidebar widgets with a high-performance 2x2 live telemetry matrix:
    - **CPU**: Real-time processor load % and live clock speed (GHz).
    - **GPU**: Direct Windows WDDM 3D engine utilization % and temperature monitor for discrete graphics (e.g. RTX 4060).
    - **RAM**: Live physical memory consumption (Used/Total GB and %).
    - **Internet**: Active Wi-Fi and Ethernet bandwidth meter tracking real-time Send & Receive speeds.
  - Zero-baseline StreamGeometry sparklines with smooth glowing area fills and stroke curves.
- 🔋 **24-Hour Battery History**:
  - Dedicated full-width battery diagnostics card with a 24-hour continuous rolling trend curve.
  - Clear time-axis markers (`24h ago`, `12h ago`, `Now`) and percentage badge.
- 📌 **Steam-Style Taskbar Context Menu**:
  - Completely redesigned taskbar right-click menu with dark Steam slate aesthetic `#171A21` and full-width edge-to-edge dividers.
  - Organized into 4 clean sections:
    - **Power Modes (1-Click Switch)**: Eco Mode, Smart Mode, Beast Mode, Game Mode.
    - **Navigation & Monitoring**: Open AxePower, Battery Hub, Phone Control, Second Monitor.
    - **Maintenance & Updaters**: System Doctor, System Utilities, Update PC.
    - **System**: Settings, Exit AxePower.
- 📜 **Steam Update Banner & Scrollable Patch Notes Modal Dialog**:
  - Bright Cyan/Blue bottom banner popping open on update checks or when an update is ready to install.
  - Full-featured Steam Patch Notes dialog featuring a multi-post scrollable feed, Up (`▲`) and Down (`▼`) scroll buttons, and live community reaction buttons (`👍 Rate Up`, `👎`, `💬 Discuss`, `🔗 Share`).
- 🚀 **Silent Background Auto-Updater**:
  - Background auto-checking and silent background download of update installers into `%TEMP%\AxePower_Update\`.
  - 1-Click **Restart & Install** banner button that launches the new installer and seamlessly restarts AxePower.
  - Configurable in *Settings &rarr; Updates* with interactive toggles for automatic checks and background downloads.
- 🖥️ **SpaceDesk Wireless Second Display & Microsoft Phone Link**:
  - 1-Click SpaceDesk virtual display driver install (`Datronicsoft.SpacedeskDriver.Server`) and console launcher.
  - Interactive QR code modals for Google Play (Android), App Store (iOS), and HTML5 Web Viewer.
  - Instant Microsoft Phone Link launcher and Link to Windows pairing modals.
- 🎧 **Audio Suite & Creative Software Hub**:
  - 1-Click catalog integration for Dolby Access (Dolby Atmos 3D spatial surround sound), SteelSeries GG Sonar mixer, Peace Equalizer APO, DaVinci Resolve, OBS Studio, and Microsoft PowerToys.
  - PowerShell batch updates (`winget upgrade --all`) and individual package upgrades.
- 🪟 **Window Controls & Layout Refinements**:
  - Added dedicated `? Help` button to the title bar directly to the left of Minimize.
  - Set `✕` close button to minimize directly to the system tray (`Hide()`) with background taskbar monitoring intact.
  - Streamlined Settings and About views with left-aligned update checkers.

---

## 🌟 What's New in v1.5.0

- 🛍️ **Ninite Multi-Installer Integration**:
  - Added Ninite to Update PC & Software Hub with dual-action buttons:
    - **Install**: Launches the bundled local multi-installer to setup apps with zero clicks.
    - **Custom**: Opens the official Ninite builder at [ninite.com](https://ninite.com/) to create custom packages.
- 🧭 **Tool Guides & Help Modal Scrolling**:
  - Implemented horizontal scrollable tabs in the in-app Tool Help modal, ensuring all tools (including Controller Navigation) are smoothly navigable.
- 📖 **Dedicated Help & Documentation Center Webpage**:
  - Launched official [AxePower Help Center](https://axepower.vercel.app/help.html) with detailed controller gamepad mappings, step-by-step setup guides, and live instant search.
  - Replaced "Readme" button with "Learn More" linking directly to corresponding tool documentation.
- 🔋 **Live Real-Time Battery Telemetry**:
  - Dynamic battery metrics and real-time charging status.
- 🛠️ **Firmware & BIOS Update Checks**:
  - Integrated hardware BIOS and firmware inspection in Update PC.
- 🎨 **Dynamic Theme & Resource Architecture**:
  - Fully dynamic theme resources and instantaneous light/dark theme switching across all views.

---

## 🌟 What's New in v1.4.0

- 🧭 **Sidebar & Navigation Overhaul**:
  - **Pinned Footer**: Settings and About sections are cleanly anchored to the bottom footer of the sidebar.
  - **Reorganized Categories**: Grouped navigation into **CONTROL**, **CONNECTION** (Phone Control), collapsible **MAINTENANCE** (Optimizer, Doctor), and collapsible **UTILITIES** (MSI Afterburner, Uninstaller Pro, Update PC, Debloat).
- 🎮 **Controller Navigation (Gopher360) in Game Mode**:
  - Dedicated gamepad / Xbox controller navigation controls in the **Game Mode** page.
  - Turn gamepad into a Windows mouse & keyboard with quick reference mapping and instant toggle shortcuts (`Back + Start`).
- ⚡ **Intelligent Default Power Rules**:
  - **Plugged In**: Defaults to **Smart Mode**.
  - **On Battery / Unplugged**: Defaults to **Eco Mode**.
  - **Low Battery (< 30%)**: Defaults to **Eco Mode**.
  - Improved manual power mode selection flow.
- 📐 **Dashboard UI Refinements**:
  - Harmonized symmetric card dimensions across Eco, Smart, and Beast cards.
- 🏎️ **Performance & Stability**:
  - Improved non-blocking discovery timeouts and updated underlying dependencies.

---

## 🌟 What's New in v1.3.0

- 📱 **Phone Control & SpaceDesk Integration**:
  - 🖥️ **SpaceDesk Virtual Display**: Dedicated green 1-click launcher & auto-installer to turn phones and tablets into wireless secondary PC displays *(Credit to datronicsoft)*.
  - 📂 **LAN Drop (Wi-Fi Quick Share)**: High-speed offline file drop and clipboard note sync between PC and phone via dynamic QR code *(Credit to yadavnikhil03)*.
  - 📱 **Android Screen Mirroring (scrcpy)**: Ultra-low latency device mirroring, stay-awake / screen-off power toggles, resolution & FPS presets, and 1-click APK sideloading *(Credit to Genymobile)*.
- 🛍️ **Software Hub & Gaming Launchers**:
  - Direct integration with **Steam**, **Epic Games Launcher**, **Rockstar Games Launcher**, **Ubisoft Connect**, **Battle.net**, **GOG Galaxy**, **EA App**, **DaVinci Resolve (Free)**, **MSI Afterburner**, **CPU-Z**, **GPU-Z**, **HWiNFO**, **CrystalDiskInfo**, **OBS Studio**, **Discord**, **VLC**, **7-Zip**, **Notepad++**, and runtimes.
  - Strict 2-button flow: **Open** / **Uninstall** (when installed) or **Install** (when not installed).
  - 3-stage resilient installation with non-blocking stdin handling and instant verified official download website fallbacks.
- 🌐 **Zero Hardcoding & Universal Windows 11 Compatibility**:
  - Fully dynamic path resolution across any Windows drive (C:, D:, E:, etc.), user profile directory, and system locale.
- ⚙️ **Smart Mode Performance Defaults**: Default power mode out-of-the-box is now set to **Smart Mode** for both AC and battery power, with custom dropdown selections for Eco, Smart, Beast, and Game Mode in Settings.
- 🔄 **Remember Last Selected Mode**: Added a toggle switch in Settings ➔ Startup to automatically persist and restore your last manually selected power mode across application restarts.
- 📖 **Interactive In-App Tool Help & Guides**: Comprehensive modal guides for all sidebar tools with step-by-step instructions, shortcuts, and direct Readme links.
- 🗑️ **Uninstaller Pro**: Deep native uninstaller supporting Win32, MSI, AppX, and WinGet apps with leftover scanning and automated registry safety backups.
- 🔋 **Modernized Battery Indicator**: Scaled 3x dynamic live battery indicator in the header with power-state awareness (AC Power / On Battery).
- 🌐 **Official Product Website**: Launched [AxePower Website](https://axepower.vercel.app/) with direct GitHub release auto-download.
- 🚀 **GitHub Releases Auto-Updater**: Native update engine that checks for latest releases, streams installer downloads with live progress bars, and updates seamlessly.
- 🧹 **Win11Debloat Integration**: One-click elevated execution of Windows debloating and telemetry removal scripts *(Credit to Raphire)*.
- 📈 **MSI Afterburner Companion**: Fast-launch and focus integration for GPU tuning, fan curves, and on-screen statistics *(Credit to MSI / Guru3D)*.
- 🔄 **Update PC Utility**: Elevated one-click driver and Windows update management via Windows Package Manager / USOClient.
- 🗔 **System Tray & Minimize-to-Tray**: Custom dark tray menu with quick mode switching and title bar minimize-to-tray button.
- 🛡️ **Clean Upgrades**: The installer automatically terminates running instances and uninstalls older versions cleanly before updating.
- 🔒 **100% Local Privacy**: Zero telemetry, zero analytics tracking, and zero remote data collection.

---

## 📥 How to Install

1. Go to the [Releases](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases) page or [AxePower Website](https://axepower.vercel.app/).
2. Download the latest **`AxePower-Setup.exe`**.
3. Run the installer and follow the quick setup wizard.
4. AxePower will automatically start and sit quietly in your system tray!

---

## 👤 Author & Support

- **Developer**: [Abhimanyu Vishwakarma](https://github.com/mrAbhimanyuVishwakarma)
- **Official Download Repo**: [AxePower_Download](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download)
- **Issue Tracker & Support**: [GitHub Issues](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues)
