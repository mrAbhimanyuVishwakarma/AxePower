# AxePower architecture

**Status:** describes the code at `b68f001` plus the uncommitted working tree of 2026-10-05.
**Audience:** anyone changing AxePower.App or AxePower.Core.

This is a description of what exists, followed by a plan. Where the code is messy the document
says so. It is not domain-driven design: there are no aggregates, no repositories in the DDD
sense, no domain events, and no separate domain model. AxePower is a **two-project, service-
oriented WPF application**: a presentation project and a large class library of services, wired
together with Microsoft.Extensions.DependencyInjection.

---

## 1. Projects and what they may reference

| Project | Target | Role | References |
|---|---|---|---|
| `AxePower.App` | `net8.0-windows`, WPF (`UseWPF`) | Windows, views, view models, UI-only services (themes, hotkeys, tray, WebView2), the composition root (`App.ConfigureServices`) | `AxePower.Core`, CommunityToolkit.Mvvm, Hardcodet.NotifyIcon.Wpf, WebView2, MS DI |
| `AxePower.Core` | `net8.0-windows10.0.19041.0`, no UI framework | Every feature's logic **and** its Windows integration: registry, WMI, P/Invoke, processes, sockets, HTTP, SQLite, audio | NuGet only (LibreHardwareMonitorLib, Microsoft.Data.Sqlite, System.Management, MMI, YoutubeExplode, NAudio, Concentus, TagLibSharp, …) |
| `AxePower.Tests` | xUnit | Unit, integration, policy and fuzz tests | App and Core |

**Dependency direction today:** `App → Core`, never the reverse. Core contains no WPF, Forms or
Dispatcher types (checked: no `System.Windows.*` UI namespaces in Core). This is the one
architectural rule the code already keeps everywhere, and it must stay that way.

Out of the solution but part of the product: `Installer/` (Inno Setup), `supabase/` (Postgres
migrations and two Edge Functions: `delete-account`, `steam-login`), `AppWebsite/` (static site),
and `AxePower Mobile/` (a separate Flutter app, not covered here).

## 2. Processes and trust boundaries

```
 ┌──────────────────────────── AxePower.exe (one process, usually ELEVATED) ────────────────────────────┐
 │  WPF UI (App)  ──calls──►  Core services  ──►  registry · WMI · services · P/Invoke · SendInput     │
 │       │                         │                                                                    │
 │       │ WebView2 (Share Screen preview)          listeners:  LAN Drop  :5050 (chosen adapter)       │
 │       │                                                      Share viewer host :8890+ (all IPv4)    │
 │       │                                                      OAuth loopback (127.0.0.1, one shot)   │
 │       ▼                                                      single-instance named pipe            │
 │  child processes (inherit elevation): MediaMTX, FFmpeg, adb/scrcpy, winget, powershell, netsh,     │
 │  powercfg, reg, sc, schtasks, msiexec, uninstallers, tailscale, third-party tools                  │
 └────────────────────────────────────────────────────────────────────────────────────────────────────┘
        │ HTTPS out                    │ LAN / tailnet in                         │ local files
        ▼                              ▼                                          ▼
  Supabase (accounts, friends,   phones and browsers on the LAN,          %LOCALAPPDATA%\AxePower,
  presence, realtime), GitHub    friends' AxePower over LAN or             %APPDATA%\AxePower,
  (updates, tools), YouTube,     Tailscale                                 Downloads\AxePower_LanDrop
  LRCLIB, Kugou, NVIDIA, vendors
```

### 2.1 Elevation boundary

- `app.manifest` asks for `asInvoker`. Elevation comes from the `RunAsAdministrator` setting,
  **which defaults to `true`**. At startup `App.TryElevateAndExit` relaunches through
  `ElevationService.TryRelaunchElevated` (UAC). A refused prompt keeps the app running as a
  standard user, and a sentinel argument stops a relaunch loop.
- So in the default configuration **the whole process is elevated**, including the two network
  listeners, the WebView2 host, the YouTube and lyrics HTTP clients, and every child process.
  There is no split between a low-privilege UI and a privileged broker.
- A few services also start single processes with `Verb = "runas"` for the case where the app
  is not elevated: Debloat, DNS optimizer, driver update, MSI Afterburner, service optimizer, and
  `MainViewModel`.
- The installer launches the app with `runasoriginaluser`, and the Windows startup task uses
  HighestAvailable.
- **The Software Hub page lowers what it starts** (Prompt 19, 2026-10-05). Web pages, app
  protocols, "Open" and downloaded portable tools go through `UserShellLauncher`: when AxePower
  is elevated they are handed to Explorer as one argument and run as the signed-in user, and a
  failed hand-off is reported instead of falling back to an elevated start (`ElevationPolicy`).
  winget and vendor installers keep AxePower's rights; they elevate themselves anyway. Programs
  it runs without a shell come from `ExecutableAllowList` at fixed paths (System32, and winget
  from its package folder under Program Files when elevated, never the per-user alias folder).

