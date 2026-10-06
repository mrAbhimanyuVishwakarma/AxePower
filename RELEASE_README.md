# AxePower ⚡
### Intelligent Power Management & Gaming Optimizer for Windows

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/Framework-WPF%20%7C%20.NET%208-512BD4)
![License](https://img.shields.io/badge/License-MIT-green)
![Version](https://img.shields.io/badge/Version-2.1.0-blueviolet)

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
- 🎵 **Music Player:** In-app audio player supporting local audio libraries, YouTube Music streaming, and a dashboard-integrated Header Player.
- 🖥️ **Share Screen:** Browser-based low-latency desktop and gameplay streaming with zero setup for viewers.
- 📱 **Phone Control:** All-in-one mobile suite with offline **LAN Drop** Wi-Fi file sharing and **scrcpy** Android screen mirroring.
- 🛍️ **Software Hub & Updaters:** 1-click installer and launcher for gaming stores, hardware monitoring tools, Ninite multi-installer, drivers, and runtimes.
- 🖥️ **Built-In Terminal:** A real CMD / PowerShell window inside AxePower, where every command the app runs shows its live output.
- 🌡️ **True CPU & GPU Temperatures:** Reads your machine's real sensors — and keeps working on Windows 11 PCs with Memory Integrity turned on, where most monitoring tools show nothing.

**The best part?** You can turn on **Auto Switch**, and AxePower will automatically switch between these modes for you. Out-of-the-box, it defaults to **Smart Mode** when plugged in and **Eco Mode** on battery for optimal balance, or you can choose your own defaults!

---

## 🌟 What's New in v2.1.0

- 🧰 **System Care, rebuilt**: Optimizer and Doctor now share one focused workspace with a collapsible category rail, recommended actions, health repairs, cleanup, DNS tools, restore points, and live terminal output.
- 🛡️ **Windows Update controls**: the recommended security-updates-only policy, forced-reboot protection, Windows Update cache/service repair, and restore-point creation now execute their real Windows operations and report clear status.
- 🗂️ **Uninstaller redesign**: Win32 and Windows Apps get dedicated views in a full-width sortable table, with application type beside each app, ascending/descending ordering, batch selection, and **Select All** for leftovers.
- 🧭 **Responsive Software Hub**: categories move into a collapsible left rail. Install controls remain on the right when space allows and flow beneath app details only at compact widths, avoiding clipped cards and buttons.
- 🔔 **Notifications & Alerts**: desktop notifications, automatic-mode-change notices, and the low-battery alert chime are wired to persistent settings and runtime behavior.
- 🎮 **Game Mode clarity**: only the active mode glows after applying; a manually chosen Eco, Smart, or Beast card previews its glow before Apply. The Game Mode dashboard icon now fills its card height cleanly.
- 🚀 **Updater reliability fix**: background and manual updates now share one download, use a verified `.part` file, tolerate slow GitHub responses, surface progress and failures, validate the installer before launch, and reuse a completed download. Release notes are displayed without raw Markdown symbols.
- 🧹 **Navigation and UI polish**: Maintenance is now Windows Update, System Care, Uninstaller, and Debloat; Utilities contains Terminal and Tools. NVIDIA Control Panel and Versions use compact icon-only actions where required.
- 📦 **Packaging**: ships as **`AxePower-v2.1.0-Setup.exe`**.

## 🌟 What's New in v2.0.9

- 🔁 **Routines**: build Samsung-style **If → Then** automations from Tools → Timer & Routines.
  - Conditions cover time ranges, running apps, plugged-in/on-battery state, and battery level. Match every condition or any condition.
  - Actions can change the power mode, close or open apps, start the shutdown/restart/sleep timer, or show a notification.
  - Routines can restore what they changed when their conditions stop, and the most recently started routine wins when rules overlap.
- ⏱️ **Timer & Routines**: the Tools card now shows live timer/routine status and opens the full Routines editor.
- 🧰 **Expanded Tools**: Revo Uninstaller and WizTree join MSI Afterburner, CPU-Z, GPU-Z, and CrystalDiskInfo, with each card using the app's real icon.
- 🎨 **UI polish**: cleaner tray menus and consistent surfaces, controls, and status areas across built-in and custom skins.
- 📦 **Packaging**: ships as **`AxePower-v2.0.9-Setup.exe`**.

## What's New in v2.0.8

- 👤 **AxePower Account** (optional; everything still works without one):
  - Sign in with **Google, GitHub, Discord, Steam** or **email**. Your name and picture sit at the bottom of the sidebar.
  - A unique **@username** and a display name. The username can change once every 30 days.
  - Your sign-in is saved encrypted for your Windows user. Offline, AxePower keeps showing your profile.
- 🪪 **Profile**: About is now **Profile**: your account, plus version, updates, release notes, support and the legal pages in one place. Edit your profile, sign out, or delete your account there.
- 👥 **Friends on your account**:
  - Add friends by **@username**, with **suggestions as you type**. Accept or decline requests.
  - See who is **online, away or offline**, and what they're doing: "Playing Valorant", "In a call", "Sharing screen".
  - **Invite** a friend to a call or to watch your screen: they get a pop-up with **Join** on any page. Calls still connect directly (same Wi-Fi or Tailscale).
  - Your old contacts stay as **Saved rooms**. The name under the friends list is gone (the sidebar shows who you are), and the settings gear moved to the top.
- 🔒 **Privacy controls** in Settings → Friends: hide what you're doing, appear offline, choose whether people can find you by name, and manage blocked people. Blocking ends a friendship, and the other person isn't told.
- 📜 **Privacy Policy and Terms** now describe what an optional account stores, who can see it, and how to delete it.
- 🧹 **Fixes**: the Game Mode card's name no longer disappears in small windows, a muted microphone shows a crossed-out mic (it showed a magnifier), and primary buttons have readable text in the Light skin.
- 📦 **Packaging**: ships as **`AxePower-v2.0.8-Setup.exe`**.

## What's New in v2.0.7

- 🧰 **Tools**: the sidebar's MSI Afterburner entry is now **Tools**, a page with MSI Afterburner, CPU-Z, GPU-Z and CrystalDiskInfo. Each card opens the app, or installs it through WinGet straight from its publisher, and shows whether it is running. AxePower never bundles these apps.
- 🎨 **30 new skins**: Midnight Sakura, Synthwave Sunset, Molten Lava, Deep Ocean, Matrix Terminal, Paper & Ink, Mint Latte, Aurora Glass, Frosted Rose Glass (the first light glass skin), High Contrast, and twenty more across classic editor palettes and gaming looks. Every one passes a text-contrast check.
- ⚙️ **Settings**:
  - A category list beside the settings (it folds into an icon rail), one category per page, and slightly more compact rows.
  - **Taskbar preferences**: choose which items the tray menu shows. Settings and Exit always stay.
  - **Check for updates** in Settings works like the one in About, patch notes included.
  - The Theme list shows skin names, sorted with each glass version under its skin, and the mouse wheel no longer flicks through themes.
- 🔋 **Battery**: plugged in but not charging (for example with Conservation Mode) is shown as such; temperature comes from the battery's own sensor in °C or °F; the serial number and manufacture date are the battery's own. Chemistry shows "Not reported" when the battery doesn't say.
- 🖱️ **Touchpad**: the Settings switch really turns the touchpad off and on, using the same switch as Windows, and stays in step with it.
- 👥 **Friends**: the page follows every skin, glass included. Its settings category is renamed Friends.
- 🧹 **Fixes**: About page icons and Check for updates follow every skin; tray menu modes keep their colours (Eco green, Smart blue, Beast red, Game Mode purple).
- 📦 **Packaging**: ships as **`AxePower-v2.0.7-Setup.exe`**.

## What's New in v2.0.6

- ⏱️ **Power timer**:
  - Shut down, restart or sleep your PC after the time you type, or a quick 15 m / 30 m / 1 h / 2 h preset, from the button in the bottom-right corner of every page.
  - The countdown turns **red** for the last two minutes and a warning appears on screen. Its "Extend time in AxePower" line opens the timer so you can add 10 minutes or cancel.
  - The timer runs inside AxePower: exiting the app cancels it.
- 🎵 **Music**:
  - **Ambient Mode** fills the whole screen with album art and synced lyrics, and can open by itself like a **screensaver** after 1, 2, 5 or 10 idle minutes while music plays (Music Settings → Ambient Mode). It never covers a full-screen game or video.
  - **Lyrics are found for far more songs.** The catalogue lookup had been failing on most YouTube titles; it now matches them, and lines change within about 40 ms of being sung.
  - A **compact player**, **YT Music / Local Music** buttons in the header, **Last played** lists for each, the **last 7 YouTube searches** with their songs, and your **YouTube queue restored** after a restart.
  - **Add Folder** picks real folders (several at once), remembers them, scans in the background, and every folder card has a small **×** to remove it (your files are never touched).
- 🔋 **Battery**:
  - **Health is now real.** It is read from your battery's own design capacity; before, it was always shown as 98%. Expect your number to change.
  - A **Capacity over time** chart from the history Windows keeps, and **Open full battery report** for the complete Windows report.
- 🧹 **Cleaner pages**:
  - Bottom status bars removed from Battery, Phone Control, Optimizer, Doctor, Update PC and Uninstaller Pro.
  - The help window lists tools on the left and keeps one size for every tool.
  - Dropdowns are solid on glass skins, and the text cursor is visible in every text box on dark skins.
- 🔐 **Privacy & legal**:
  - A rewritten **Privacy Policy** and new **Terms of Service** in About, matching the website.
  - Complete open-source **license notices**, now shipped with portable builds as well as the installer.
- 🗑️ **Removed**: a Software Hub link to a site distributing cracked games, and a duplicate Settings button on the Battery page.
- 📦 **Packaging**: ships as **`AxePower-v2.0.6-Setup.exe`**.

> **Heads-up:** a YouTube Music queue saved by v2.0.4 cannot be restored once (it was saved without the details needed); from v2.0.6 on, it is kept across restarts.

---

## 🌟 What's New in v2.0.0

- 🖥️ **Share Screen — the real thing**:
  - Share any display to a friend's **browser**. They click a link; there is nothing to install on their side.
  - **Hardware encoding** on NVIDIA (NVENC), AMD (AMF) and Intel (QSV), picked automatically, with a software path only as a last resort.
  - **60 FPS is held by dropping resolution, not frames** — 1080p60 steps down to 900p60 and 720p60 when the link tightens, because motion matters more than pixels when a game is moving.
  - **System audio or microphone** rides along with the picture, following your default output device.
  - **In-app preview** shows the real stream through the real pipeline, so what you see is the delay your viewer sees.
  - **Remote control**, off by default: granted per session, and the code is never part of the watch link, so passing someone the link does not pass them your keyboard.
- 🎵 **Music — sources, discovery and a real library**:
  - **Provider-based sources** for YouTube Music and local files, with better track matching and a way to pick a different take when the match is wrong.
  - **Discovery** keeps the queue going when you reach the end, adding only to the end and never touching what you queued.
  - **Media shortcuts** that work from any application — play, skip, seek and volume — every key rebindable, with conflicts reported rather than silently failing.
  - **Library index and instant search** over your local music, by title, artist or album, however large the folder.
- 🔐 **Secured phone portal**:
  - The LAN portal now requires an access token carried by its QR link, and controls music live from your phone. **Older bookmarked links stop working** — rescan the QR code.
- 📦 **Packaging**:
  - Ships as **`AxePower-v2.0.0-Setup.exe`**, bootstrapping the .NET 8 Desktop Runtime and cleanly upgrading existing installations.

---

## 🌟 What's New in v1.9.0

- 🎵 **Integrated Music Player & Media Hub**:
  - Full in-app audio playback engine with queue management, track progress, volume control, and background playback.
  - **YouTube Music Integration**: Stream audio directly from YouTube Music inside AxePower.
  - **Dashboard Header Player**: Sleek mini-player embedded seamlessly in the top header matching the dashboard aesthetic.
  - **Dynamic Audio Device Following**: Automatically follows default Windows output audio device transitions without requiring an app restart.
- 🖥️ **Share Screen & GameStream**:
  - Stream your active desktop or gaming session directly to friends via a local web link.
  - Viewers connect straight from any modern browser with low latency and synchronized audio — no external client downloads required.
  - Stability fixes for CamStudio and spacedesk virtual display resets.
- 🗑️ **Next-Gen Uninstaller Pro Engine**:
  - Overhauled leftover scanner featuring confidence scoring, deep registry/filesystem probes, and pre-clean safety backup manifests.
  - Dedicated **Broken Entry Executor** to purge corrupted and orphaned Windows registry uninstallation remnants.
  - Expanded heuristics covering Win32 setups, MSI installers, Squirrel frameworks, and modern Windows AppX/MSIX packages.
- 🔋 **Battery View Diagnostics Optimization**:
  - Streamlined battery history card layout with responsive sparklines and cleaner metrics presentation.
  - Fixed binding errors and settings churn during dashboard load for faster initial rendering.
- 🧭 **UX & Navigation Enhancements**:
  - Sidebar group expansion states are now remembered across sessions.
  - Installer refreshed as **`AxePower-v1.9.0-Setup.exe`**, bootstrapping the .NET 8 Desktop Runtime and cleanly updating existing installations.

---

## 🌟 What's New in v1.8.0

- 🖥️ **Built-In Terminal (CMD & PowerShell)**:
  - A new **Terminal** page under *Maintenance* running a persistent interactive shell — switch between **PowerShell** and **Command Prompt**, type commands, and get real output.
  - It is now the one place AxePower's own commands report to: **Software Hub** installs and upgrades and **System Doctor** diagnostics (SFC / DISM / DNS benchmark) stream their progress here instead of hiding it behind a spinner.
  - A **Stop** button cancels whatever is running, including the long background diagnostics started from another page.
  - The shell inherits AxePower's privilege level, so when the app is elevated the terminal is elevated too.
- 🛡️ **Run as Administrator — Properly**:
  - New **Run as administrator** toggle in *Settings* (on by default), plus a **Relaunch with Admin** button in the title bar that disappears once you are already elevated.
  - AxePower keeps an `asInvoker` manifest and elevates by relaunching itself, so unlike most tools the setting can actually be switched **off**.
  - The logon task is registered at `HighestAvailable`, which means AxePower starts elevated at boot with **no UAC prompt at all**.
  - A refused UAC prompt no longer loops — the app simply carries on as a standard user.
- 🌡️ **Real CPU & GPU Temperatures That Survive Memory Integrity**:
  - New three-tier sensor stack, tried in order of reliability:
    1. **Lenovo ACPI-WMI firmware** (`LENOVO_GAMEZONE_DATA`) — needs no kernel driver, the same interface Lenovo Vantage and Legion Toolkit use.
    2. **MSI Afterburner shared memory** — free to read, no elevation required, and the numbers match what Afterburner already shows you.
    3. **LibreHardwareMonitor** — the traditional MSR route via its own kernel driver.
  - This matters: on current Windows 11 the MSR driver (WinRing0) is on Microsoft's vulnerable-driver blocklist, so with **Memory Integrity** enabled — the default on new machines — MSR-only tools silently report nothing. AxePower now falls through to a route that still works.
  - The UI says so when a reading comes from the ACPI chassis zone rather than the processor die, instead of passing an ambient number off as your CPU temperature.
- 💾 **One-Click System Restore Point**:
  - Create a Windows restore point from the Software Hub before making changes. AxePower enables System Restore on the system drive and lifts the once-per-24-hours throttle first, so the checkpoint is genuinely written rather than silently skipped.
- 🪟 **New Glass Theme (Mica & Acrylic)**:
  - The old *Transparent* theme is replaced by **Glass**, built on the real Windows 11 DWM backdrops, with an Acrylic blur-behind fallback for Windows 10 and early Windows 11 builds.
  - Existing settings files that still say `Transparent` migrate to `Glass` automatically — nothing to reconfigure.
  - Light, Dark and System themes were retuned alongside it.
- 🧭 **Dashboard Quick Actions Now Deep-Link**:
  - Dashboard tiles jump straight to the exact destination — e.g. a Gaming Runtimes tile opens the Software Hub already on the *Apps* tab with the *Gaming* category selected, rather than dropping you on a generic page.
  - Software Hub category chips now filter the list correctly when clicked.
- 🧰 **Portability & Housekeeping**:
  - Removed developer-machine paths that were baked into the shipped build — the Ninite launcher now resolves next to the installed executable, so it behaves the same for every user.
  - NVIDIA / AMD control-panel launchers are resolved through the Windows folder constants instead of a literal `C:\Program Files`, so they work on machines where Windows is not installed on C:.
  - Installer refreshed as **`AxePower-v1.8.0-Setup.exe`**, still bootstrapping the .NET 8 Desktop Runtime and cleanly removing older installs.

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
- 🔒 **Private by Default**: No ads and no usage analytics. Accounts are optional; without one, nothing about you is stored in AxePower's cloud. The in-app **Trust & Permissions** page lists every connection, what triggers it and what it sends.

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
