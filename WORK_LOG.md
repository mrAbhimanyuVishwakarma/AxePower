# AxePower Development & Work Log 📋

**Repository:** [AxePower](https://github.com/mrAbhimanyuVishwakarma/AxePower)  
**Downloads & Releases Repository:** [AxePower_Download](https://github.com/mrAbhimanyuVishwakarma/AxePower_Download)  
**Website:** [AxePower.com](https://axepower.com) (`AppWebsite/`)  
**Primary Architect & Developer:** [Abhimanyu Vishwakarma](https://github.com/mrAbhimanyuVishwakarma)  
**Last Updated:** August 14, 2026

---

## 🎯 Executive Summary

AxePower is an open-source, high-performance Windows PC Control Center & Power Optimizer built with **.NET 8 WPF**, **C# 12**, and **Domain-Driven Design (DDD)** with `CommunityToolkit.Mvvm`. It provides intelligent, automated power mode switching (**Eco**, **Smart**, **Beast**, **Game**), display refresh rate scaling (60Hz &harr; 144Hz/165Hz/240Hz), battery health telemetry, third-party integrations (MSI Afterburner, Win11Debloat, Winget), an Uninstaller Pro engine, and a peer-to-peer local **Android Companion App**.

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
# Requires Inno Setup 6 (iscc.exe on PATH)
iscc "Installer/AxePowerSetup.iss"
# Output installer: Installer/Output/AxePower-Setup.exe
```

---

## 📌 Continuing Work & Next Action Items

1. **Discord Community Link**:
   - Update `contactModal` and `discordCard` in `AppWebsite/index.html` with the official Discord server invite link once generated.
2. **Android Companion APK Release**:
   - Link the companion APK download button to the Android release artifact repository when ready for public distribution.
3. **Continuous Deployment (CI/CD)**:
   - Configure GitHub Actions to automatically deploy `AppWebsite/` to GitHub Pages or custom domain `AxePower.com` upon commits to `main`.
4. **Installer Code Signing**:
   - Add digital certificate signing to `Installer/Output/AxePower-Setup.exe` to avoid Windows SmartScreen warnings on new releases.