Consequence: a remote-code-execution bug in any network-facing parser runs as administrator.
That is why Prompts 12–15 hardened those parsers so heavily. It is also why stage 7 below
(splitting privilege) is the long-term fix rather than more hardening.

### 2.2 Network boundary

| Direction | Endpoint | Owner | Exposure |
|---|---|---|---|
| Inbound | LAN Drop / phone portal, TCP 5050 (configurable) | `LanDropService` | Bound to the chosen adapter only; refused on Public/unclassified networks unless allowed per session; session token, cookie, rate limit (see `docs/security/`) |
| Inbound | Share Screen viewer host, TCP 8890 (next free port up to +50) | `ShareViewerHost` | All IPv4 interfaces (LAN plus Tailscale); refused on untrusted networks unless allowed; nonce/HMAC admission; HTTPS over Tailscale |
| Inbound | MediaMTX WebRTC/WHEP TCP 8889 and UDP 9189 (WebRTC port + 300) | `MediaMtxServer` (child process) | All interfaces; every read and publish checked by the viewer host |
| Loopback | MediaMTX RTSP ingest (8554), API (9997+) | `MediaMtxServer` | 127.0.0.1 only |
| Loopback | OAuth callback | `OAuthLoopback` | 127.0.0.1, one callback, then closed |
| Local IPC | single-instance pipe | `App` | named pipe, same user |
| Outbound | `kjnqsgdlcytpooueymdt.supabase.co` (REST, Auth, Realtime) | `SupabaseAccountApi`, `RealtimeFeed`, `FriendsService` | presence heartbeat every 60 s while signed in |
| Outbound | `api.github.com`, `github.com` | `UpdateService`, `ShareToolProvider`, `WinGetHubService`, tools | signed update manifest (see `docs/security/SIGNED_UPDATES.md`) |
| Outbound | YouTube / YouTube Music, `lrclib.net`, `lyrics.kugou.com`, cover-art hosts | Music providers and lyrics | only when Music uses them |
| Outbound | NVIDIA, MSI, vendor and tool download sites | `GraphicsDriverService`, `DriverUpdateService`, `ToolsService`, Software Hub | user-initiated |

There is **no product analytics or crash reporting**. Nothing in App or Core sends usage data.
"Telemetry" in the code means local hardware readings (§4, Power & telemetry) and Share
Screen's encoder statistics. Neither leaves the PC.

## 3. Responsibilities by layer (as the code actually divides them)

AxePower has no layer projects. Responsibility is divided by convention, and the convention is
followed unevenly. These are the roles a type plays, whatever folder it lives in:

| Role | What belongs there | Where it lives today |
|---|---|---|
| **Presentation** | Views, view models, converters, controls; UI-thread marshalling; prompts and dialogs | `App/Views`, `App/ViewModels`, `App/Converters`, `App/Controls`, `App/Windows` |
| **UI integration** | Things only a UI process does: themes/skins into resources, global hotkeys, tray, taskbar media buttons, WebView2, ambient window | `App/Services` (`ThemeService`, `HotkeyService`, `TaskbarMediaIntegration`, `AmbientWindowService`, `SkinResourceFactory`, prompt implementations) |
| **Application (orchestration)** | Use-case flow: start a share, sign in, run a batch uninstall, play a queue. Calls policy and adapters; holds session state | Core `*Service` classes such as `ShareScreenService`, `AccountService`, `FriendsService`, `MusicPlayerService`, `BatchUninstallService`, `RoutineEngine`, `DynamicModeAutomationService` |
| **Policy (pure logic)** | Decisions with no I/O: parsers, classifiers, catalogs, crypto formats, argument builders | Scattered: `UninstallCommandParser`, `ExitCodeInterpreter`, `UninstallEntryClassifier`, `PowerPlanCatalog`, `PlaybackQueue`, `QualityPreset`, `FfmpegArgumentBuilder`, `ShareScreenCrypto`, `RoomMessageCrypto`, `LanDropPathSanitizer`, `UsernameRules`, `AppNameNormalizer` |
| **Infrastructure adapters** | Files, SQLite, HTTP, sockets, child processes | `SettingsService`, `LogService`, `LibraryDatabase`, `*Store` classes, `SupabaseAccountApi`, `OAuthLoopback`, `MediaMtxServer`, `ShareToolProvider`, `LrcLibLyricsService`, `CoverArtService`, `CommandRunner`, `PowerShellRunner`, `TerminalService` |
| **Native integration** | P/Invoke, WMI/CIM, registry, Service Control Manager, `SendInput`, job objects, LibreHardwareMonitor | About 25 Core files declare P/Invoke; 8 use WMI; about 30 touch the registry; 3 use `ServiceController`. Only `Core/Native/PowerInterop.cs` is a dedicated native file |

