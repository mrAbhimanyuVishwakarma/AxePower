---
name: phone-control
description: Phone Control suite — Android screen mirroring via scrcpy/ADB, SpaceDesk second monitor, LAN Drop (built-in HTTP file/clipboard/remote-control server with QR code), and Microsoft Phone Link launch.
---

# Phone Control

Four phone tools share one view, with one tab each. The sidebar has two entries: **Phone Control** (opens Screen Mirror) and **Second Monitor**.

| Tab key | Tab | Engine |
|---|---|---|
| `ScreenMirror` | Android mirroring & control | scrcpy + adb (downloaded on demand) |
| `SecondMonitor` | Tablet or phone as an extra display | SpaceDesk driver (datronicsoft) |
| `LanDrop` | Wi-Fi file, clipboard and remote drop | Built-in HTTP server |
| `PhoneLink` | Microsoft Phone Link | `ms-phonelink:` protocol |

Navigation keys: `PhoneHub`, `PhoneControl` and `AndroidControl` open ScreenMirror. `SecondMonitor`, `LanDrop` and `PhoneLink` open their own tabs (see `MainViewModel.Navigate`).

## Where it lives

| Piece | File |
|---|---|
| View / VM (all tabs) | [PhoneHubView.xaml](../AxePower.App/Views/PhoneHubView.xaml), [PhoneHubViewModel.cs](../AxePower.App/ViewModels/PhoneHubViewModel.cs) |
| Standalone LAN Drop view (DataTemplate still registered) | [LanDropView.xaml](../AxePower.App/Views/LanDropView.xaml), [LanDropViewModel.cs](../AxePower.App/ViewModels/LanDropViewModel.cs) |
| scrcpy / adb | [AndroidControlService.cs](../AxePower.Core/Services/AndroidControlService.cs) |
| LAN Drop server | [LanDropService.cs](../AxePower.Core/Services/LanDropService.cs), models [LanDropModels.cs](../AxePower.Core/Models/LanDropModels.cs) |
| QR rendering | [QrCodeGenerator.cs](../AxePower.Core/Helpers/QrCodeGenerator.cs) |

## Screen Mirror (scrcpy)
- **Tool management:** `EnsureLatestScrcpyExecutableAsync` checks `%LOCALAPPDATA%\AxePower\tools\scrcpy\`. It compares `installed_version.json` against `api.github.com/repos/Genymobile/scrcpy/releases/latest` and, when needed, downloads the `*win64*.zip` asset and extracts it there. `adb.exe` ships in the same zip.
- **Launch:** `LaunchOrInstallAsync(options)`. If a `scrcpy` process is already running, the service focuses it (`AlreadyRunning`) instead of starting another.
- **Options** (`AndroidControlOptions.BuildArguments`): `--stay-awake`, `--turn-screen-off`, `--no-audio`, `--max-fps=30|60|120`, `--max-size=720|1080` (0 = native), `--video-bit-rate=8M`, plus `-s <serial>` for the selected device.
- **Devices:** `adb devices -l`. A serial that contains `:` is a wireless device.
- **Wireless ADB:** `adb connect <ip:port>`. Success means the output contains "connected".
- **Sideload APK:** `adb [-s serial] install -r "<apk>"`.
- The phone needs USB debugging enabled, or Wireless debugging for the network route.

## Second Monitor (SpaceDesk)
- **Detection** (`GetSpaceDeskExePath`): looks for `spacedeskServiceTray.exe` / `spacedeskDriverConsole.exe` under Program Files (x86 too) `\datronicsoft\spacedesk\`, then the uninstall registry key "spacedesk Windows DRIVER".
- **Install:** `winget install --id Datronicsoft.SpacedeskDriver.Server --silent`. If that fails, it opens spacedesk.net.
- **QR modal** (`ShowDownloadQr`) links to the Android and iOS viewer apps and the HTML5 viewer (`viewer.spacedesk.net`).
- `OpenWindowsDisplaySettings` opens Windows display settings so the user can arrange the new monitor.

## LAN Drop
A hand-rolled HTTP/1.1 server on a `TcpListener` bound to **all interfaces** (default port **5050**). The phone just opens the URL (or scans the QR code) in a browser, with no app needed.

- **URL:** `http://<selected LAN IP>:<port>/`. Candidate IPs come from `LanDropService.GetLocalIpAddresses()`.
- **Received files** are saved to `%USERPROFILE%\Downloads\AxePower_LanDrop\`. The filename passes through `Path.GetFileName` to block path traversal.
- **Routes:**

| Method | Path | Purpose |
|---|---|---|
| GET | `/`, `/index.html` | Embedded mobile web portal (HTML is inline in `GetEmbeddedWebPortalHtml()`) |
| GET | `/api/status` | Shared files, clipboard messages, battery, server info (JSON) |
| GET | `/api/download?id=<fileId>` | Download a file the PC shared |
| GET | `/qr` | QR code image for the URL |
| POST | `/api/upload` | Multipart upload from the phone |
| POST | `/api/text` | Post a clipboard or text message |
| POST | `/api/remote` | Body is one of `play-pause`, `next`, `prev`, `vol-up`, `vol-down`, `mute`, `lock` |
| OPTIONS | * | CORS preflight |

- The remote actions send media and volume keys with `keybd_event`. `lock` calls `LockWorkStation()`.
- Events `OnFileReceived`, `OnTextReceived` and `OnStateChanged` drive the VM's received-files and messages lists.

> **Security note:** there is no pairing, token or PIN. Anyone on the same network who can reach the port can download shared files, upload files into Downloads, post messages, and **lock the PC**. Keep the server off on untrusted networks. Adding a per-session token to the URL/QR is the obvious hardening step.

## Phone Link
`LaunchPhoneLink` tries `ms-phonelink:`, then `ms-yourphone:`, then opens `https://aka.ms/phonelink`. The QR modal (`phonelink_android` / `phonelink_ios`) links the store pages for Link to Windows.

## Requirements
- Internet access the first time scrcpy is set up (GitHub download).
- SpaceDesk installs a display driver, which needs UAC.
- LAN Drop needs the PC and the phone on the same subnet. Windows Firewall may ask to allow AxePower on first start.

## Tests
[PhoneControlTests.cs](../AxePower.Tests/PhoneControlTests.cs), [LanDropServiceTests.cs](../AxePower.Tests/LanDropServiceTests.cs)
