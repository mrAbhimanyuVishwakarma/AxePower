# AxePower Development & Work Log 📋

**Repository:** [AxePower](https://github.com/mrAbhimanyuVishwakarma/AxePower)  
**Downloads & Releases Repository:** [AxePower_Download](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download)  
**Website:** [AxePower.com](https://axepower.com) (`AppWebsite/`)  
**Primary Architect & Developer:** [Abhimanyu Vishwakarma](https://github.com/mrAbhimanyuVishwakarma)  
**Last Updated:** September 23, 2026

---

## 🎯 Executive Summary

AxePower is an open-source, high-performance Windows PC Control Center & Power Optimizer built with **.NET 8 WPF**, **C# 12**, and **Domain-Driven Design (DDD)** with `CommunityToolkit.Mvvm`. It provides intelligent, automated power mode switching (**Eco**, **Smart**, **Beast**, **Game**), display refresh rate scaling (60Hz &harr; 144Hz/165Hz/240Hz), battery health telemetry, third-party integrations (MSI Afterburner, Win11Debloat, Winget, Gaming Launchers & Runtimes), an Uninstaller Pro engine, and a peer-to-peer local **Phone Control** suite (LAN Drop & scrcpy mirroring).

---

## 📂 Project Architecture

```
AxePower/
├── AxePower.App/                  # Presentation Layer (.NET 8 WPF, CommunityToolkit MVVM)
│   ├── Views/                     # Fluent Dark XAML Views (Dashboard, AutoSwitch, GameMode, Battery, About, Uninstaller)
│   ├── ViewModels/                # CommunityToolkit ViewModels (DashboardViewModel, AutoSwitchViewModel, etc.)
│   ├── Helpers/                   # Touchpad, System Tray, Process Helpers
│   └── Resources/                 # Icons, Mode graphics, and application assets
│
├── AxePower.Core/                 # Core Business & Infrastructure Layer (.NET 8 Class Library)
│   ├── Models/                    # PowerPlan, BatteryInfo, GameProfile, AppRule, UninstallerItem models
│   ├── Services/                  # PowerPlanService, BatteryService, GameScanner, LeftoverScanner, UpdateService
│   └── Utils/                     # Native Win32 interop (powercfg, PInvoke, ChangeDisplaySettingsEx)
│
├── AxePower.Tests/                # Automated Unit & Integration Tests (xUnit)
│
├── AppWebsite/                    # Official Product Website (AxePower.com)
│   ├── index.html                 # Semantic HTML5 single-page product presentation
│   ├── styles.css                 # Premium dark technical UI stylesheet
│   ├── script.js                  # Dynamic GitHub release resolver, FAQ accordion, modals & live telemetry sync
│   ├── assets/                    # Hi-res UI screenshots, hero photography, icons & favicons
│   ├── robots.txt                 # Search engine crawling rules
│   └── sitemap.xml                # Search engine index sitemap
│
├── present ui screenshot/         # Official screenshots (PC Dashboard, Mobile Dashboard UI, Home image)
├── Installer/                     # Inno Setup 6 compilation script (AxePowerSetup.iss)
├── README.md                      # Primary GitHub repository documentation
├── RELEASE_README.md              # User-facing release & download documentation
└── WORK_LOG.md                    # Continuing development and work log (this file)
```

---

## 🛠️ Work Completed & Verified

### 1. Official Single-Page Product Website (`AppWebsite/`)
- **Brand Fidelity & Theme-Adaptive Icons**:
  - Strictly standardized on **AxePower**.
  - **Dark Mode**: Uses `assets/AxePower_white.ico` for header brand logo, footer logo, and browser favicon.
  - **Light Mode**: Automatically switches to `assets/AxePower_black.ico` for header brand logo, footer logo, and browser favicon (with real-time toggle update and pre-render theme matching).
- **Hero Section**: Showcases the high-resolution Lenovo LOQ laptop + Android smartphone visual (`assets/website_home_image.png`) with clean presentation.
- **Android Companion App CTAs**:
  - Added dedicated **"Download Android App"** button with a distinct **"COMING SOON"** pill badge in Section 6 (Mobile Companion).
  - Added **"Android APK (Coming Soon)"** CTA button alongside the Windows installer in Section 8 (Final Download card).
  - Ready for direct APK link insertion when release is ready.
- **Real Screenshots & Devices Integrated**:
  - **Section 3 Dashboard**: Showcases the ASUS ROG STRIX SCAR gaming laptop render displaying the full AxePower Dashboard (`assets/dashboard_laptop.png`).
  - **Mobile Companion**: Modern smartphone mockup featuring both dark and light UI screenshots (`assets/dashboard_mobile.png` & `assets/dashboard_mobile_light.png`).
- **Full Dark & Light Mode Engine**:
  - Seamless theme toggle button in header with smooth sun/moon icon transitions.
  - High-contrast, deep obsidian typography in Light Mode for maximum readability across all cards, tags, and buttons.
  - Persistent preference stored in `localStorage` with automatic system theme fallback.
  - Zero layout shift or theme flash on page load.
- **Desktop Application Integration (`AxePower.App`)**:
  - **Official Website Integration**:
    - Added dedicated **`axepower.vercel.app ↗`** interactive button directly in the **Hero Card** of the **About** section next to the developer credit.
    - Updated `VisitWebsiteCommand` and `ViewAllReleasesCommand` in [`AboutViewModel.cs`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/ViewModels/AboutViewModel.cs) to open the official AxePower website: **`https://axepower.vercel.app/`**.
    - Clicking the website button, **"View release notes"**, or **"Visit website"** from the in-app **About** page opens `https://axepower.vercel.app/` directly in the default browser.
  - **License Card Updated**:
    - **Licensed to**: Automatically reads and displays the active Windows device name via **`Environment.MachineName`** (e.g. your PC's desktop name).
    - **License type**: Set to **`AxePower Pro`** in clear emerald green text.
  - **Game Mode Icon Modernization**:
    - Replaced the GameMode header badge with the purple cybernetic lion crest (`Gamemode.png`) in `GameModeView.xaml` and `AppWebsite`.
    - Set the **left sidebar navigation** Game Mode icon to the clean white `controller.png` in [`MainWindow.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/MainWindow.xaml) to maintain perfect visual consistency with all other white sidebar icons.
    - Replaced the card icons in `GameModeView.xaml`:
      - **Game Mode & App Switch**: Updated to `Resources/icons/controller.png` with blue backdrop.
      - **Game Booster (Background Apps)**: Updated to `Resources/icons/boost.png` with warning accent backdrop.
      - **System Services Optimizer**: Updated icon foreground to pure crisp white (`#FFFFFF`) matching the modern Game Mode styling.
  - **Auto Switch Icon Modernization (`AutoSwitchView.xaml`)**:
    - **Rule Cards & Summary Left**:
      - `When plugged in`: `Resources/icons/power.png` (plug icon)
      - `When unplugged`: `Resources/icons/poweroff.png` (unplugged icon)
      - `When battery below 30%`: `Resources/icons/battery_below30.png` (low battery level icon)
      - `Custom schedule`: `Resources/icons/custom.png` (custom palette icon)
    - **Summary Right Trailing Badges**:
      - `Switch to Beast Mode`: `Resources/icons/performance_beast.png`
      - `Switch to Eco Mode (Unplugged)`: `Resources/icons/tick.png`
      - `Switch to Eco Mode (Low Battery)`: `Resources/icons/tick.png`
      - `Switch to Smart Mode`: `Resources/icons/bolt_beast.png`
  - **Battery View Icon Modernization (`BatteryView.xaml`)**:
    - **Battery Health**: `Resources/icons/batteryhealth.png`
    - **Estimated Time Remaining**: `Resources/icons/timeremaining.png`
    - **Charge Level**: `Resources/icons/battery.png`
    - **Charge Cycles**: `Resources/icons/chargecycle.png`
    - **Temperature**: `Resources/icons/thermometer_beast.png`
    - **Current Power Usage**: `Resources/icons/bolt_beast.png`
  - **Header Battery Status Pill Scaled (3x Prominent Size)**:
    - Expanded the header battery status pill in [`MainWindow.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/MainWindow.xaml):
      - **3x Vector Battery Shell**: Scaled to `68x32px` with a `3.5px` border stroke and `14px` positive terminal notch.
      - **Expanded Fill Range**: Scaled live charge bar from `0` to `55px` in [`BatteryViewModel.cs`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/ViewModels/BatteryViewModel.cs).
      - **Bold Prominent Typography**: Increased font size to `20px` bold for crystal-clear readability across all displays.
      - **Pill Cushioning**: Generous `24x14px` padding with `16px` rounded corners.
  - **Sidebar Tools Reordering & Android Control Integration**:
    - Arranged bottom tools in [`MainWindow.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/MainWindow.xaml) strictly in requested order:
      1. **Android Control** (`scrcpy` live mirroring & control)
      2. **MSI Afterburner** (hardware monitoring & tuning)
      3. **Uninstaller Pro** (deep uninstall & leftover cleaning)
      4. **Update PC** (system driver & package updates)
      5. **Debloat** (Win11Debloat by Raphire)
      6. **About** (device license & updates)
    - Created **[`AndroidControlService.cs`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.Core/Services/AndroidControlService.cs)**:
      - Automatically detects or downloads the latest official Windows 64-bit release of [scrcpy](https://github.com/Genymobile/scrcpy.git) from GitHub.
      - Launches scrcpy directly to mirror and control connected Android devices.
      - Restores/focuses active instance if already running.
  - **In-App Interactive Tool Help & Guide System (`MainWindow.xaml`, `MainViewModel.cs`, `ToolInstructionItem.cs`)**:
    - Replaced generic external browser navigation on `(i)` icons with a unified in-app Fluent modal guide overlay.
    - Added dedicated instruction sets for all 5 sidebar tools (**Android Control**, **MSI Afterburner**, **Uninstaller Pro**, **Update PC**, **Debloat**):
      - **Overview Card**: Concise description of tool capabilities and operating mechanism.
      - **How To Use (Step-by-Step)**: Clear, numbered walkthrough on prerequisite setup and operation.
      - **Pro Tips & Shortcuts**: Key keyboard shortcuts, performance guidelines, and safety practices.
      - **Direct Readme Link**: Dedicated `📖 Readme` button pointing to `https://github.com/mrAbhimanyuVishwakarma/AxePower_Download#readme`.
      - **Official Source / Repo**: Direct link to the open-source repository or vendor website.
      - **One-Click Tool Launcher**: Directly launch or navigate to the tool right from the guide modal.
  - **Standardized Architecture for Adding Future Tools**:
    - Whenever a new tool is introduced to AxePower in the future, follow this strict protocol:
      1. **Service Layer**: Implement service in `AxePower.Core/Services/<ToolName>Service.cs` with auto-download or elevation handling.
      2. **Dependency Injection**: Register singleton in `AxePower.App/App.xaml.cs` and in `AxePower.Tests/Uninstaller/DependencyInjectionTests.cs`.
      3. **ViewModel**: Inject service into `MainViewModel.cs` and add `Open<ToolName>Command`.
      4. **In-App Guide**: Append a new `ToolInstructionItem` entry in `MainViewModel.InitializeToolGuides()` containing complete step-by-step instructions, tips, credit, and links.
      5. **Sidebar XAML**: Place the tool button in `MainWindow.xaml` with an `(i)` button bound to `Command="{Binding OpenToolHelpCommand}" CommandParameter="<ToolId>"`.
      6. **Documentation**: Update `README.md`, `RELEASE_README.md`, and `WORK_LOG.md`.
  - **Performance Defaults & "Start with Last Selected Power Mode" Support**:
    - Set out-of-the-box performance defaults in `AppSettings.cs` to **Smart Mode** for both `UnpluggedMode` (battery) and `PluggedInMode` (AC plugged in).
    - Upgraded **Performance Defaults** in [`SettingsView.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/Views/SettingsView.xaml) to fully interactive ComboBoxes with custom styled icons and colors:
      - **Default mode on battery**: `Eco Mode`, `Smart Mode`, `Beast Mode`, `Game Mode` (defaults to Smart).
      - **Default mode when plugged in**: `Eco Mode`, `Smart Mode`, `Beast Mode`, `Game Mode` (defaults to Smart).
      - **Fan mode on battery**: `Smart`, `Eco (Quiet)`, `Beast (Turbo)`.
    - Bound and implemented **"Start with last selected power mode"** toggle in [`SettingsView.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/Views/SettingsView.xaml):
      - When toggled ON: Tracks and remembers the last manually chosen power mode from the Dashboard/Tray menu (persisted in `settings.json` under `LastSelectedPowerMode`).
      - On application startup: If enabled, immediately restores and applies `LastSelectedPowerMode` and synchronizes AutoSwitch hardware state without overriding user's selection until a subsequent power event.
    - Upgraded all footer/header **"? Help"** buttons across the application ([`DashboardView.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/Views/DashboardView.xaml), [`AutoSwitchView.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/Views/AutoSwitchView.xaml), [`SettingsView.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/Views/SettingsView.xaml), [`AboutView.xaml`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/Views/AboutView.xaml)) to trigger the interactive in-app **Tool Instructions & Guide Modal**.
  - **Release v1.3.0 & Inno Setup Compilation**:
    - Bumped project version to `1.3.0` across [`AxePower.Core.csproj`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.Core/AxePower.Core.csproj), [`AxePower.App.csproj`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/AxePower.App.csproj), [`UpdateService.cs`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.Core/Services/UpdateService.cs), and [`SettingsViewModel.cs`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/AxePower.App/ViewModels/SettingsViewModel.cs).
    - Executed `dotnet publish -c Release -r win-x64` to compile standalone win-x64 binaries.
    - Compiled Inno Setup installer script [`Installer/AxePower.iss`](file:///c:/Users/Abhimanyu%20Vishwakarm/GitHub/AxePower/Installer/AxePower.iss) generating **`Installer/Output/AxePower-v1.3-Setup.exe`** (10.59 MB).
  - **Verification**:
    - `dotnet build -c Release` succeeded (0 errors, 0 warnings).
    - `dotnet test -c Release` passed all 76 unit tests.
    - `AxePower-v1.3-Setup.exe` generated successfully with LZMA solid compression.
- **Direct Installer Auto-Discovery**:
  - `initLatestRelease()` in `script.js` dynamically queries `https://api.github.com/repos/mrAbhimanyuVishwakarma/AxePower_Download/releases/latest`.
  - Automatically isolates the Windows `.exe` installer without hardcoding version numbers.
  - Direct one-click download execution.
- **Interactive Modals & Legal Sync**:
  - **Privacy Policy**: 100% aligned with desktop app's `AboutView.xaml` (100% local processing, zero telemetry, local Wi-Fi/USB companion sockets, third-party open source credits).
  - **Terms of Service**: Open-source MIT License terms and safe hardware operation guidelines.
  - **Contact & Community**: Direct links to GitHub Issues, Developer Profile, and official Discord server card.
  - **Dynamic Timestamps**: Automatically synchronizes copyright year (`2026`), version tags, and release dates.

### 2. Desktop Application Core Features
- **Auto Switch Service**: Real-time AC/DC detection and automated power plan application.
- **Dynamic Refresh Rate Scaling**: Switches display Hz via `ChangeDisplaySettingsEx` (e.g. 60Hz on DC battery vs 165Hz on AC power).
- **Phone Control SpaceDesk Integration**: Added a dedicated **SpaceDesk** action button (`#10B981` Emerald Green) in the Phone Control page header. Automatically launches SpaceDesk Driver Console if installed, installs `Datronicsoft.SpacedeskDriver.Server` via WinGet if not present, and provides instant fallback to the official `https://www.spacedesk.net/` portal.
- **Installer & WinGet Automation Robustness**: Added `--disable-interactivity` and process timeout protection to prevent WinGet from freezing on stdin prompts (e.g. Battle.net, GOG, EA App). Automatically triggers official download websites if silent installers fail.
- **Zero-Hardcoding & Universal Path Resolution**: Completely audited codebase to remove machine-specific hardcoded paths; dynamically resolves paths using `Environment.SpecialFolder`, `DriveInfo.GetDrives()`, and Windows Registry.
- **Phone Control (All-in-One Mobile Suite)**:
  - **LAN Drop**: Offline local Wi-Fi file & clipboard drop via QR code scanning powered by a lightweight zero-permission `TcpListener` HTTP server *(Credit to yadavnikhil03)*.
  - **Android Screen Control**: Ultra-low latency device mirroring via `scrcpy`, performance presets, stay-awake / screen-off power toggles, and direct APK sideloading *(Credit to Genymobile)*.
  - **Remote Companion**: Mobile browser portal with live PC battery percentage, volume/media keys, and 1-click PC lock.
- **Game Booster & Scanner**: Automatically suspends non-essential background processes during game sessions.
- **Uninstaller Pro Engine**: Full native app uninstallation with confidence-scored leftover filesystem and registry scanning.
- **System Tray Integration**: Hardcodet tray icon with quick mode switching and minimize-to-tray capability.
- **Win11Debloat & MSI Afterburner Companions**: One-click elevated launch helpers with creator attribution.
- **Built-in Auto-Updater**: Native GitHub releases update engine with download streaming and Inno Setup execution.

---

## 🚀 Development & Build Workflows

### Running the Website Locally
```powershell
# In PowerShell:
python -m http.server 8085 --directory "c:\Users\Abhimanyu Vishwakarm\GitHub\AxePower\AppWebsite"
# Access via browser at http://localhost:8085/
```

### Building & Running the Desktop Application
```powershell
# Restore and build the solution
dotnet build AxePower.sln -c Debug

# Run the WPF Desktop Application
dotnet run --project AxePower.App/AxePower.App.csproj

# Run all automated tests
dotnet test AxePower.Tests/AxePower.Tests.csproj
```

### Publishing a Release Build
```powershell
dotnet publish AxePower.App/AxePower.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o "AxePower.App/bin/Release/net8.0-windows/publish"
```

### Compiling the Inno Setup Installer
```powershell
# Compile the installer using Inno Setup 6
& "C:\Users\abhim\AppData\Local\Programs\Inno Setup 6\ISCC.exe" "d:\Projects\AxePower\Installer\AxePower.iss"
# Output installer: d:\Projects\AxePower\Installer\Output\AxePower-v1.6.0-Setup.exe
```

---

## 🚀 Version 1.6.0 Release Summary

1. **Task Manager-Style 2x2 Live Performance Grid**:
   - Replaced legacy right-rail widgets with a responsive 2x2 performance matrix:
     - **CPU**: Win32 `GetSystemTimes` high-precision CPU utilization % & live clock speed.
     - **GPU**: Direct Windows WDDM 3D engine utilization % & GPU temperature (RTX 4060 / Intel UHD).
     - **RAM**: Win32 `GlobalMemoryStatusEx` physical memory usage (GB & %).
     - **Internet**: IPv4/IPv6 delta bandwidth monitoring for live Send & Receive speeds.
   - Built custom `SparklinePathConverter` with `Viewbox` scaling to guarantee zero-baseline pinning (0% at bottom, 100% at top) with glowing gradient fills.
2. **24-Hour Battery History**:
   - Dedicated full-width battery trend card seeded with 24-hour continuous rolling historical curve.
   - Range header `· 24h to Now` with time-axis labels: `24h ago`, `12h ago`, `Now`.
3. **Steam-Style Taskbar Context Menu**:
   - Modernized tray right-click menu with `#171A21` Steam dark theme, 4 grouped sections, and edge-to-edge full-width dividers.
   - 1-Click dynamic mode toggling: Eco, Smart, Beast, Game Mode.
4. **Steam Update Banner & Scrollable Patch Notes Modal**:
   - Cyan/Blue update banner with `View patch notes` and `Restart & Install`.
   - Scrollable changelog feed with multi-post updates, scroll arrows (`▲`/`▼`), and interactive community reaction bar.
5. **Background Auto-Update Engine**:
   - Background GitHub release checks with silent installer download to `%TEMP%\AxePower_Update\`.
   - 1-Click installer execution with automatic restart.
   - Settings toggles for `AutoCheckUpdates` and `AutoDownloadUpdates`.
6. **Website Light Mode Visual Fixes**:
   - Fixed Game Mode Profile Workflow step titles to dynamic `var(--text-primary)` (readable dark text in light mode).
   - Added brightness/contrast filter to `MSI Afterburner.png` so the fighter jet logo renders in crisp dark slate in light mode.
7. **Window Layout Refinements**:
   - `? Help` button in title bar left of Minimize.
   - Window Close `✕` minimizes cleanly to System Tray.
   - Bottom status bar preserved with clean empty slot for future user widgets.
8. **Website & Help Center Documentation Sync**:
   - **`index.html`**: Updated features grid with Task Manager 2x2 telemetry, 24h battery history, SpaceDesk second display, and Audio Suite cards; updated privacy/terms modals to v1.6.0 with full credits.
   - **`features.html`**: Added showcase cards for Task Manager 2x2 Telemetry, SpaceDesk Wireless Display, Steam Context Menu & Background Updater, and Audio & Creative Tools Suite.
   - **`download.html`**: Bumped release version to `v1.6.0` and embedded a comprehensive "What's New in Version 1.6.0" changelog highlight matrix.
   - **`help.html`**: Added 4 extensive step-by-step documentation guides (`#telemetry`, `#spacedesk`, `#updater`, `#audio`), updated navigation pills, and added search filtering keywords.
   - **`sitemap.xml`**: Refreshed all URLs `<lastmod>` to `2026-09-01`.
   - **`README.md` & `RELEASE_README.md`**: Fully aligned with v1.6.0 features and tools.
9. **Installer Created**:
   - `d:\Projects\AxePower\Installer\Output\AxePower-v1.6.0-Setup.exe` built with Inno Setup 6.7.3.

---

## 🚀 Version 1.9.0 Release Summary

1. **Integrated Music Player & Media Hub**:
   - Full-featured audio playback engine with local music library scanner, playback queue, and background audio streaming.
   - **YouTube & Spotify Integration**: Seamless YouTube audio query/streaming and Spotify playback controller.
   - **Header Player**: Minimalist mini-player integrated right into the top header matching dashboard aesthetics.
   - **Dynamic Audio Output Switching**: Automatically detects and switches audio output device when the Windows default device changes without requiring an app restart.
2. **Share Screen & GameStream**:
   - Low-latency browser-based desktop and gameplay sharing over local network.
   - Viewers connect seamlessly via any standard browser without installing external software.
   - Display reset fixes for CamStudio and spacedesk virtual displays.
3. **Next-Gen Uninstaller Pro Overhaul**:
   - Deep leftover scanner equipped with heuristic confidence scoring, safety registry backup manifests, and file system probes.
   - **Broken Entry Executor** for safely cleaning broken or orphaned Windows uninstaller registry records.
   - Support for Win32, MSI, Squirrel, and modern AppX packages.
4. **Battery Diagnostics & UI Polish**:
   - Streamlined battery history card layout with responsive sparklines and cleaner metrics presentation.
   - Sidebar group expansion states remembered across application restarts.
   - Fixed binding errors and settings churn during dashboard load for faster initial rendering.
5. **Installer & Packaging**:
   - Compiled `AxePower-v1.9.0-Setup.exe` with Inno Setup 6 LZMA solid compression.

---

## 📌 Continuing Work & Next Action Items

1. **Android Companion APK Release**:
   - Link the companion APK download button to the Android release artifact repository when ready for public distribution.
2. **Continuous Deployment (CI/CD)**:
   - Configure GitHub Actions to automatically deploy `AppWebsite/` to GitHub Pages or custom domain `AxePower.com` upon commits to `main`.
3. **Installer Code Signing**:
   - Add digital certificate signing to `Installer/Output/AxePower-v1.6.0-Setup.exe` to avoid Windows SmartScreen warnings on new releases.