The honest summary: Core mixes orchestration, policy and native calls inside the same classes.
The large ones (`BatteryService` 840, `UpdateService` 770, `SystemTelemetryService` 760,
`GraphicsDriverService` 710) each contain all three.
`LanDropService` (1,950 lines) was split by role on 2026-10-05; see LAN Drop below. So was
`WinGetHubService` (980 lines, now a 300-line coordinator); see Software Hub below. Feature folders that already separate them reasonably well are **Uninstaller**
(interfaces, executors, finders, policy types), **Music** (Engine, Providers, Library, Lyrics,
Integrations), and **Share Screen** (crypto, host, media server, pipeline as separate types).

## 4. Feature areas: owners, layers and dependencies

"Owner" means the type responsible for the feature's state and rules. View models present it
and should not re-implement it. Every service registered in `App.ConfigureServices` appears
below, grouped by feature, with the interface it is registered under where there is one. Lifetimes
are singletons unless noted.

### Accounts
| Type | Role | Depends on |
|---|---|---|
| `AccountService` (owner) | Application | `IAccountApi`, `AccountSessionStore`, `IClock`, `IOAuthLoopback` |
| `SupabaseAccountApi` (`IAccountApi`, `IFriendsApi`) | Infrastructure (HTTPS) | `SupabaseProject` (URL and publishable key) |
| `AccountSessionStore` | Infrastructure (DPAPI file) | `LogService` |
| `OAuthLoopback` | Infrastructure (loopback socket) | — |
| `UsernameRules` | Policy | — |
| Presentation | `AccountViewModel` (singleton), Profile views | `AccountService`, `IClock` |

### Friends
| Type | Role | Depends on |
|---|---|---|
| `FriendsService` (owner) | Application (presence heartbeat, requests, realtime) | `AccountService`, `IFriendsApi`, a `SupabaseRealtimeFeed` factory, `IClock`, `SettingsService`, and a delegate that asks `GameModeGuard` for the running game |
| `SupabaseRealtimeFeed` (`RealtimeFeed.cs`) | Infrastructure (Supabase Realtime over `ClientWebSocket`) | `SupabaseProject` |
| Presentation | `FriendsHubViewModel` (singleton) | `FriendsService`, `AccountViewModel` |

Depends on **Accounts** (sign-in session) and, through a delegate, on **Game Mode** (presence
shows the running game). Share Screen's settings view model reads it for activity sharing.

### Music
| Type | Role | Depends on |
|---|---|---|
| `MusicPlayerService` / `IMusicPlayerService` (owner) | Application | `IAudioEngine`, `PlaybackStateStore`, `LooseTrackReader`, `SettingsService`, `NotificationService`, providers |
| `NAudioEngine` (`IAudioEngine`) | Native integration (WASAPI) | NAudio |
| `ProviderRegistry`, `LocalFileProvider` and `YouTubeProvider` (both `IMusicProvider`), `StreamResolver` (`IStreamResolver`), `StreamCache`, `DiscoveryService` | Application + infrastructure (HTTP, files) | YoutubeExplode, `MusicStoragePaths` |
| `LibraryDatabase`, `MusicLibraryRepository` | Infrastructure (SQLite) | `MusicStoragePaths` |
| `TagLibTagReader` (`ITagReader`), `LooseTrackReader`, `CoverArtService` (`ICoverArtService`) | Infrastructure (files, HTTP) | TagLibSharp |
| `LrcLibLyricsService` (`ILyricsService`), `YouTubeCaptionLyricsProvider` (`IYouTubeCaptionLyricsProvider`) | Infrastructure (HTTP) | — |
| `PlaybackStateStore`, `RecentlyPlayedService`, `RecentSearchesService` | Infrastructure (JSON files) | `MusicStoragePaths` |
| `SmtcIntegration`, `RecentlyPlayedService`, `TaskbarMediaIntegration` (all `IPlayerIntegration`), `PlayerIntegrationHost` | Native integration (SMTC, taskbar) and history | — |
| `MusicRemote` | Application (phone remote API) | `IMusicPlayerService` |
| `DpapiSecretStore` (`ISecretStore`) | Infrastructure (DPAPI file) | `MusicStoragePaths` |
| UI integration (App) | `MusicDispatcher`, `HotkeyService`, `MusicShortcutHandler`, `TaskbarMediaIntegration`, `AmbientWindowService`, `MusicPaletteExtractor` (`IPaletteExtractor`), `FileDropHelper` | |
| Presentation | `NowPlayingViewModel`, `LyricsViewModel`, `AmbientModeViewModel`, `SourcesViewModel`, `ShortcutsViewModel` (singletons); `MusicViewModel`, `MusicSettingsViewModel` (transient) | |

Misplaced shared abstractions: `IClock` and `ISecretStore` live in the Music namespace but are
used by Accounts, Friends and `PortalAccessToken`.

