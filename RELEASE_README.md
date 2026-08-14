# AxePower ⚡
### Intelligent Power Management & Gaming Optimizer for Windows

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/Framework-WPF%20%7C%20.NET%208-512BD4)
![License](https://img.shields.io/badge/License-MIT-green)
![Version](https://img.shields.io/badge/Version-1.3.0-blueviolet)

[Official Website](https://axepower.vercel.app/) • [Releases](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases) • [Issues & Support](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues)

</div>

Welcome to AxePower! This is a simple, set-and-forget tool designed to automatically optimize your laptop's battery life, thermal envelope, and performance.

---

## ⚡ What does it do?

AxePower manages your laptop's power settings and refresh rates based on whether your charger is plugged in or unplugged. 

Have you ever noticed your laptop running slowly when unplugged, or your battery draining too fast? AxePower solves this by offering four dedicated modes:

- 🔋 **Eco Mode:** Saves battery so your laptop runs longer when you are away from the charger.
- ⚖️ **Smart Mode:** The perfect middle ground for everyday tasks like browsing and watching videos.
- 🚀 **Beast Mode:** Gives you maximum power and sustained clocks for gaming or heavy tasks when plugged in.
- 🎮 **Game Mode:** Automatically detects running games, suspends background bloat, and locks maximum display refresh rate.
- 📱 **Android Control:** Direct screen mirroring and control of Android devices via high-speed USB/Wi-Fi connection.

**The best part?** You can turn on **Auto Switch**, and AxePower will automatically switch between these modes for you. Out-of-the-box, it defaults to **Smart Mode** for balanced performance, or you can choose your own defaults or enable **Start with last selected power mode** to remember whatever mode you chose manually!

---

## 🌟 What's New in v1.3.0

- ⚙️ **Smart Mode Performance Defaults**: Default power mode out-of-the-box is now set to **Smart Mode** for both AC and battery power, with custom dropdown selections for Eco, Smart, Beast, and Game Mode in Settings.
- 🔄 **Remember Last Selected Mode**: Added a toggle switch in Settings ➔ Startup to automatically persist and restore your last manually selected power mode across application restarts.
- 📱 **Android Control (scrcpy)**: Direct screen mirroring and control of Android devices via high-speed USB/Wi-Fi connection *(Credit to Genymobile)*.
- 📖 **Interactive In-App Tool Help & Guides**: Comprehensive modal guides for all sidebar tools (**Android Control**, **MSI Afterburner**, **Uninstaller Pro**, **Update PC**, **Debloat**) and `? Help` buttons with step-by-step instructions, shortcuts, and direct Readme links.
- 💬 **Attribution & "How to use" Tooltips**: Hovering over any sidebar `(i)` button shows the creator credit and "how to use" guide indicator.
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

1. Go to the [Releases](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/releases) page or [AxePower.com](https://axepower.com).
2. Download the latest installer (e.g. **`AxePower-v1.3-Setup.exe`**).
3. Open the downloaded file and follow the simple on-screen steps.
4. Once installed, search for "AxePower" in your Start Menu or open the Desktop shortcut!

---

## Getting Started

1. **Dashboard**: Open AxePower to view your battery status, current power plan, and switch modes manually.
2. **Auto Switch**: Go to the Auto Switch tab, turn on Auto Switch, and customize what happens when *Plugged In* vs *On Battery*.
3. **Game Mode**: Manage background processes you want suspended during games with custom lion badge.
4. **Tool Help & Guides**: Click the `(i)` icon next to any sidebar tool (Android Control, MSI Afterburner, Uninstaller Pro, Update PC, Debloat) to open interactive step-by-step instructions.
5. **System Tray**: Right-click the AxePower icon in your taskbar (near your clock) to switch modes or minimize to tray.
6. **About Page**: Check for updates, read release notes, or view developer and third-party credits.

---

## Need Help?

- **App isn't switching modes?** Make sure "Auto Switch" is turned on in the Auto Switch tab.
- **Is it safe?** Absolutely. AxePower just uses your computer's built-in Windows power settings automatically so you don't have to change them yourself.
- **Have an issue or suggestion?** Open an issue on our [GitHub Issue Tracker](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download/issues).

---

## 📜 Credits

- **[scrcpy](https://github.com/Genymobile/scrcpy)** by Genymobile
- **[Win11Debloat](https://github.com/Raphire/Win11Debloat)** by Raphire
- **[MSI Afterburner](https://www.msi.com/Landing/afterburner/graphics-cards)** by MSI & Guru3D (Alexey Nicolaychuk)
- **[Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)** by Philipp Sumi
- **[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)** by Microsoft & .NET Community

Developed with ❤️ by [Abhimanyu Vishwakarma](https://github.com/mrAbhimanyuVishwakarma)
