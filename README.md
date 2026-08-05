# AxePower
One-click laptop power optimizer.


![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/Framework-WPF%20%7C%20.NET%208-512BD4)
![License](https://img.shields.io/badge/License-MIT-green)

AxePower is a modern Windows desktop application that automatically optimizes your laptop's power plan based on your charger's connection status. Built with C#, WPF, and .NET 8 featuring a premium Fluent Dark UI.

## Screenshots

<img width="961" height="631" alt="image" src="https://github.com/user-attachments/assets/c9e3be08-5052-4928-992d-b5ce54540ea0" />

<img width="961" height="631" alt="image" src="https://github.com/user-attachments/assets/b849cb73-f24f-4ecc-a6c4-99beadd0ae67" />

## Features
- **Three Power Modes**: Quick toggle between Eco Mode (Battery Saver), Smart Mode (Balanced), and Beast Mode (Ultimate Performance).
- **Smart Auto Switch**: Automatically detects when you plug/unplug your laptop and applies your preferred power mode silently in the background.
- **Customizable Switch Delay**: Add a delay before switching plans to prevent rapid toggling if your charger connection is loose.
- **Safe & Secure**: Does not alter registry or critical Windows services blindly. Safely modifies standard Windows Power Plans using `powercfg`.
- **System Logs**: Keep track of mode switches and any background activities.
- **Premium UI**: Designed with glassmorphic cards, modern rounded aesthetics, and fluid interaction feedback inspired by Windows 11 Fluent Design.

## Tech Stack
- **Language**: C# 12
- **Framework**: .NET 8 WPF
- **Architecture**: MVVM (Model-View-ViewModel) using CommunityToolkit.Mvvm
- **Installation**: Inno Setup

## Project Structure
- `AxePower.App/`: WPF application, Views (Dashboard, Settings, Logs), and ViewModels.
- `AxePower.Core/`: Business logic, Services (`AutoSwitchService`, `PowerPlanService`), and Models.
- `Installer/`: Inno Setup script for building the `.exe` installer.

## Installation Steps
1. Download the latest `AxePowerSetup.exe` from the Releases page.
2. Run the installer and follow the prompt (it will create a Start Menu and optional Desktop shortcut).
3. Open AxePower and configure your Smart Auto Switch preferences.

## How to Use
1. Open **AxePower**.
2. **Dashboard**: View your battery percentage, trend, and quickly apply a power mode.
3. **Settings**: Enable "Auto Switch", set your desired behavior for when the laptop is plugged in, unplugged, or on low battery.
4. **Logs**: Check the logs to see the history of power mode switches.
5. **System Tray**: Right-click the AxePower icon in your taskbar tray for quick mode switches.

## Safety Note
AxePower uses standard Windows `powercfg` commands. It does not delete user files or make risky registry changes. If a command fails, it will safely fall back to the Windows 'Balanced' plan.

## Troubleshooting
- **Cannot switch to Beast Mode**: If your laptop does not support Ultimate Performance out of the box, AxePower will attempt to create it. If it fails, it will fall back to Smart Mode.
- **Auto Switch not working**: Ensure "Enable Auto Switch" is checked in Settings, and check the Logs for any errors.

## Build Instructions
1. Ensure you have the [.NET 8 SDK](https://dotnet.microsoft.com/download) installed.
2. Clone this repository.
3. To run locally:
   ```bash
   dotnet run --project AxePower.App
   ```
4. To generate a release build:
   ```bash
   dotnet publish AxePower.App/AxePower.App.csproj -c Release -r win-x64 --self-contained false
   ```

## Creating the Installer
1. Install [Inno Setup](https://jrsoftware.org/isinfo.php).
2. Open `Installer/AxePowerSetup.iss` in Inno Setup Studio.
3. Compile (Ctrl+F9). The installer will be in `Installer/Output`.

## Version History
- **v1.0.0**: Initial MVP Release. Added Dashboard, 3 modes, Auto switch, Settings, and Logs.

## AI Assistant / Developer Context
If you are an AI assistant (like ChatGPT, Claude, or Gemini) analyzing this repository to help the user code, please adhere to these project constraints and architectural patterns:

- **Architecture**: Strict MVVM pattern. The UI (`AxePower.App`) uses WPF and `CommunityToolkit.Mvvm`. Business logic and state (`AxePower.Core`) are completely decoupled from UI.
- **Dependency Injection**: Services (e.g., `PowerPlanService`, `AutoSwitchService`, `BatteryService`, `AppKillerService`) are registered as Singletons in `App.xaml.cs`. ViewModels are Transient and constructor-injected with these services.
- **Technology Stack**: .NET 8, C# 12 (use modern language features like primary constructors, pattern matching, records), and WPF. Null-safety is enabled.
- **State & Configuration**: `SettingsService` handles JSON-based persistence of `AppSettings`. `LogService` records background activities. 
- **System Interactions**: Modifying power plans is done via wrapping Windows `powercfg.exe` in `CommandRunner` for safety, avoiding direct registry edits.
- **UI Design**: The app uses modern Fluent Dark UI with rounded corners. Avoid legacy WPF UI elements. Rely on existing styles defined in `Resources/Styles.xaml`.
- **Project Structure**:
  - `AxePower.Core/Services/`: Background operations, system monitors (Battery, Powercfg).
  - `AxePower.Core/Models/`: Data structures (`AppSettings`, `PowerMode`, etc.).
  - `AxePower.App/ViewModels/`: Presentation logic, relies heavily on `[ObservableProperty]` and `[RelayCommand]` source generators.
  - `AxePower.App/Views/`: XAML definitions, bound exclusively to ViewModels.