### LAN Drop and Phone Hub
| Type | Role | Depends on |
|---|---|---|
| `LanDropService` (owner, about 490 lines) | Application: listener and session lifecycle (start, stop, watchdog, network changes), connection throttle, the public API and events | the components below, `PortalAccessToken`, `NetworkProfileClassifier` |
| `LanDropRequestHandler` | Application: one connection (lockout, read, size rules, auth, route) | `LanDropHttp`, `LanDropAuthenticator`, `LanDropClientRegistry`, endpoints |
| `LanDropHttp`, `LanDropRequest` | Infrastructure: bounded request reading, parsing, response writing | — |
| `LanDropAuthenticator`, `LanDropClientRegistry` | Policy + state: token/cookie/query checks, lockout, device bar; connected devices | `PortalAccessToken`, `AuthenticationRateLimiter` |
| `LanDropEndpoints` (`ILanDropHost` back to the service), `LanDropMusicEndpoints` | Application: one method per route | `LanDropStorage`, `LanDropClipboard`, `IRemoteMediaActions`, `MusicRemote` |
| `LanDropStorage`, `LanDropClipboard`, `LanDropDeviceNames` | Infrastructure (files) and state | `StreamingMultipartParser`, `LanDropPathSanitizer` |
| `WindowsRemoteMediaActions` (`IRemoteMediaActions`), `RemoteMediaActionParser`, `HostPowerStatus` | Native integration (media keys, lock, battery) and policy | — |
| `LanDropPortalPage` | Content (the phone page) | — |
| `PortalAccessToken` | Policy + state (session token) | — |
| `LanDrop/*` (parser, sanitizer, limits, classifier, rate limiter) | Policy and native (classifier uses the Network List Manager COM API) | — |
| `AndroidControlService` | Infrastructure (adb, scrcpy child processes) | — |
| Presentation | `PhoneHubViewModel` (the reachable UI); `LanDropViewModel`/`LanDropView` (registered but **not reachable** from navigation) | |

Depends on **Music** (`MusicRemote`). **Share Screen** reuses `AuthenticationRateLimiter` and
`NetworkProfileClassifier` from here; they are shared network utilities living in a feature folder.

### Share Screen (with room chat, voice and remote control)
| Type | Role | Depends on |
|---|---|---|
| `ShareScreenService` / `IShareScreenService` (owner) | Application | everything below, `TailscaleCertificateProvider`, `NetworkProfileClassifier` |
| `ShareViewerHost` | Infrastructure (HTTP/WebSocket/TLS server) + policy (admission) | `RemoteInputService`, `ShareScreenCrypto`, `AdmissionNonces`, `ReplayFilter`, `AuthenticationRateLimiter` |
| `MediaMtxServer`, `ShareEncodePipeline`, `ShareToolProvider` (`IShareToolProvider`), `ShareEncoderProbe` (`IShareEncoderProbe`) | Infrastructure (child processes, downloads) | `ChildProcessJob` |
| `ShareAudioPump`, `RoomVoiceService` (`IRoomVoiceService`), `ShareDeviceProvider` (`IShareDeviceProvider`) | Native integration (WASAPI, Opus) | NAudio, Concentus |
| `RemoteInputService` | Native integration (`SendInput`) | — |
| `ShareScreenCrypto`, `RoomMessageCrypto`, `RoomVoiceCrypto`, `QualityPreset`, `FfmpegArgumentBuilder` | Policy | — |
| `ShareChatStore`, `ShareAttachmentStore` | Infrastructure (DPAPI files) | — |
| `TailscaleCertificateProvider` | Infrastructure (`tailscale` CLI) | — |
| `ShareRoomSession` (joining client), `ShareRoomLink` | Infrastructure (`ClientWebSocket`s, admission, decryption, replay windows) | `ShareRoomClient`, `RoomMessageCrypto`, `RoomVoiceCrypto`, `ReplayFilter` |
| `ShareRoomConversation`, `ShareRoomRouter`, `ShareAttachmentChunker`, `ShareAttachmentAssembler`, `ShareVoiceCoordinator` | Application (chat history, file chunking and reassembly, voice routing; no UI) | `IShareScreenService` or `ShareRoomSession`, `ShareChatStore`, `ShareAttachmentStore`, `IRoomVoiceService` |
| Presentation | `ShareScreenViewModel`, `ShareScreenSettingsViewModel` (singletons, so a live share survives navigation) | |

`ShareScreenViewModel` and `ShareRoomSession` are created per page, not registered: the view model
builds a `ShareRoomSession` when the user joins a room, and routes chat and voice through
`ShareRoomRouter` to either the host service or that session. The view model holds no sockets,
crypto, file or `Task.Run` code (moved to Core in Prompt 18, 2026-10-05).

