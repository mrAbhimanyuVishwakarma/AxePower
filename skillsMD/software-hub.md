---
name: software-hub
description: Software Hub app catalog — curated winget/Store packages (runtimes, launchers, tuning tools, productivity), install/update/uninstall, batch install, terminal install script, Ninite and portable-tool special cases.
---

# Software Hub (Gaming Runtimes & Tools)

A curated catalog of about 35 apps that install, update and uninstall through `winget`. It is the "📦 Gaming Runtimes & Tools" tab (`Apps`) of the Software Hub view. The other tabs are covered in [update-pc.md](update-pc.md).

## Where it lives

| Piece | File |
|---|---|
| Catalog, status detection, winget calls | [WinGetHubService.cs](../AxePower.Core/Services/WinGetHubService.cs) (interface [IWinGetHubService.cs](../AxePower.Core/Services/IWinGetHubService.cs)) |
| Model | [SoftwareHubModels.cs](../AxePower.Core/Models/SoftwareHubModels.cs): `SoftwarePackageItem`, `PackageCategory` |
| Card + page VM | [SoftwareHubViewModel.cs](../AxePower.App/ViewModels/SoftwareHubViewModel.cs): `PackageCardViewModel`, `SoftwareHubViewModel` |
| View | [SoftwareHubView.xaml](../AxePower.App/Views/SoftwareHubView.xaml) |
| Bundled Ninite | `Ninite Installer.exe` copied next to the app (`AppContext.BaseDirectory`) |

## Catalog
The catalog is hardcoded in `WinGetHubService.InitializeCatalog()`. Categories and their chip labels:

| `PackageCategory` | Label | Examples |
|---|---|---|
| `Runtimes` | Runtimes & DirectX | VC++ 2015+ x64, DirectX, .NET 8 Desktop Runtime |
| `Gaming` | Launchers & Gaming | Steam, Epic, Rockstar, Ubisoft, Battle.net, GOG, EA, WeMod, DLSS 5 Swapper |
| `Monitoring` | Hardware & Tuning | MSI Afterburner, CPU-Z, GPU-Z, HWiNFO, CrystalDiskInfo |
| `Utilities` | Tools & Productivity | Ninite, 7-Zip, OBS, Discord, VLC, Notepad++, DaVinci Resolve, Audacity, GIMP, Canva, Blender, HandBrake, PowerToys, ShareX, Peace EQ, Dolby Access, SteelSeries GG, CamStudio |

To add a package, append a `SoftwarePackageItem` in `InitializeCatalog()`. Set:
- `Id`: internal key.
- `WinGetId`: the exact winget ID, or a 12-character Store product ID such as `9N0866FS04B8`.
- `Name`, `Description`, `Category`, and `Glyph` (a Segoe MDL2 code point).
- `WebUrl`: fallback page.
- Optionally `LaunchProtocolOrExe`, used for install detection and the Open button.

## Package card states
- `IsWebLinkOnly`: only an external link, which opens after a warning modal (`ShowWebLinkWarning`).
- `HasCustomInstaller` (`LocalInstallerPath` or `CustomWebUrl` set): shows "run local installer" and "open website" buttons instead of install/update/uninstall. Ninite uses this path.
- `dlss5swapper`: the button reads "Download & Run". It downloads the latest portable GitHub release to `%LOCALAPPDATA%\AxePower\Tools\DLSS5-Swapper\` instead of using winget.
- `camstudio`: detected in `%LOCALAPPDATA%\AxePower\Tools\CamStudio\` or Program Files. Uninstalling it deletes the local folder.

## Install detection
`RefreshInstallationStatusesAsync` runs on a background thread in two passes:
1. **Fast pass** (`CheckIsInstalledFast`): checks that the `LaunchProtocolOrExe` file exists, that the URL protocol is registered under `HKCR` (for example `steam://`), or known registry keys per launcher (Steam `HKCU\Software\Valve\Steam`, Epic, Rockstar, Ubisoft, Battle.net, Afterburner).
2. **Slow pass**: parses `winget list` output once and matches each remaining package by `WinGetId` or name.

## Actions
All actions run `winget.exe` directly (no shell), stream stdout/stderr through `IProgress<string>`, and treat exit code `0` or `-1978335189` (already installed / no upgrade) as success.

| Action | Command | Timeout |
|---|---|---|
| Install | `winget install --id "<id>" --exact --source <winget\|msstore> --accept-* --silent` | 5 min |
| Update | `winget upgrade --id "<id>" --exact --source … --silent` | 5 min |
| Uninstall | `winget uninstall --id "<id>" --silent`, then retries without `--silent` | 35 s each |

- The source is `msstore` when the ID matches `^9[A-Z0-9]{11}$`, and `winget` otherwise.
- After a failed install, the service checks the disk with `CheckIsInstalledFast`, because wrapped installers can report a wrong exit code. If the package really is missing, it opens `WebUrl` in the browser.

## Batch install
- Selection helpers: `SelectAll`, `SelectUninstalled`, `ClearSelection`. Web-link-only cards are never selected.
- **Install selected** (`InstallSelectedDirectAsync`) installs the packages one at a time and logs progress to the Terminal.
- **Install via Terminal** (`InstallSelectedViaTerminal`) builds a PowerShell script of `winget install` lines, switches to the Terminal page and sends the script to the live shell. That shell inherits AxePower's elevation.

## Filtering
`ApplyFilter()` combines the category chip (`SelectedCategory`, `All` or a `PackageCategory` name) with a search over name, description and winget ID. Both properties re-run the filter when they change.

## Gotchas
- `winget list` matching by `Name.Contains` can report a false "installed" for packages with short or generic names.
- Install and upgrade run without elevation unless AxePower itself is elevated. Machine-scope installers then show their own UAC prompt.
- Ninite resolves `Ninite Installer.exe` from the app directory, so the installer must ship next to `AxePower.exe` (see [Installer/AxePower.iss](../Installer/AxePower.iss)).

## Tests
[SoftwareHubTests.cs](../AxePower.Tests/SoftwareHubTests.cs)