### Uninstaller
| Type | Role | Depends on |
|---|---|---|
| `BatchUninstallService` / `IBatchUninstallService` (owner of a run) | Application | `UninstallExecutorFactory`, `IProcessTreeWatcher`, `IInstallStateVerifier` |
| `ApplicationDiscoveryService` (`IApplicationDiscoveryService`), `FileSystemProbe` (`IFileSystemProbe`), `ProcessDetectionService` (`IProcessDetectionService`) | Native integration (registry, MSI, Appx, files, processes) | `IMsiProductStateReader` |
| `Win32UninstallExecutor`, `MsiUninstallExecutor`, `AppxUninstallExecutor`, `BrokenEntryExecutor`, `UninstallExecutorFactory`, `UninstallerLauncher` | Infrastructure (processes) | `PowerShellRunner`, `NativeProcesses` |
| `LeftoverScannerService` (`ILeftoverScannerService`), `LeftoverCleanupService` (`ILeftoverCleanupService`), `Leftovers/*` finders | Application + native (registry, files, tasks, services, firewall) | `IProtectionService`, `IRegistryBackupService` |
| `ProtectionService`, `ProtectedPaths`, `UninstallEntryClassifier`, `UninstallCommandParser/Builder`, `ExitCodeInterpreter`, `UninstallerKindDetector` | Policy | — |
| `RegistryBackupService`, `BackupManifestStore`, `UninstallHistoryService` (`IUninstallHistoryService`), `FileCleanupService` (`IFileCleanupService`), `SystemRestoreService` | Infrastructure | `reg.exe`, WMI |
| Presentation | `UninstallerViewModel` (+ `.Leftovers` partial), item view models; `IUninstallerPrompts` → `MessageBoxUninstallerPrompts` | |

This is the most cleanly separated feature: interfaces for every adapter, policy types
unit-tested on their own.

### Software Hub, drivers and updates
| Type | Role | Depends on |
|---|---|---|
| `WinGetHubService` / `IWinGetHubService` (owner) | Application: plans each action, runs the steps, reports progress, batches with partial failure and cancellation | everything below |
| `SoftwareCatalog` | Policy + data: the package list; entries that fail validation are left out | `PackageInput` |
| `PackageDiscovery` (`IPackageDiscovery`) | Infrastructure: registry, protocol and file checks, one `winget list`, App Paths for "Open" | `IProcessRunner` |
| `InstallPlanner` | Policy (pure): action → steps (winget arguments, fallbacks, refusals, rights per step) | `SoftwareCatalog`, `PackageInput` |
| `PackageInput` | Policy (pure): identifier, URL, download-host and file-name checks | — |
| `GitHubPortableToolDownloader` (`IPortableToolDownloader`) | Infrastructure (HTTP): allowed hosts only, temp file, PE check, then move | — |
| `AllowListedProcessRunner` (`IProcessRunner`), `ExecutableAllowList`, `ProcessCommand`, `ProcessTreeJob` (`Services/Processes`) | Infrastructure: the one way to run a non-interactive program; `ArgumentList`, stdin closed, timeout, a job object per run so cancel ends exactly that tree | — |
| `UserShellLauncher` (`IUserShellLauncher`), `ElevationPolicy` | Infrastructure + policy: validated pages, protocols and programs, as the signed-in user when elevated | `ExecutableAllowList` (Explorer) |
| `WindowsUpdateShieldService` (`IWindowsUpdateShieldService`) / `WindowsUpdateSystem` | Native integration (services, registry) | `ServiceController` |
| `DriverUpdateService` | Infrastructure (an interactive elevated PowerShell window; inline `-EncodedCommand` when already elevated) | `ExecutableAllowList` |
| `FirmwareUpdateService`, `GraphicsDriverService` | Infrastructure + native (WMI, vendor HTTP); launches through the launcher and runner, driver downloads only from vendor hosts | `IUserShellLauncher`, `IProcessRunner` |
| `RestorePointService` | Native integration (PowerShell `Checkpoint-Computer`) | `IProcessRunner` |
| `TerminalService` | Infrastructure (PowerShell host) | — |
| `UpdateService` + `Update/*` | Application + infrastructure (GitHub, Authenticode, manifest signature) | `IManifestVerifier`, `IAuthenticodeVerifier` |
| Presentation | `SoftwareHubViewModel` (1,136 lines: commands and state only; card and batch Cancel), `TerminalViewModel`, `AboutViewModel` (update UI) | |
| Views | `SoftwareHubView` (68 lines: header, tabs, where the parts go) with `Views/SoftwareHub/UpdatersTab`, `PackagesTab`, `UpdateShieldTab`, `WebLinkWarningDialog`, `NvidiaDriverPickerDialog`; styles and converters in `Resources/SoftwareHubStyles.xaml` | `SoftwareHubViewModel` (inherited DataContext) |

Tests: `AxePower.Tests/SoftwareHub/` covers malicious identifiers, quoting round-trips through a
real process, exit codes, timeout, cancellation (including an orphaned grandchild only the job
object can reach), elevation decisions, partial batch failure, downloads and the view model.
`SoftwareHubUiCode_BuildsNoCommandLines` fails if the page's UI code starts a process or spells
out a command.

### Power, Game Mode, battery and hardware telemetry
| Type | Role | Depends on |
|---|---|---|
| `PowerPlanService` (owner of plans), `PowerPlanCatalog` | Native integration (`powercfg`, `PowerInterop`) + policy | — |
| `AutoSwitchService`, `DynamicModeAutomationService` (`IDynamicModeAutomationService`), `TimeoutService`, `AppMonitorService` | Application | `PowerPlanService`, `SettingsService` |
| `GameModeGuard`, `GameModeGpuService`, `LenovoGpuDisplayModeService` (`IGpuDisplayModeController`), `GameScannerService`, `AppKillerService` | Application + native (registry, processes, WMI) | — |
| `ServiceThrottlerService` (`IServiceThrottlerService`), `SystemServiceOptimizer`, `MemoryOptimizerService` (`IMemoryOptimizerService`) | Native integration (services, P/Invoke) | `ServiceController` |
| `BatteryService` | **Mixed** (WMI, registry, P/Invoke, charge policy) | `BatteryDeviceReader` |
| `HardwareMonitorService`, `SystemTelemetryService`, `LenovoSensorReader`, `AfterburnerSensorReader`, `MsiAfterburnerService` | Native integration (LibreHardwareMonitor, WMI, shared memory) | — |
| `DisplayService`, `TouchpadService` / `WindowsTouchpadPlatform`, `ControllerNavigationService` | Native integration | — |
| `PowerTimerService` / `IPowerActions` → `WindowsPowerActions` | Application + native | — |
| `RoutineEngine`, with `WindowsRoutineWorld` (`IRoutineWorld`) and `WindowsRoutineActions` (`IRoutineActions`) | Application with native seams | power, display, processes |
| Presentation | `DashboardViewModel`, `GameModeViewModel` (1,240 lines), `BatteryViewModel`, `AutoSwitchViewModel`, `PowerTimerViewModel` (singleton), `RoutinesViewModel`; `WindowGameModePrompts` (`IGameModePrompts`) in App | |

### System care, tweaks and tools
| Type | Role | Depends on |
|---|---|---|
| `TweakService` (`ITweakService`), `CacheCleanerService` (`ICacheCleanerService`), `DnsOptimizerService` (`IDnsOptimizerService`), `SystemDoctorService` (`ISystemDoctorService`), `DebloatService`, `StartupService` | Native integration (registry, netsh, sfc, processes) | — |
| `ToolsService` | Infrastructure (third-party tool launch and download) | — |
| Presentation | `SystemCareViewModel` → `SystemOptimizerViewModel`, `SystemDoctorViewModel`; `DebloatViewModel`; `ToolsViewModel` | |

### Shell and cross-cutting
| Type | Role | Notes |
|---|---|---|
| `SettingsService` | Infrastructure (one JSON file) | Owns `AppSettings`, a single object of 49 properties plus `Music`, `Uninstaller`, `ShareScreen` and `Routines` sections. Almost every view model and many services take the whole service. |
| `LogService`, `LogRedactor` | Infrastructure (log file + in-memory list) | Shown by `LogsViewModel`. Every line is redacted (tokens, links, codes, cookies, emails) before it is shown or written. Catch-block rules and the remaining debt: `docs/engineering/EXCEPTION_DEBT.md`, enforced by `ExceptionHandlingPolicyTests`. |
| `NotificationService`, `WindowsAlertSoundService` (`IAlertSoundService`) | Presentation support (in Core, so services can raise toasts) | |
| `ElevationService` | Native integration (token query, UAC relaunch) | |
| `SkinCatalog` (Core), `ThemeService`, `SkinResourceFactory` (App) | Policy + UI integration | Skins are data (`skins/<id>/skin.json`) compiled to WPF resources in App. |
| `MainViewModel` | Presentation (shell) | Takes every page view model in its constructor, plus tray, update and elevation actions. |
| `MainWindow` + `Views/Shell` | Presentation (shell views) | Since Prompt 20 (2026-10-05) the window (282 lines) keeps only the title bar, content area, snackbar, invite toast and tray icon, because its code-behind needs those. `ShellSidebar`, `ToolHelpOverlay`, `UpdateBanner` and `PatchNotesDialog` are user controls bound to `MainViewModel`. `Resources/ViewTemplates.xaml` maps page view models to views, `Resources/ShellChromeStyles.xaml` holds the caption buttons and tray frame, and `Resources/TrayMenuStyles.xaml` the tray items (merged only into the tray menu). |

**Splitting a view.** A user control resolves `StaticResource` keys when it loads, before it joins
the window, so it sees its own and the application's resources but not the window's. Keys it
needs belong in App resources or a dictionary the control merges. Bindings that use `RelativeSource
AncestorType=UserControl` find the nearest control, so a new control must inherit the same
DataContext. `AxePower.Tests/Shell/ShellViewTests.cs` checks binding errors, resource keys in every
skin, and arrow-key navigation. `docs/engineering/VISUAL_CHECKS.md` is the owner's manual check.

## 5. Data ownership and persistence

Each file has one owning type. Nothing else should read or write it directly.

| Data | Location | Owner | Format / protection |
|---|---|---|---|
| App settings (all features) | `%LOCALAPPDATA%\AxePower\settings.json` | `SettingsService` | JSON, plain |
| Log | `%APPDATA%\AxePower\AxePower.log` (+ `.old`, rotated at 5 MB) | `LogService` | text; must never contain secrets (tested for LAN Drop tokens) |
| Account session | `%LOCALAPPDATA%\AxePower\Account\session.dat` | `AccountSessionStore` | DPAPI (current user) |
| Music library | `%LOCALAPPDATA%\AxePower\Music\library.db` | `LibraryDatabase` / `MusicLibraryRepository` | SQLite |
| Music state | `…\Music\playback-state.json`, `recently-played.json`, `recent-searches.json`, `covers\`, `stream-cache\` | `PlaybackStateStore`, `RecentlyPlayedService`, `RecentSearchesService`, `CoverArtService`, `StreamCache` | JSON / files |
| Music provider secrets | `…\Music\secrets.dat` | `DpapiSecretStore` | DPAPI |
| Room chat history and attachments | `%LOCALAPPDATA%\AxePower\Chat\share-chat.json`, `Chat\Attachments\` | `ShareChatStore`, `ShareAttachmentStore` | DPAPI |
| Share Screen runtime | `%LOCALAPPDATA%\AxePower\share\mediamtx.yml` (rewritten per session), `share\tls\room.crt/.key` | `MediaMtxServer`, `TailscaleCertificateProvider` | generated |
| Downloaded tools | `%LOCALAPPDATA%\AxePower\tools\` (MediaMTX, FFmpeg, scrcpy, …) | `ShareToolProvider`, `ToolsService`, `AndroidControlService` | binaries |
| Uninstall history and backups | `%LOCALAPPDATA%\AxePower\UninstallerHistory\` (`sessions.json`, per-session `manifest.json` and `.reg` exports); legacy `%APPDATA%` copy migrated | `UninstallHistoryService`, `BackupManifestStore`, `RegistryBackupService` | JSON, `.reg` |
| User skins | `%APPDATA%\AxePower\Skins\` | `SkinCatalog` | JSON |
| Game cover art | `%APPDATA%\AxePower\CoverArt\` | `GamePosterService` (App) | images |
| LAN Drop received files | `%USERPROFILE%\Downloads\AxePower_LanDrop\` (configurable) | `LanDropService` | user files; staging `.landrop_*.tmp` |
| Cloud data | Supabase: `profiles`, `devices`, friends tables (see `supabase/migrations`) | `SupabaseAccountApi` | RLS policies server-side |

Gaps to know about:
- Settings and uninstall history are plain JSON (no secrets belong in them). `settings.json` is
  written atomically (temp file, then `File.Replace` with a backup); `BackupManifestStore` uses
  an `AtomicFile` helper; other JSON stores write in place.
- Test seams exist for `SettingsService`, `LanDropService` (download folder) and the Music,
  Chat and Account stores (constructor paths). Other stores hard-code their paths.

## 6. Rules for view models

View models present state and turn user intent into calls on Core. The current code breaks
this in specific, countable places. These rules stop it growing; the staged plan removes what
exists.

**A view model must not:**

1. Start processes (`Process.Start`, `ProcessStartInfo`), including "open this URL or folder".
   Use a shell-launcher service.
2. Read or write the registry.
3. Read or write files or directories, other than through a Core service or store.
4. Open sockets, `HttpClient`s or `ClientWebSocket`s, or run PowerShell.
5. Declare P/Invoke or call WMI.
6. Hold cryptographic, parsing or protocol logic. Those are policy types in Core.
7. Construct services with `new`. They are injected, and test fakes go through the same seam.

**A view model may:** hold observable state and commands; marshal to the UI thread; call
injected Core interfaces or services; ask the user through an injected prompt interface (as
`IUninstallerPrompts` and `IGameModePrompts` already do); and format values for display.

**Baseline of current violations** (counted 2026-10-04; the ratchet in stage 1 holds these
numbers and fails on any increase):

| Rule | Files (occurrences) |
|---|---|
| Processes | PhoneHubViewModel (16), BatteryViewModel (6), MainViewModel (5), LanDropViewModel (4), GameCardViewModel (3), SettingsViewModel (2), UninstallerViewModel, ToolsViewModel, DebloatViewModel, AccountViewModel, AboutViewModel (1 each) |
| Registry | PhoneHubViewModel (3) |
| Files | PhoneHubViewModel (8), MusicViewModel (7), LanDropViewModel (5), AboutViewModel (3), UninstallerViewModel, SettingsViewModel, LyricsViewModel, GameCardViewModel (1 each) |
| Sockets / protocol | none (ShareScreenViewModel's joining client moved to Core `ShareRoomSession`, 2026-10-05) |
| PowerShell | TerminalViewModel (10), MainViewModel, DashboardViewModel (1 each) |
| P/Invoke | ToolsViewModel (2) |

**Services follow matching rules:** a new Windows call (P/Invoke, WMI, registry, SCM) goes in
an adapter behind an interface named for what it does (`ITouchpadPlatform`, `IPowerActions`
and `IRoutineWorld` are the existing models), not inline in an orchestration service.

## 7. Dependency direction (target)

```
 App (presentation, UI integration)
   │  depends on
   ▼
 Core feature modules:  Accounts · Friends · Music · LanDrop · ShareScreen · Uninstaller ·
                        SoftwareHub · Power · SystemCare · Routines · Shell
   │  depend on                         ▲
   ▼                                    │ may reference another feature only through an
 Core/Common: IClock, ISecretStore,     │ interface that feature publishes (listed below)
 settings sections, logging, process runner, network utilities, AtomicFile
   │
   ▼
 Core/Platform: Windows adapters (power, display, input, processes, registry, WMI, NLM)
```

Allowed cross-feature edges today, each to be expressed as an interface the provider owns:
- Friends → Accounts (session, `IFriendsApi`)
- Friends → Game Mode (running game for presence, already a delegate)
- LanDrop → Music (`MusicRemote`, as an `IMusicRemote`)
- ShareScreen → network utilities (rate limiter, network classifier, moved to Common)
- ShareScreen settings → Friends (activity sharing)
- Routines and Game Mode → Power, Display, process control (already through `IRoutineActions`
  and friends)

Anything else that appears is a mistake to fix, not a precedent.

## 8. Staged path to feature modules (no big-bang rewrite)

Each stage ships on its own and keeps behaviour identical unless it says otherwise. Verify each
with the full build and test suite, the settings.json snapshot, and `--self-test`. A stage that
touches UI needs the owner's visual check before it is called done.

**Stage 0. This document.** Owners, layers and edges written down (done).

**Stage 1. Guardrails (low risk, tests only).**
Add an architecture-policy test, like `AnalyzerPolicyTests`, that scans `AxePower.App/ViewModels`
for the forbidden APIs in §6 and fails when any file's count rises above the baseline table. It
also checks that Core still references no WPF. The baseline can only go down; lowering it is
part of each later stage.

**Stage 2. Shared abstractions out of feature folders (low risk, moves and namespaces only).**
Move `IClock`, `SystemClock`, `ISecretStore` and `DpapiSecretStore` from Music, and
`AuthenticationRateLimiter` and `NetworkProfileClassifier` from LanDrop, into `Core/Common`
(network utilities into `Core/Common/Network`). Pure moves plus `using` updates; no behaviour
change.

**Stage 3. Group the flat `Core/Services` folder by feature (low risk, file moves).**
Move the 60 root-level files into feature folders: `Power/`, `GameMode/`, `Battery/`,
`Hardware/`, `SystemCare/`, `SoftwareHub/`, `Drivers/`, `Shell/`. Keep namespaces unchanged in
this step, so no consumer changes and the diff is pure renames. Change namespaces later, one
folder at a time.

**Stage 4. Pull infrastructure out of view models (medium risk, one view model per change).**
In order of size:
- ~~`ShareScreenViewModel`'s joining client~~: done (Prompt 18): `ShareRoomSession`,
  `ShareRoomConversation`, `ShareAttachmentAssembler`, `ShareVoiceCoordinator`.
- `PhoneHubViewModel`'s process, registry and file work into `AndroidControlService` and a new
  SpaceDesk adapter.
- `MusicViewModel`'s file I/O into a library service.
- An `IShellLauncher` for every "open URL/folder" call.
- `TerminalViewModel`'s PowerShell into `TerminalService`.
- Delete the unreachable `LanDropViewModel`/`LanDropView` once Phone Hub has everything.

Each change lowers the stage 1 baseline. This overlaps Prompts 18–20; do them together.

**Stage 5. Split the mixed services (medium risk, guided by Prompts 17 and 19).**
- `LanDropService`: done 2026-10-05 (Prompt 17), behind characterization tests
  (`LanDropCharacterizationTests`) and component tests (`LanDropComponentTests`).
- `WinGetHubService`: done 2026-10-05 (Prompt 19): `SoftwareCatalog`, `PackageDiscovery`,
  `InstallPlanner`, the shared `Services/Processes` runner and launcher.
- `BatteryService` → charge policy (pure) plus WMI/registry adapters.

Each keeps its public surface while it is split, so callers do not change in the same commit.

**Stage 6. Settings by section (medium risk, file format unchanged).**
Introduce typed per-feature accessors (for example `ISettingsSection<MusicSettings>`) over the
same `settings.json`, so a feature depends on its section and not on `AppSettings`. Migrate one
feature at a time. No file-format change, so no migration for users.

**Stage 7. Separate privilege (high risk, needs its own design and threat model).**
Run the UI and network listeners as the standard user, and move privileged operations (services,
power plans, registry under HKLM, uninstallers) into an elevated broker, reached over an
authenticated local channel. This removes "every parser runs as admin" (§2.1). Do not start it
before stages 1–6 have shrunk the surface it must carry.

**Stage 8 (optional, decide later). Assemblies per feature.**
Only if build times or enforcement need it: lift the stable features (Uninstaller, Music, Share
Screen) into their own class libraries. Folder boundaries plus the stage 1 test give most of the
benefit without it.

### What not to do
- Rename everything at once, or move namespaces and logic in the same commit.
- Introduce a mediator, CQRS or event bus to "decouple" features; the coupling here is a handful
  of explicit edges (§7), and an indirection layer would hide them.
- Call any of this DDD. If a feature ever grows a real domain model, describe that feature on its
  own terms.
