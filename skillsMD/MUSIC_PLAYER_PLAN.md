# 🎵 AxePower Music — Implementation Plan & Build Spec

> **Target release:** AxePower **v1.9.0** · **Branch:** `feature/music-player` · **Status:** Ready to build
> **Roadmap item:** `idea.txt` → #10 "Compact Music Player & Universal Media HUD"
> **Written:** September 2026

This document is the complete specification for adding a built-in music player to AxePower. It is written so that a developer or an AI coding agent can build the whole feature from this file alone, phase by phase, without any other context.

---

## Table of contents

0. [How to use this document (read first)](#0-how-to-use-this-document-read-first)
1. [Goal and scope](#1-goal-and-scope)
2. [Clean-room rule: Harmonoid is a reference only](#2-clean-room-rule-harmonoid-is-a-reference-only)
3. [Locked decisions](#3-locked-decisions)
4. [Facts about the current AxePower code](#4-facts-about-the-current-axepower-code)
5. [Complete feature list](#5-complete-feature-list)
6. [Architecture](#6-architecture)
7. [Core contracts (C#)](#7-core-contracts-c)
8. [Audio engine design](#8-audio-engine-design)
9. [Queue and playback rules](#9-queue-and-playback-rules)
10. [Music library](#10-music-library)
11. [Playlists, history and stats](#11-playlists-history-and-stats)
12. [Settings schema](#12-settings-schema)
13. [Persisted playback state and secrets](#13-persisted-playback-state-and-secrets)
14. [UI specification](#14-ui-specification)
15. [Windows integration: media controls, media keys, taskbar](#15-windows-integration-media-controls-media-keys-taskbar)
16. [AxePower automations](#16-axepower-automations)
17. [Universal media controller](#17-universal-media-controller)
18. [Phone remote (LAN portal) and security fix](#18-phone-remote-lan-portal-and-security-fix)
19. [Lyrics](#19-lyrics)
20. [Discord Rich Presence and Last.fm](#20-discord-rich-presence-and-lastfm)
21. [Tag editor](#21-tag-editor)
22. [Mix (auto-extending queue)](#22-mix-auto-extending-queue)
23. [Open with AxePower (file arguments)](#23-open-with-axepower-file-arguments)
24. [Spotify Connect remote (optional Phase 7)](#24-spotify-connect-remote-optional-phase-7)
25. [Apple Music: what is and is not possible](#25-apple-music-what-is-and-is-not-possible)
26. [Build phases with checklists](#26-build-phases-with-checklists)
27. [Automated test plan](#27-automated-test-plan)
28. [Manual verification checklist](#28-manual-verification-checklist)
29. [Release and documentation checklist](#29-release-and-documentation-checklist)
30. [Never do / out of scope](#30-never-do--out-of-scope)
31. [Known risks](#31-known-risks)

---

## 0. How to use this document (read first)

**If you are the agent or developer building this, follow these rules:**

1. **Read these files before writing any code.** They are binding project rules and take precedence over your own habits:
   - `.agents/plugins/axepower-wpf/skills/axepower-wpf-engineer/SKILL.md`
   - `.agents/plugins/axepower-wpf/rules/architecture.md`
   - `.agents/plugins/axepower-wpf/rules/ui-design.md`
   - `.agents/plugins/axepower-wpf/rules/system-safety.md`
   - `.agents/plugins/axepower-wpf/rules/testing-and-quality.md`
   - `.agents/plugins/axepower-wpf/rules/release-discipline.md`
2. **Check `git status` first.** The owner may have uncommitted work. Never revert, stash, reformat or commit files you did not change. Stage only your own files.
3. **Create the branch** `feature/music-player` from `main`.
4. **Build in phase order** (see [§26](#26-build-phases-with-checklists)). A phase is finished only when all of these pass:
   ```powershell
   dotnet restore
   dotnet build -c Debug
   dotnet build -c Release
   dotnet test -c Release
   dotnet run --project AxePower.App/AxePower.App.csproj -- --self-test
   ```
   Read the real output. Fix every failure before starting the next phase. Never claim a pass you did not see.
5. **Commit once per phase:** `feat(music): phase N – <summary>`. Add the attribution line your environment requires.
6. **Never `git push` and never open a PR** without the owner's explicit approval.
7. **Never show unfinished features.** A button, tab or setting appears only in the phase that makes it work. Otherwise it must be disabled with a clear reason, or left out.
8. **After every phase, write the completion report** from SKILL.md §J: files inspected, created and modified; build and test commands with their real results; known limitations; manual checks still needed.
9. **Inspect before editing.** Before touching an existing file, read it and confirm the facts in [§4](#4-facts-about-the-current-axepower-code) still hold. If the code has changed in a way that breaks this plan, stop and ask the owner.
10. **Do not open or copy anything from the Harmonoid repository.** Everything you need is in this document. See [§2](#2-clean-room-rule-harmonoid-is-a-reference-only).

---

## 1. Goal and scope

**AxePower Music** is a lightweight local music player built into AxePower. It is reachable from:

| Surface | What it is |
|---|---|
| **Music page** | Sidebar entry holding the library (tracks, albums, artists, genres, folders, playlists), the queue, lyrics and a Now Playing bar |
| **Mini player** | Small, always-on-top floating pill window. `Ctrl+Shift+M` toggles it. It never steals focus |
| **Tray menu** | "Now Playing" section in the Steam-style tray menu, plus the track in the tray tooltip |
| **Dashboard card** | Compact Now Playing card |
| **Windows** | Media flyout and keyboard media keys (SMTC), taskbar preview buttons and progress bar |
| **Phone remote** | Now playing, controls, queue and library browsing on the existing LAN portal (`http://<pc-ip>:5050`) |

It also does things only AxePower can do:
- ducks or pauses music when a game starts
- adapts playback to the power mode
- sleep timer that can put the PC to sleep
- battery rules
- controls other apps' media (Spotify app, browsers, VLC) through the universal media controller.

**Supported formats:** MP3, AAC/M4A, ALAC, FLAC, WAV, AIFF, WMA, Ogg Vorbis, Opus.
**Not supported:** APE, DSD (DSF/DFF), WavPack. Opening one shows "Unsupported format".

---

## 2. Clean-room rule: Harmonoid is a reference only

The feature set is modelled on the open-source player **Harmonoid**, which is licensed under **PolyForm Strict 1.0.0**. That license allows only noncommercial *use*. It forbids distributing the software and "making changes or new works based on the software". AxePower is MIT-licensed and publicly distributed, so:

- ✅ **Allowed:** implementing the *features and behaviours* listed in this document. Ideas and feature lists are not protected. Also allowed: a credit line such as "Feature set inspired by Harmonoid".
- ❌ **Forbidden:** copying or line-by-line translating Harmonoid code (Dart → C#), and reusing its icons, images, fonts, localization strings, name or branding.
- ❌ **Forbidden:** opening Harmonoid source files while implementing. Build only from this document and public API documentation (NAudio, Microsoft Learn, TagLib#, SQLite).

The one design pattern this plan borrows is generic: **one player broadcasts an immutable state snapshot to pluggable integrations.** It is the ordinary observer pattern.

---

## 3. Locked decisions

These defaults are fixed for the build. The owner may change any of them *before* building by editing this table.

| # | Decision | Choice | Why |
|---|---|---|---|
| D1 | Audio engine | **NAudio (`NAudio.Core` + `NAudio.Wasapi`) + Windows Media Foundation**, behind `IAudioEngine` | MIT license, small, no extra native DLLs. Windows 10+ decodes MP3/AAC/ALAC/FLAC/WAV/WMA itself. Supports WASAPI shared and exclusive. We own the sample pipeline, so gapless, crossfade, ReplayGain and a visualizer are straightforward. libmpv can be added later as a second `IAudioEngine` if APE/DSD are ever needed |
| D2 | Windows Runtime access | Change the target framework to **`net8.0-windows10.0.19041.0`** with `SupportedOSPlatformVersion` **`10.0.17763.0`** | Needed for SMTC, the universal media controller and WinRT imaging. Still runs on Windows 10 1809+. Cost: `Microsoft.Windows.SDK.NET.dll` (roughly 20 MB+) ships in the publish output |
| D3 | Sidebar placement | **"Music" button in the existing CONTROL group**, after "Battery" | The sidebar deliberately has no ScrollViewer, and `MainWindow` `MinHeight="760"` is sized to fit it. A new group header would cost more height than a single button |
| D4 | Streaming services | **Universal media controller** (Phase 3) for all apps. **Spotify Connect remote** as optional Phase 7 using the user's own Spotify Client ID. **No Apple Music login** | Playing Spotify/Apple audio inside a third-party Windows app is not possible (DRM, no SDK). See [§24](#24-spotify-connect-remote-optional-phase-7) and [§25](#25-apple-music-what-is-and-is-not-possible) |
| D5 | Library storage | **SQLite** (`Microsoft.Data.Sqlite`) at `%LOCALAPPDATA%\AxePower\Music\library.db` | Scales to large libraries and supports full-text search. `settings.json` stays small |
| D6 | Online features | **All off by default**, with explicit opt-in labelled with what data leaves the PC | The README promises "zero telemetry" and "privacy-first" |
| D7 | Secrets (Last.fm session, Spotify refresh token) | **DPAPI-encrypted file** (`ProtectedData`, CurrentUser scope), never `settings.json` | `SettingsService` writes plain JSON. The README's "Encrypted JSON" claim is not true of the current code |

---

## 4. Facts about the current AxePower code

The build depends on these facts as they stood in September 2026. Verify each one before editing the file concerned.

### Solution and build
- **Projects:** `AxePower.App` (WPF, `AssemblyName` = `AxePower`), `AxePower.Core` (class library), `AxePower.Tests` (xUnit 2.9 + Moq 4.20, references both).
- **All three `.csproj` files target `net8.0-windows`.** All three must change together (D2). Otherwise the test project cannot reference the others (NuGet error NU1201).
- **Version is `1.8.0`** in both `AxePower.App.csproj` and `AxePower.Core.csproj` (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`).
- **The installer scripts hard-code the publish folder** `..\AxePower.App\bin\Release\net8.0-windows\win-x64\publish\`:
  - `Installer/AxePower.iss` (lines ~47–48)
  - `Installer/AxePowerSetup.iss` (lines ~16–17)
- **The D2 framework change moves that folder to `...\net8.0-windows10.0.19041.0\win-x64\publish\`.** Search the whole repo for `net8.0-windows\` and update every hit: both `.iss` files, `README.md`, `RELEASE_README.md`, and `.agents/.../scripts/verify-axepower.ps1`.

### App startup (`AxePower.App/App.xaml.cs`)
- `ConfigureServices()` registers Core services as **singletons** and ViewModels as **transient**. `MainWindow` is a singleton.
- **Startup order:** `LoadSettings` → elevation relaunch check → `ThemeService.ApplyTheme` → `StartupService` → `BatteryService.StartHistoryCollection` → `AutoSwitchService.Start` → `AppMonitorService.Start` → show `MainWindow` → `ListenForOtherInstances`.
- **Single instance:** a named pipe `AxePower.Desktop.Singleton.Pipe`. The second instance writes **one byte**, and the first instance only shows its window. File paths are **not** forwarded today.
- `Application_Exit` stops `AutoSwitchService` and `AppMonitorService`.
- `--self-test` resolves services and `MainViewModel`/`MainWindow`. **Extend it to resolve the music services.**
- The app runs **elevated by default** (`RunAsAdministrator = true`, `asInvoker` manifest plus a relaunch). **Consequence:** Windows (UIPI) blocks OLE drag-and-drop from Explorer into AxePower's window. See [§14.2](#142-music-page).

### Settings (`AxePower.Core/Services/SettingsService.cs`, `Models/AppSettings.cs`)
- **Folder:** `%LOCALAPPDATA%\AxePower\`. Files: `settings.json`, `settings.backup.json`, `settings.temp.json`.
- **Atomic write:** write temp → `File.Replace(temp, settings, backup)`. **Reuse this exact pattern** for every file the music feature writes.
- `GetSettings()` returns `_currentSettings.Clone()`. `SaveSettings(s)` saves and raises `OnSettingsChanged` (which every listener receives).
- **`AppSettings.Clone()` is written by hand.** A new property must be added to `Clone()` too, or its value is silently lost.
- **Plain JSON** via `System.Text.Json` with `JsonStringEnumConverter` and `[JsonPropertyName("camelCase")]`. It is not encrypted.
- Existing settings the music feature reuses: `ReduceMotion`, `LowBatteryThresholdPercent`, `ShowNotifications`, `AppRulesEnabled`, `Theme`.

### Other services the music feature uses
- **`LogService`**: `LogInfo(string)`, `LogWarning(string)`, `LogError(string, Exception?)`. The file is `%APPDATA%\AxePower\AxePower.log`.
- **`NotificationService`**: `Show(string message)` → in-app notification through `MainViewModel.ShowNotification`.
- **`PowerMode` enum:** `Eco`, `Smart`, `Beast`, `GameMode`.
- **`PowerPlanService.ApplyModeAsync(PowerMode)`** returns `bool` after verifying the active scheme. **There is no event for a completed mode change.** Add one ([§16.2](#162-power-mode-playback-profiles)). Every caller (Dashboard, tray, AutoSwitch, AppMonitor) goes through this method.
- **`AppMonitorService`**: WMI `Win32_ProcessStartTrace`/`StopTrace` watchers. They run **only when `AppRulesEnabled`** and may need admin. On a rule match it pauses AutoSwitch and applies the rule's mode. **There are no public events today.** Add them ([§16.1](#161-game-mode-auto-duck--pause)).
- **`LanDropService`**:
  - raw `TcpListener` on **`IPAddress.Any`, port 5050**, hand-parsed HTTP
  - routes `GET /`, `/api/status`, `/api/download?id=`, `/qr`; `POST /api/upload`, `/api/text`, `/api/remote`
  - `/api/remote` actions: `play-pause`, `next`, `prev`, `vol-up`, `vol-down`, `mute`, **`lock`** (simulated keys or `LockWorkStation`)
  - **no authentication at all**, and responses send `Access-Control-Allow-Origin: *`
  - portal HTML is an embedded string (`GetEmbeddedWebPortalHtml()`), and `ServerUrl` feeds the QR code. See [§18](#18-phone-remote-lan-portal-and-security-fix) for the security fix.

### UI
- **`MainViewModel.Navigate(string)`**: a `switch` on the base key picks the view model. Deep links use `Key:arg1:arg2`. Each page has a `XxxActiveStyle` property that is refreshed in `OnCurrentViewChanged`.
- **`MainWindow.xaml`**:
  - `DataTemplate DataType=` view-model → view mappings near line 25
  - sidebar `StackPanel` near line 110; group header style `SidebarGroupHeaderTextStyle`; buttons use `Style="{Binding XxxActiveStyle}"` with `NavigateCommand`
  - window `MinWidth="1024" MinHeight="760"`
  - tray `tb:TaskbarIcon` with a custom `ContextMenu` near line 1458; menu items use `Tag` + `OpenAppAndNavigate_Click`, or `Command` bindings
- **Theme brushes** (use `DynamicResource`, never hard-coded colours): `WindowBackgroundBrush`, `WindowSecondaryBackgroundBrush`, `SidebarBackgroundBrush`, `CardBackgroundBrush`, `ElevatedCardBackgroundBrush`, `CardBorderBrush`, `CardHoverBrush`, `HoverBackgroundBrush`, `DividerBrush`, `PrimaryTextBrush`, `SecondaryTextBrush`, `MutedTextBrush`, `DisabledTextBrush`, `AccentBrush`, `AccentHoverBrush`, `AccentPressedBrush`, `FocusBrush`, `SuccessBrush`, `WarningBrush`, `ErrorBrush`, `EcoBrush`, `SmartBrush`, `BeastBrush`, `TrayMenu*Brush`.
  - Theme dictionaries: `Resources/Themes/DarkTheme.xaml`, `LightTheme.xaml`, `GlassTheme.xaml`. **Every new brush must be added to all three.**
- **Styles** in `Resources/Styles.xaml`: `CardStyle`, `CardElevatedStyle`, `PrimaryButtonStyle`, `SecondaryButtonStyle`, `ToggleSwitchStyle`, `AppComboBoxStyle`, `OverlayScrollViewerStyle`, `CompactScrollBarStyle`, `PageTitleText`, `SectionTitleText`, `SecondaryText`, `MutedText`, `HeroValueText`, `PrimaryFontFamily`, sidebar styles.
- **Credits:** `Views/AboutView.xaml`, "Third-Party Software & Open Source Credits" (near line 359), and `README.md` → "Third-Party Credits & Acknowledgements".
- **Tests:** `AxePower.Tests/` (xUnit, `WpfApplicationFixture` for WPF), `Uninstaller/DependencyInjectionTests.cs`, `LanDropServiceTests.cs`, `AppSettingsTests.cs`.

---

## 5. Complete feature list

Legend: **P#** = the build phase that delivers it (see [§26](#26-build-phases-with-checklists)).

### 5.1 Playback
| Feature | Details | P |
|---|---|---|
| Play / pause / stop / next / previous / seek | "Previous" restarts the track if more than 3 s in (see [§9](#9-queue-and-playback-rules)) | 1 |
| Queue | Open, play next, add to queue, insert, move (drag), remove, clear, jump, save as playlist | 1 |
| Shuffle | Restores the original order when turned off | 1 |
| Repeat | Off / All / One | 1 |
| Gapless playback | Sample-accurate joins between consecutive tracks | 1 |
| Volume 0–100% + mute | Software volume that never touches the Windows master volume | 1 |
| Volume boost to 200% | Opt-in, with a soft limiter against clipping | 1 |
| Resume last session | Queue, track, position, shuffle, repeat, volume. Restores **paused** by default | 1 |
| Open files / folders | Picker; drag-and-drop that also works when elevated | 1 |
| Output device selection | "Windows default (follows changes)" or a specific device. Pauses when the device is unplugged | 4 |
| WASAPI exclusive mode | Opens at the source sample rate for bit-perfect output; falls back to shared and shows why | 5 |
| ReplayGain | Off / Track / Album / Auto, preamp −15…+15 dB, clipping prevention, fallback gain | 5 |
| Crossfade | 0–12 s, equal-power; optionally skipped between tracks of the same album | 5 |
| Speed and pitch | Speed 0.5×–2.0× (pitch preserved); pitch −12…+12 semitones | 5 |
| Audio quality badge | e.g. `FLAC · 24-bit/96 kHz · Exclusive · Bit-perfect`, built only from real engine state | 5 |
| Spectrum visualizer | 32 bars; off in Eco mode, with Reduce Motion, or when not visible | 5 |

### 5.2 Library
| Feature | Details | P |
|---|---|---|
| Library folders | Add or remove folders. First run offers the user's Music folder | 2 |
| Scanner | Incremental (size and modification time), cancellable, with progress. Runs in the background at low priority | 2 |
| Folder watching | `FileSystemWatcher` with debounce; buffer overflow → rescan that folder | 2 |
| Browse | Tracks, Albums (grid), Artists, Genres, Folders (tree), Playlists | 2 |
| Sorting | Tracks: title/artist/album/year/date added/duration. Albums: title/artist/year | 2 |
| Search | Full-text (SQLite FTS5) prefix search across title, artists, album and genre | 2 |
| Multiple artists and genres | Split on `;`, `/`, `, `, ` feat. `, ` ft. `, ` & ` (artists) and `;`, `/`, `,` (genres) | 2 |
| Album artist grouping | An album is keyed by (album title, album artist or else first artist) | 2 |
| Cover art | Embedded → `cover.jpg/png`, `folder.jpg/png`, `front.jpg/png`, `album.jpg/png` in the track's folder → placeholder. Cached at 128/256/512 px | 2 |
| Hide from library | Excludes a track. **Never deletes the file** | 2 |
| Show in Explorer | `explorer.exe /select,"<path>"` (argument-quoted, no shell string built from user input) | 2 |

### 5.3 Playlists and history
| Feature | Details | P |
|---|---|---|
| Playlists | Create, rename, delete, add, remove, reorder | 2 |
| M3U/M3U8 | Import and export (relative or absolute paths, UTF-8) | 2 |
| Liked songs | Heart toggle, system playlist | 2 |
| Recently played | System playlist, last 1,000 plays | 2 |
| Play count, last played, most played | Stored per track | 6 |

### 5.4 AxePower surfaces
| Feature | Details | P |
|---|---|---|
| Mini player | Floating pill with compact/expanded modes, corner snap, per-monitor position, opacity, optional click-through | 3 |
| Global hotkey | `Ctrl+Shift+M` toggles the mini player, configurable. Reports conflicts honestly | 3 |
| Tray Now Playing | Title/artist header, Play/Pause, Next, Previous, Mini player; track in the tooltip | 3 |
| Dashboard card | Compact now playing or an "Open Music" call to action | 3 |
| Accent from album art | Dominant colour tints the Music page and mini player only. Contrast-checked | 3 |
| Universal media controller | Shows and controls Spotify, browsers, VLC and others through Windows sessions | 3 |
| Phone remote | Now playing, cover, controls, seek, volume, queue, library search, playlists | 3 |
| LAN portal access token | Security fix covering all portal API routes | 3 |

### 5.5 AxePower automations
| Feature | Details | P |
|---|---|---|
| Game Mode duck / pause | Triggered by app rules or fullscreen detection. Restores afterwards | 4 |
| Power-mode playback profiles | Eco = larger buffer, fewer UI updates, visuals off | 4 |
| Sleep timer | Minutes / end of track / end of queue, fade-out, then Stop / Sleep PC / Hibernate PC | 4 |
| Low-battery rule | Optionally pause, or force the Eco profile, below the threshold | 4 |
| Device-removed pause | Pauses when headphones or USB DAC disappear | 4 |

### 5.6 Extras
| Feature | Details | P |
|---|---|---|
| Lyrics | `.lrc` sidecar → embedded synced → embedded unsynced → LRCLIB online (opt-in). Synced highlight, offset adjust | 6 |
| Tag editor | Single and batch; cover replace/remove; lyrics. Backup and undo | 6 |
| Discord Rich Presence | Opt-in | 6 |
| Last.fm scrobbling | Opt-in, with an offline scrobble queue | 6 |
| Mix | Auto-extends the queue from the local library | 6 |
| "Open with AxePower" | File arguments forwarded to the running instance; opt-in "Open with" registration (HKCU, removable) | 6 |
| Track-change notification | Opt-in | 6 |

### 5.7 Optional
| Feature | Details | P |
|---|---|---|
| Spotify Connect remote | Log in with your own Spotify Client ID; browse playlists; control and transfer playback between Spotify devices | 7 |

---

## 6. Architecture

### 6.1 New files

```
AxePower.Core/
  Models/Music/
    Track.cs                 Track, AudioFormatInfo, ReplayGainInfo
    QueueItem.cs
    PlayerState.cs           PlayerState, OutputInfo, MusicSource
    MusicEnums.cs            RepeatMode, ReplayGainMode, GameDuckMode, SleepTimerAfterAction,
                             MiniPlayerCorner, TrackSortField, AlbumSortField
    MusicSettings.cs         (+ nested MiniPlayerSettings, AutomationSettings, IntegrationSettings)
    LibraryModels.cs         AlbumSummary, ArtistSummary, GenreSummary, FolderNode, TrackQuery,
                             AlbumQuery, LibraryScanProgress, LibraryScanResult
    Playlist.cs              Playlist, PlaylistItem, PlaylistKind
    LyricsModels.cs          Lyrics, LyricLine, LyricsSource
    ExternalMediaSession.cs  (universal controller snapshot)
    PersistedPlaybackState.cs
  Native/
    MusicInterop.cs          SHQueryUserNotificationState, SetSuspendState, IsPwrHibernateAllowed
  Services/Music/
    Engine/
      IAudioEngine.cs, NAudioEngine.cs
      DecoderFactory.cs      extension/header sniffing → ISampleProvider source
      Decoders/OpusDecoderSource.cs, VorbisDecoderSource.cs
      Pipeline/GaplessSourceChain.cs, CrossfadeMixer.cs, ReplayGainStage.cs,
               VolumeStage.cs, SoftLimiter.cs, TempoPitchStage.cs, SpectrumTap.cs,
               FormatAdapter.cs (resample + channel map), PcmOutputConverter.cs (float → 16/24/32-bit int)
      OutputFactory.cs       WASAPI shared/exclusive negotiation
    IMusicPlayerService.cs, MusicPlayerService.cs
    PlaybackQueue.cs         pure queue logic (unit-testable, no audio)
    PlaybackStateStore.cs    atomic JSON persistence
    Integrations/
      IPlayerIntegration.cs, PlayerIntegrationHost.cs
      SmtcIntegration.cs
      HistoryIntegration.cs
      DiscordRpcIntegration.cs      (P6)
      LastFmScrobbler.cs, LastFmClient.cs   (P6)
    Library/
      IMusicLibraryService.cs, MusicLibraryService.cs
      MusicLibraryRepository.cs     SQLite access + migrations
      LibraryScanner.cs, LibraryWatcher.cs
      ITagReader.cs, TagLibTagReader.cs
      ArtistGenreSplitter.cs
      ICoverArtService.cs, CoverArtService.cs  (WinRT imaging for resize + dominant colour)
    Playlists/IPlaylistService.cs, PlaylistService.cs, M3uSerializer.cs
    Lyrics/ILyricsService.cs, LyricsService.cs, LrcParser.cs, LrcLibClient.cs  (P6)
    Devices/IAudioDeviceService.cs, AudioDeviceService.cs
    External/IUniversalMediaService.cs, UniversalMediaService.cs
    Automation/
      IMusicAutomationService.cs, MusicAutomationService.cs
      ISleepTimerService.cs, SleepTimerService.cs
      IFullscreenDetector.cs, FullscreenDetector.cs
      ISystemPowerActions.cs, SystemPowerActions.cs
    Remote/MusicRemoteApi.cs  (JSON handlers used by LanDropService)
    Mix/MixGenerator.cs (P6)
    Secrets/ISecretStore.cs, DpapiSecretStore.cs
    Streaming/Spotify/ (P7) SpotifyAuthService.cs, SpotifyApiClient.cs, SpotifyConnectService.cs
    Common/IClock.cs, SystemClock.cs

AxePower.App/
  ViewModels/Music/
    MusicViewModel.cs          page: tabs, library lists, search, scan status
    NowPlayingViewModel.cs     SINGLETON, shared by page bar, mini player, tray, dashboard
    QueueViewModel.cs, LyricsViewModel.cs, TagEditorViewModel.cs (P6)
    MusicSettingsViewModel.cs  exposed from SettingsViewModel as a child VM
    SpotifyViewModel.cs (P7)
  Views/Music/
    MusicView.xaml             page shell
    NowPlayingBar.xaml, QueuePanel.xaml, LyricsPanel.xaml,
    TracksList.xaml, AlbumGrid.xaml, ArtistList.xaml, GenreList.xaml, FolderTree.xaml, PlaylistsView.xaml,
    TagEditorDialog.xaml (P6), SpotifyPanel.xaml (P7)
    MusicSettingsSection.xaml  hosted inside SettingsView
    NowPlayingCard.xaml        hosted inside DashboardView
  Windows/MiniPlayerWindow.xaml(.cs)
  Services/
    HotkeyService.cs           RegisterHotKey on the main window HWND
    MiniPlayerService.cs       create/show/hide/position the mini player window
    TaskbarMediaIntegration.cs IPlayerIntegration using WPF TaskbarItemInfo
    FileDropHelper.cs          WM_DROPFILES + ChangeWindowMessageFilterEx (works when elevated)
    MusicDispatcher.cs         marshals/throttles engine events onto the UI thread
  Converters/TimeSpanToClockConverter.cs, BoolToGlyphConverter.cs (as needed)

AxePower.Tests/Music/…  (see §27), plus TestAudio.cs (generates WAV fixtures in code)

THIRD_PARTY_NOTICES.md  (repo root; installed with the app)
```

### 6.2 NuGet packages

Pin exact versions in the `.csproj`: use the latest stable at build time, not below the stated minimum. Record each one in `THIRD_PARTY_NOTICES.md`.

| Package | Project | License | Phase | Purpose |
|---|---|---|---|---|
| `NAudio.Core` (≥ 2.2.1) | Core | MIT | 1 | Sample providers, WAV/AIFF readers, resampler, FFT |
| `NAudio.Wasapi` (≥ 2.2.1) | Core | MIT | 1 | WASAPI output, Media Foundation reader, MMDevice API |
| `NAudio.Vorbis` | Core | MIT | 1 | Ogg Vorbis decoding |
| `Concentus` + `Concentus.Oggfile` | Core | BSD-3 | 1 | Opus decoding |
| `TagLibSharp` (≥ 2.3.0) | Core | LGPL-2.1 (separate DLL) | 1 | Tags, covers, ReplayGain, lyrics, tag writing |
| `Microsoft.Data.Sqlite` (8.0.x) | Core | MIT | 2 | Library database (bundled SQLite includes FTS5) |
| `System.Security.Cryptography.ProtectedData` (8.0.x) | Core | MIT | 6 | DPAPI secret storage |
| `SoundTouch.Net` | Core | LGPL-2.1 (separate DLL) | 5 | Tempo and pitch |
| `DiscordRichPresence` | Core | MIT | 6 | Discord RPC |
| `VirtualizingWrapPanel` | App | MIT | 2 | Virtualized album grid |

Reference `NAudio.Core` and `NAudio.Wasapi` only, not the `NAudio` meta-package. That avoids pulling WinForms/WinMM/ASIO assemblies.

LGPL components stay separate, dynamically loaded DLLs. Their license texts go into `THIRD_PARTY_NOTICES.md`, which the installer copies to `{app}`.

### 6.3 Target framework change (D2)
In **all three** `.csproj` files:
```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
```
Then update every hard-coded `net8.0-windows\` publish path (see [§4](#4-facts-about-the-current-axepower-code)). Verify:
- `dotnet publish AxePower.App/AxePower.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false` succeeds
- Inno Setup compiles with the new path.

### 6.4 Dependency injection (add to `App.ConfigureServices`)
```csharp
// Music — Core (all singletons)
services.AddSingleton<IClock, SystemClock>();
services.AddSingleton<ISecretStore, DpapiSecretStore>();
services.AddSingleton<ITagReader, TagLibTagReader>();
services.AddSingleton<MusicLibraryRepository>();
services.AddSingleton<ICoverArtService, CoverArtService>();
services.AddSingleton<IMusicLibraryService, MusicLibraryService>();
services.AddSingleton<IPlaylistService, PlaylistService>();
services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
services.AddSingleton<IAudioEngine, NAudioEngine>();
services.AddSingleton<PlaybackStateStore>();
services.AddSingleton<IMusicPlayerService, MusicPlayerService>();
services.AddSingleton<IUniversalMediaService, UniversalMediaService>();
services.AddSingleton<IFullscreenDetector, FullscreenDetector>();
services.AddSingleton<ISystemPowerActions, SystemPowerActions>();
services.AddSingleton<ISleepTimerService, SleepTimerService>();
services.AddSingleton<IMusicAutomationService, MusicAutomationService>();
services.AddSingleton<ILyricsService, LyricsService>();                  // P6
services.AddSingleton<IPlayerIntegration, SmtcIntegration>();            // multiple registrations
services.AddSingleton<IPlayerIntegration, HistoryIntegration>();
services.AddSingleton<IPlayerIntegration, DiscordRpcIntegration>();      // P6
services.AddSingleton<IPlayerIntegration, LastFmScrobbler>();            // P6
services.AddSingleton<PlayerIntegrationHost>();
// Music — App
services.AddSingleton<MusicDispatcher>();
services.AddSingleton<IPlayerIntegration, TaskbarMediaIntegration>();
services.AddSingleton<HotkeyService>();
services.AddSingleton<MiniPlayerService>();
services.AddSingleton<NowPlayingViewModel>();   // singleton on purpose
services.AddTransient<MusicViewModel>();
services.AddTransient<MusicSettingsViewModel>();
```

### 6.5 Startup and shutdown
- **After `MainWindow` is created** (it can be hidden when `StartMinimised`), get its HWND with `new WindowInteropHelper(mainWindow).EnsureHandle()`. Then run, **without blocking the UI thread**:
  1. `PlayerIntegrationHost.InitializeAsync(hwnd)` (SMTC needs the HWND)
  2. `IMusicPlayerService.InitializeAsync()`, which restores the persisted state **paused**, unless `ResumePlaybackOnStartup` is on
  3. `IMusicAutomationService.Start()`
  4. `HotkeyService.Register(...)`
  5. `IMusicLibraryService.InitializeAsync()` → if `ScanOnStartup`, start an incremental scan **10 seconds after startup** at low priority.
- **`Application_Exit`:** `await IMusicPlayerService.ShutdownAsync()` (persist state, release the device), dispose the integrations (SMTC, Discord), flush the Last.fm queue, and dispose `LibraryWatcher`.
- **`RunSelfTest`:** resolve `IMusicPlayerService`, `IMusicLibraryService` and `NowPlayingViewModel`, and print one line each.

### 6.6 Threading rules
- **Decoders** (Media Foundation, Vorbis, Opus) are created and read **only on background (MTA) threads**, never on the WPF UI thread (Media Foundation COM objects break across apartments). The engine owns a dedicated worker for load/seek work; WASAPI owns its render thread.
- **Engine events** fire on background threads. `MusicDispatcher` (App layer) marshals them to the UI thread with `Dispatcher.BeginInvoke` and **throttles position updates** to 4 Hz (1 Hz in the Eco profile).
- **Core services never reference WPF types.**
- **Every async method takes a `CancellationToken`.** A new load or seek cancels the stale one.
- **No tight polling.** The only periodic checks allowed:
  - position ticks while playing
  - fullscreen detection every 5 s, only while playing with game-duck on
  - Spotify state polling (P7), only while the Spotify panel is visible or Spotify is the active source.

### 6.7 Storage layout
```
%LOCALAPPDATA%\AxePower\
  settings.json                 (existing; gains "music": { … })
  Music\
    library.db (+ -wal, -shm)   SQLite library
    playback-state.json         queue/position/volume/etc. (atomic writes)
    covers\{coverKey}_{size}.jpg
    tag-backups\                last 20 tag edits (P6)
    scrobble-queue.json         (P6)
    secrets.bin                 DPAPI-protected (P6/P7)
```

---

## 7. Core contracts (C#)

The signatures below are the intended public surface. Names may be adjusted, but the behaviour may not.

```csharp
namespace AxePower.Core.Models.Music;

public enum RepeatMode { Off, All, One }
public enum ReplayGainMode { Off, Track, Album, Auto }          // Auto = Album when playing an album/playlist in album order, else Track
public enum GameDuckMode { Off, Duck, Pause }
public enum SleepTimerAfterAction { Stop, SleepPc, HibernatePc }
public enum MusicSource { None, AxePower, External, Spotify }

public sealed record AudioFormatInfo(string Codec, int SampleRate, int BitsPerSample, int Channels, int BitrateKbps, bool IsLossless);
public sealed record ReplayGainInfo(double? TrackGainDb, double? TrackPeak, double? AlbumGainDb, double? AlbumPeak);

public sealed record Track(
    long Id,                                // 0 = opened directly, not in library
    string Path,
    string Title,                           // falls back to file name without extension
    IReadOnlyList<string> Artists,
    string? Album,
    string? AlbumArtist,
    int? TrackNumber,
    int? DiscNumber,
    int? Year,
    IReadOnlyList<string> Genres,
    TimeSpan Duration,
    AudioFormatInfo Format,
    ReplayGainInfo? ReplayGain,
    string? CoverKey,
    bool Liked);

public sealed record QueueItem(Guid Key, Track Track);  // Key allows the same track twice

public sealed record OutputInfo(
    string DeviceName, bool Exclusive, int SampleRate, int BitsPerSample, int Channels,
    bool BitPerfect,                        // true ONLY when: exclusive, native rate, no active DSP, output bits >= source bits
    string? FallbackReason);                // e.g. "Exclusive mode unavailable: device in use"

public sealed record PlayerState(
    IReadOnlyList<QueueItem> Queue,
    int Index,                              // -1 when queue empty
    bool Playing,
    bool Buffering,
    bool Completed,
    TimeSpan Position,
    TimeSpan Duration,
    double Volume,                          // 0.0–2.0 (1.0 = 100%)
    bool Muted,
    bool Shuffle,
    RepeatMode Repeat,
    double Speed,                           // 0.5–2.0
    double PitchSemitones,                  // -12..+12
    int? MixOffset,
    OutputInfo? Output,
    string? Error)
{
    public QueueItem? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;
    public static PlayerState Empty { get; } = new(Array.Empty<QueueItem>(), -1, false, false, false,
        TimeSpan.Zero, TimeSpan.Zero, 1.0, false, false, RepeatMode.Off, 1.0, 0, null, null, null);
}
```

```csharp
namespace AxePower.Core.Services.Music;

public sealed record DspSettings(
    ReplayGainMode ReplayGain, double PreampDb, bool PreventClipping, double FallbackGainDb,
    TimeSpan Crossfade, bool CrossfadeSkipSameAlbum,
    double Speed, double PitchSemitones, bool AllowBoost);

public sealed record OutputSettings(string? DeviceId, bool Exclusive, bool MatchSourceRate, int SharedLatencyMs);

public interface IAudioEngine : IAsyncDisposable
{
    event EventHandler<Guid>? TrackStarted;          // QueueItem.Key whose audio just began (incl. gapless/crossfade boundaries)
    event EventHandler<Guid>? TrackEnded;            // natural end, no next item preloaded
    event EventHandler<EngineError>? Error;          // (QueueItem.Key, message, exception)
    event EventHandler<OutputInfo>? OutputChanged;
    event EventHandler? DeviceLost;

    Task LoadAsync(QueueItem item, TimeSpan startAt, bool play, CancellationToken ct);
    void SetNext(QueueItem? next);                   // what to preload for gapless/crossfade; null = none
    void Play();
    void Pause();
    Task StopAsync();
    Task SeekAsync(TimeSpan position, CancellationToken ct);
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }
    void SetVolume(double volume, bool muted);        // 0.0–2.0; >1.0 only honoured when AllowBoost
    void SetDsp(DspSettings dsp);                     // live; stages turn to pass-through when neutral
    Task ConfigureOutputAsync(OutputSettings output, CancellationToken ct); // applied at next safe point (see §8.6)
    float[]? GetSpectrum();                           // latest 32-bar snapshot or null when disabled
    bool SpectrumEnabled { get; set; }
}

public interface IMusicPlayerService
{
    PlayerState State { get; }
    event EventHandler<PlayerState>? StateChanged;    // any change except plain position ticks
    event EventHandler<TimeSpan>? PositionChanged;    // throttled by the caller's profile (4 Hz / 1 Hz)

    Task InitializeAsync(CancellationToken ct);
    Task OpenAsync(IReadOnlyList<Track> tracks, int startIndex = 0, bool play = true, bool shuffle = false, CancellationToken ct = default);
    Task OpenPathsAsync(IEnumerable<string> filesOrFolders, bool play = true, CancellationToken ct = default);
    Task PlayAsync();
    Task PauseAsync();
    Task TogglePlayPauseAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task JumpAsync(int index);
    Task SeekAsync(TimeSpan position);
    void SetVolume(double volume);
    void ToggleMute();
    void SetShuffle(bool on);
    void SetRepeat(RepeatMode mode);
    void SetSpeed(double speed);
    void SetPitch(double semitones);
    void PlayNext(IEnumerable<Track> tracks);
    void AddToQueue(IEnumerable<Track> tracks);
    void Insert(int index, Track track);
    void Move(int from, int to);
    void Remove(int index);
    void ClearQueue();
    void SetMix(bool on);                             // P6
    Task FadeOutAndPauseAsync(TimeSpan duration);     // used by sleep timer
    Task DuckAsync(double level, TimeSpan fade);      // used by automations; level 0–1 of current volume
    Task UnduckAsync(TimeSpan fade);
    Task ShutdownAsync();
}

public interface IPlayerIntegration : IDisposable
{
    string Name { get; }
    Task InitializeAsync(IntPtr mainWindowHandle, CancellationToken ct);
    void OnStateChanged(PlayerState state);          // must return quickly; offload slow work
    void OnPositionChanged(TimeSpan position) { }
    void OnSettingsChanged(MusicSettings settings) { } // enable/disable itself
}

public interface IMusicLibraryService
{
    event EventHandler<LibraryScanProgress>? ScanProgress;
    event EventHandler? LibraryChanged;
    bool IsScanning { get; }
    Task InitializeAsync(CancellationToken ct);
    Task<LibraryScanResult> ScanAsync(bool full, CancellationToken ct);
    Task AddFolderAsync(string path, CancellationToken ct);
    Task RemoveFolderAsync(string path, CancellationToken ct);         // removes rows, never files
    Task<IReadOnlyList<Track>> GetTracksAsync(TrackQuery query, CancellationToken ct);
    Task<IReadOnlyList<AlbumSummary>> GetAlbumsAsync(AlbumQuery query, CancellationToken ct);
    Task<IReadOnlyList<ArtistSummary>> GetArtistsAsync(CancellationToken ct);
    Task<IReadOnlyList<GenreSummary>> GetGenresAsync(CancellationToken ct);
    Task<IReadOnlyList<Track>> SearchAsync(string text, int limit, CancellationToken ct);
    Task<Track?> GetTrackAsync(long id, CancellationToken ct);
    Task<Track?> GetTrackByPathAsync(string path, CancellationToken ct);
    Task<Track> ReadLooseFileAsync(string path, CancellationToken ct);   // not stored in DB
    Task SetLikedAsync(long trackId, bool liked, CancellationToken ct);
    Task HideTrackAsync(long trackId, CancellationToken ct);
    Task RecordPlayAsync(long trackId, DateTime utc, CancellationToken ct);
}

public interface ITagReader
{
    TrackTags Read(string path);                      // throws TagReadException on unreadable file
    byte[]? ReadEmbeddedCover(string path);
    string? ReadEmbeddedLyrics(string path);          // SYLT → LRC text, else USLT/LYRICS
    void Write(string path, TagEdit edit);            // P6
}

public interface ICoverArtService
{
    Task<string?> GetCoverFileAsync(Track track, int size, CancellationToken ct); // cached JPEG path
    Task<(byte R, byte G, byte B)?> GetDominantColorAsync(Track track, CancellationToken ct);
}

public interface IAudioDeviceService : IDisposable
{
    IReadOnlyList<(string Id, string Name, bool IsDefault)> GetRenderDevices();
    event EventHandler? DefaultDeviceChanged;
    event EventHandler<string>? DeviceRemoved;       // device id
    event EventHandler? DevicesChanged;
}

public interface IUniversalMediaService : IDisposable
{
    Task InitializeAsync(CancellationToken ct);
    ExternalMediaSession? Current { get; }            // best external session (never AxePower itself)
    event EventHandler<ExternalMediaSession?>? CurrentChanged;
    Task<bool> TogglePlayPauseAsync();
    Task<bool> NextAsync();
    Task<bool> PreviousAsync();
    Task<bool> SeekAsync(TimeSpan position);
    Task<bool> PauseAsync();
}

public interface IMusicAutomationService : IDisposable { void Start(); void Stop(); }

public interface ISleepTimerService
{
    DateTime? EndsAtUtc { get; }
    SleepTimerAfterAction AfterAction { get; }
    event EventHandler? Changed;
    void StartMinutes(int minutes, SleepTimerAfterAction after);
    void StartEndOfTrack(SleepTimerAfterAction after);
    void StartEndOfQueue(SleepTimerAfterAction after);
    void Cancel();
}

public interface IFullscreenDetector { bool IsFullscreenAppActive(); }   // SHQueryUserNotificationState
public interface ISystemPowerActions { bool CanHibernate { get; } bool Sleep(); bool Hibernate(); }
public interface ISecretStore { void Set(string key, string? value); string? Get(string key); }
public interface IClock { DateTime UtcNow { get; } }
```

---

## 8. Audio engine design

### 8.1 Decoders (`DecoderFactory`)
| Extension | Reader |
|---|---|
| `.wav` | `WaveFileReader` |
| `.aif`, `.aiff` | `AiffFileReader` |
| `.mp3`, `.m4a`, `.mp4`, `.aac`, `.flac`, `.wma` | `MediaFoundationReader` with `MediaFoundationReaderSettings { RequestFloatOutput = true }` |
| `.ogg`, `.oga` | Sniff the first page: contains `OpusHead` → Opus (Concentus), else `VorbisWaveReader` |
| `.opus` | Concentus `OpusOggReadStream` wrapped as an `ISampleProvider` |
| anything else | `UnsupportedFormatException` → user message "Unsupported format (.ape)" |

- Call `MediaFoundationApi.Startup()` once, on a background thread, before the first MF use.
- **Duration:** decoder `TotalTime`, falling back to the tag duration.
- **Seeking:** every reader above must support seeking. Seek errors surface as `EngineError`, never crash.

### 8.2 Pipeline (per output session)
```
[Decoder A] ─┐
             ├─ GaplessSourceChain / CrossfadeMixer ─ ReplayGainStage ─ TempoPitchStage ─ VolumeStage ─ SoftLimiter ─ SpectrumTap ─ PcmOutputConverter ─ WasapiOut
[Decoder B] ─┘        (each decoder first goes through FormatAdapter: resample + channel map to the output format)
```
- **Neutral stages pass samples through untouched:** gain 0 dB, tempo 1.0, pitch 0, volume 1.0, limiter inactive. This preserves bit-perfect output and saves CPU.
- **`FormatAdapter`:**
  - resamples with `WdlResamplingSampleProvider` when the source rate differs from the output rate
  - mono → stereo by duplication
  - more than 2 channels → stereo downmix (L = L + 0.707·C + 0.707·Ls; R likewise) when the output is stereo.
- **`PcmOutputConverter`:** float → the device format (16-bit, 24-bit packed, 24-in-32, 32-bit int, or float) with TPDF dither **only** when reducing bit depth while DSP is active.

### 8.3 Gapless
- The player service always calls `SetNext(nextItem)` (for Repeat One, the same item with a new `Key`).
- When the current decoder is within 10 s of its end, the engine opens and pre-rolls the next decoder on the worker thread.
- `GaplessSourceChain.Read()` fills the buffer from the current source. On its end it continues **in the same buffer** from the next source, then raises `TrackStarted(next.Key)`.
- **Same output format:** zero-gap join.
- **Different sample rate in shared mode:** join through `FormatAdapter`, still gapless.
- **Different sample rate in exclusive mode with `MatchSourceRate`:** reopen the device at the new rate. A short gap is accepted and documented in the UI tooltip: "Exclusive mode switches the DAC to each track's sample rate; a brief pause between tracks with different rates is expected."

### 8.4 Crossfade
- `Crossfade = 0` → gapless path only.
- Otherwise, at `duration − crossfade` start the next source and mix with equal-power curves (`a = cos(t·π/2)`, `b = sin(t·π/2)`).
- **Manual skip** (next/prev/jump) uses a 150 ms fade instead, to avoid clicks.
- If `CrossfadeSkipSameAlbum` is on and both tracks share the same album key and consecutive track numbers → use gapless instead.
- Tracks shorter than 2 × crossfade are not crossfaded.

### 8.5 ReplayGain and volume
- **Gain:** `gainDb = (selected gain ?? FallbackGainDb) + PreampDb`. Selection:
  - `Track` → track gain
  - `Album` → album gain, else track gain
  - `Auto` → album gain when the queue came from an album/playlist opened in album order, else track gain.
- **`PreventClipping`:** `gainDb = min(gainDb, −20·log10(peak))` when a peak is known.
- **Volume:** 0.0–1.0, with a perceptual curve `amplitude = volume³` so the slider feels linear. Above 1.0 (boost, only when `AllowBoost`): linear gain up to 2.0.
- **`SoftLimiter`:** active only when total gain > 0 dB (boost, positive gain or preamp). Threshold −1 dBFS, attack 1 ms, release 100 ms. Guarantees no output sample exceeds ±1.0.
- **The Windows master volume is never changed.**

### 8.6 Output (`OutputFactory`)
- **Shared (default):** `WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latencyMs)`. Latency comes from the power profile ([§16.2](#162-power-mode-playback-profiles)); the default is 60 ms. The output format is the device mix format; sources are adapted to it.
- **Exclusive:**
  1. Build `WaveFormatExtensible` candidates at the **source** rate in this order: source bit depth (16 or 24 packed), 24-in-32 (valid bits 24), 32-bit int, 16-bit.
  2. Take the first that `device.AudioClient.IsFormatSupported(Exclusive, fmt)` accepts.
  3. If none work at the source rate, try the device's default rate with resampling; then `BitPerfect = false` and `FallbackReason = "DAC does not support 44.1 kHz"`.
  4. If opening exclusive fails (device busy, policy), fall back to shared, set `FallbackReason`, and notify once.
- **Changes to device, exclusive or latency** apply at the next safe point: while paused, at a track boundary, or immediately on user action (with a 150 ms fade). Never glitch mid-song.
- **Device lost** (unplugged or disabled): raise `DeviceLost`. The player pauses (when `PauseOnDeviceRemoved`). If the setting is "Windows default", reopen on the new default at the same position and stay paused.

### 8.7 Position and events
- Position = decoder position − output buffered latency (clamped at 0). It is exposed through `Position`.
- The engine never raises events while holding locks, and it catches every exception inside `Read()`. A decoder failure mid-track → `Error` → the player skips to the next item.

### 8.8 Spectrum (visualizer)
`SpectrumTap` copies post-volume samples into a ring buffer when `SpectrumEnabled`. On `GetSpectrum()` it runs a 1024-point FFT (`NAudio.Dsp.FastFourierTransform`, Hann window) and returns 32 log-spaced bars (0–1). There is no work when disabled. The UI reads it at up to 30 fps through `CompositionTarget.Rendering`, only while a visualizer is visible.

---

## 9. Queue and playback rules

Implement these in `PlaybackQueue` (pure logic, fully unit-tested) and drive the engine from `MusicPlayerService`.

- **Open:** replace the queue, set `Index = startIndex`. With `shuffle = true`: shuffle first, start at 0.
- **Next (manual):** `Index + 1`. At the end:
  - `Repeat.All` → 0
  - otherwise stop at the end (pause, `Completed = true`, position 0 of the last item).
- **Auto-advance (track ended):**
  - `Repeat.One` → same item again
  - otherwise the same as Next.
- **Previous:**
  - position > 3 s → seek to 0
  - else `Index − 1`
  - at index 0: `Repeat.All` → last item, else seek to 0.
- **Jump(i):** play item i from 0.
- **Shuffle on:**
  - remember the original order (list of keys)
  - move the current item to index 0 and Fisher–Yates shuffle the rest (inject `Random` for tests).
- **Shuffle off:** restore the original order and set `Index` to the current item's position there. Items added while shuffled are appended to the original order; removed items are dropped from it.
- **PlayNext(tracks):** insert after `Index`, keeping their order.
- **AddToQueue:** append.
- **Insert(i, t):** if `i ≤ Index` → `Index++`.
- **Remove(i):**
  - `i < Index` → `Index--`
  - `i == Index` → load the item now at `i` (keep the playing/paused state); if none, stop and `Index = Count − 1` or −1
  - an empty queue → `PlayerState.Empty` with the volume, shuffle and repeat kept.
- **Move(from, to):** reorder so the current item stays current (adjust `Index`).
- **Duplicates are allowed** (distinct `QueueItem.Key`). **Cap: 10,000 items.** Beyond that, reject with "Queue is full (10,000 tracks)".
- **Missing or undecodable file:** notify "Couldn't play '<title>' — skipped", advance. After 3 consecutive failures, pause and set `Error`.
- **Mix ([§22](#22-mix-auto-extending-queue)):** `MixOffset` marks where auto-added items begin.

---

## 10. Music library

### 10.1 SQLite schema (migration v1; use `PRAGMA user_version`)
```sql
PRAGMA journal_mode = WAL;
CREATE TABLE library_folders (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE albums (id INTEGER PRIMARY KEY, title TEXT NOT NULL, album_artist TEXT NOT NULL, year INTEGER, cover_key TEXT,
                     UNIQUE(title, album_artist) );
CREATE TABLE artists (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE genres  (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
CREATE TABLE tracks (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE COLLATE NOCASE,
  folder TEXT NOT NULL COLLATE NOCASE,
  file_size INTEGER NOT NULL, modified_utc INTEGER NOT NULL,
  title TEXT NOT NULL, artists_display TEXT NOT NULL, album_id INTEGER REFERENCES albums(id),
  track_no INTEGER, disc_no INTEGER, year INTEGER, genres_display TEXT,
  duration_ms INTEGER NOT NULL, codec TEXT, sample_rate INTEGER, bits_per_sample INTEGER, channels INTEGER,
  bitrate_kbps INTEGER, lossless INTEGER NOT NULL DEFAULT 0,
  rg_track_gain REAL, rg_track_peak REAL, rg_album_gain REAL, rg_album_peak REAL,
  cover_key TEXT, has_embedded_lyrics INTEGER NOT NULL DEFAULT 0,
  added_utc INTEGER NOT NULL, play_count INTEGER NOT NULL DEFAULT 0, last_played_utc INTEGER,
  liked INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0);
CREATE INDEX ix_tracks_folder ON tracks(folder);
CREATE INDEX ix_tracks_album ON tracks(album_id);
CREATE TABLE track_artists (track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                            artist_id INTEGER NOT NULL REFERENCES artists(id), role INTEGER NOT NULL, -- 0 artist, 1 album artist
                            PRIMARY KEY(track_id, artist_id, role));
CREATE TABLE track_genres (track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                           genre_id INTEGER NOT NULL REFERENCES genres(id), PRIMARY KEY(track_id, genre_id));
CREATE TABLE playlists (id INTEGER PRIMARY KEY, name TEXT NOT NULL, kind INTEGER NOT NULL DEFAULT 0, -- 0 user, 1 liked, 2 history
                        created_utc INTEGER NOT NULL, modified_utc INTEGER NOT NULL);
CREATE TABLE playlist_items (playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                             position INTEGER NOT NULL, track_id INTEGER REFERENCES tracks(id) ON DELETE SET NULL,
                             path TEXT NOT NULL, PRIMARY KEY(playlist_id, position));
CREATE TABLE play_history (id INTEGER PRIMARY KEY, track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
                           played_utc INTEGER NOT NULL);   -- trimmed to newest 1,000 rows
CREATE TABLE lyrics_cache (track_id INTEGER PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE,
                           synced INTEGER NOT NULL, body TEXT, source INTEGER NOT NULL, fetched_utc INTEGER NOT NULL); -- P6; body NULL = negative cache
CREATE VIRTUAL TABLE tracks_fts USING fts5(title, artists_display, album, genres_display, content='', tokenize='unicode61 remove_diacritics 2');
```
- **FTS maintenance:** keep `tracks_fts` in sync in the repository (insert/delete by `rowid = tracks.id`).
- **Search:** each user token becomes `token*`; tokens are ANDed. Sanitise quotes and special FTS characters.
- **Concurrency:** one `SemaphoreSlim` serialises writes. Reads open their own connection. Every command is parameterised; never build SQL from input.
- **Corrupt or unreadable DB:** rename it to `library.db.corrupt-<timestamp>`, create a fresh one, log it, and notify "Music library was rebuilt".

### 10.2 Scanner
1. **Enumerate** each folder with `Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })`. Filter by supported extension and by `MinimumFileSizeKb` (default 100).
2. **Compare** against the DB (path → size, modified time):
   - unchanged → skip
   - new or changed → read tags
   - DB rows whose file no longer exists (under that root) → delete.
3. **Read tags in parallel:** max degree `clamp(ProcessorCount / 2, 1, 4)`; exactly 1 in the Eco profile or on battery. Thread priority `BelowNormal`. Insert in transactions of 200.
4. **Report progress:** `LibraryScanProgress(found, processed, added, updated, removed, currentFolder)`. Throttle to 4 Hz.
5. **Cancellation** leaves the DB consistent (the current batch is committed or rolled back as a whole).
6. **Unreadable tags:** index with the file name as title and "Unknown artist", then log a warning.
7. **Splitting** (`ArtistGenreSplitter`): split artists on `;`, `/`, ` feat. `, ` ft. `, ` featuring `, ` & `, `, `. Splitting on `&` and `,` is configurable (`SplitArtistsOnAmpersandAndComma`, default **false**, because of names like "Simon & Garfunkel"). Split genres on `;`, `/`, `,`. Trim, deduplicate case-insensitively, and drop empty entries.

### 10.3 Watching
`LibraryWatcher` runs one `FileSystemWatcher` per root (`IncludeSubdirectories = true`, `InternalBufferSize = 64 KB`).
- Created, changed, deleted and renamed events are collected per folder and debounced (3 s; 10 s in the Eco profile), then that folder is rescanned incrementally.
- On an `Error` (buffer overflow), queue an incremental rescan of the whole root.

### 10.4 Covers
- **`coverKey`:** SHA-1 of the embedded image bytes, or SHA-1 of the lower-cased path of the folder image. It is stored per track and per album.
- **Cached files:** `covers\{coverKey}_{size}.jpg` at 128, 256 and 512 px, JPEG quality 85. Generated lazily with WinRT `Windows.Graphics.Imaging` (`BitmapDecoder` → `BitmapTransform` → `BitmapEncoder`). No WPF in Core.
- **Dominant colour:** decode to 32×32, bucket colours (5 bits per channel), skip near-black, near-white and low-saturation buckets, pick the heaviest. Return null if none qualify.
- **WPF display:** `BitmapImage` with `DecodePixelWidth` equal to the display size, `CacheOption = OnLoad`, and `Freeze()`. Load off the UI thread through a small async image loader with an LRU of 300 frozen bitmaps.

---

## 11. Playlists, history and stats
- **`PlaylistService`:** CRUD, reorder, add/remove.
  - Items keep `path` so tracks outside the library still work.
  - "Liked" (`kind = 1`) and "Recently played" (`kind = 2`, read-only view over `play_history`) are created on first run.
- **`M3uSerializer`:**
  - import: `.m3u`/`.m3u8`; skip `#EXTM3U`/`#EXTINF`; resolve relative paths against the playlist file's folder; read as UTF-8 (strip BOM), falling back to Windows-1252 for `.m3u`
  - export: `.m3u8` UTF-8 with `#EXTINF:<seconds>,<artist> - <title>`, choosing relative or absolute paths.
- **`HistoryIntegration`:**
  - a play counts after 30 s of playback or 50% of the track, whichever comes first
  - then `RecordPlayAsync` increments `play_count`, sets `last_played_utc`, inserts into `play_history` and trims to 1,000
  - only for library tracks (`Id > 0`).
- **Stats (P6):** a "Most played" system view (top 100 by `play_count`) on the Playlists tab.

---

## 12. Settings schema

Add to `AppSettings`:
```csharp
[JsonPropertyName("music")]
public MusicSettings Music { get; set; } = new();
```
and in `Clone()`: `Music = this.Music.Clone(),`. **This is mandatory; see §4.**

`MusicSettings` (every property has `[JsonPropertyName]` in camelCase and a default; `Clone()` deep-copies the lists):

| Property | Type | Default | Phase |
|---|---|---|---|
| `LibraryFolders` | `List<string>` | empty | 2 |
| `ScanOnStartup` | bool | true | 2 |
| `WatchFolders` | bool | true | 2 |
| `MinimumFileSizeKb` | int | 100 | 2 |
| `SplitArtistsOnAmpersandAndComma` | bool | false | 2 |
| `ResumePlaybackOnStartup` | bool | false | 1 |
| `AllowVolumeBoost` | bool | false | 1 |
| `OutputDeviceId` | string? | null (= Windows default) | 4 |
| `PauseOnDeviceRemoved` | bool | true | 4 |
| `ExclusiveMode` | bool | false | 5 |
| `ExclusiveMatchSourceRate` | bool | true | 5 |
| `ReplayGainMode` | ReplayGainMode | Off | 5 |
| `ReplayGainPreampDb` | double | 0 | 5 |
| `ReplayGainPreventClipping` | bool | true | 5 |
| `ReplayGainFallbackDb` | double | 0 | 5 |
| `CrossfadeSeconds` | double | 0 | 5 |
| `CrossfadeSkipSameAlbum` | bool | true | 5 |
| `ShowVisualizer` | bool | true | 5 |
| `AccentFromArtwork` | bool | true | 3 |
| `MiniPlayer.ShowOnStartup` | bool | false | 3 |
| `MiniPlayer.Hotkey` | string | `"Ctrl+Shift+M"` | 3 |
| `MiniPlayer.Corner` | MiniPlayerCorner | BottomRight | 3 |
| `MiniPlayer.PositionByMonitor` | `Dictionary<string, double[]>` | empty (monitor device name → [x, y] as fraction of work area) | 3 |
| `MiniPlayer.Opacity` | double | 0.95 | 3 |
| `MiniPlayer.Expanded` | bool | false | 3 |
| `MiniPlayer.ClickThrough` | bool | false | 3 |
| `MiniPlayer.ShowExternalMedia` | bool | true | 3 |
| `UniversalControllerEnabled` | bool | true | 3 |
| `RemoteControlEnabled` | bool | true | 3 |
| `Automation.GameDuckMode` | GameDuckMode | Duck | 4 |
| `Automation.DuckLevelPercent` | int | 30 | 4 |
| `Automation.UseFullscreenDetection` | bool | true | 4 |
| `Automation.PowerProfilesEnabled` | bool | true | 4 |
| `Automation.PauseOnLowBattery` | bool | false | 4 |
| `Automation.SleepFadeSeconds` | int | 10 | 4 |
| `Automation.SleepDefaultAction` | SleepTimerAfterAction | Stop | 4 |
| `LyricsFromFiles` | bool | true | 6 |
| `LyricsOnline` | bool | **false** | 6 |
| `ShowTrackChangeNotification` | bool | false | 6 |
| `Integrations.DiscordRpc` | bool | **false** | 6 |
| `Integrations.LastFmEnabled` | bool | **false** | 6 |
| `Integrations.LastFmUsername` | string? | null (display only; session key lives in `ISecretStore`) | 6 |
| `Integrations.SpotifyEnabled` | bool | **false** | 7 |
| `Integrations.SpotifyClientId` | string? | null | 7 |

**Session values are *not* in settings:** volume, mute, shuffle, repeat, speed and pitch live in `playback-state.json`. Otherwise every slider drag would rewrite `settings.json` and raise `OnSettingsChanged` for every service.

Backward compatibility: a `settings.json` with no `music` key must load with defaults (test this).

---

## 13. Persisted playback state and secrets
- **`playback-state.json`** (`PlaybackStateStore`, atomic write as in `SettingsService`):
  ```json
  { "version": 1, "queue": [{ "trackId": 42, "path": "D:\\Music\\a.flac" }], "originalOrder": [0,3,1,2],
    "index": 3, "positionMs": 81234, "volume": 0.8, "muted": false, "shuffle": true, "repeat": "All",
    "speed": 1.0, "pitchSemitones": 0, "mixOffset": null }
  ```
  - **Written:** on track change, pause, queue edit (debounced 2 s), every 30 s while playing, and on shutdown.
  - **Restore:** resolve `trackId` via the DB, else read the file directly. Drop entries whose file is missing (log a count). A corrupt file → defaults.
- **`DpapiSecretStore`:** `ProtectedData.Protect(bytes, entropy: "AxePower.Music.v1", DataProtectionScope.CurrentUser)` into `Music\secrets.bin` (a JSON dictionary, encrypted as a whole, atomic write). **Never log secret values.**

---

## 14. UI specification

Every screen uses the semantic brushes and styles from §4. Required on all of them:
- keyboard reachable, with visible focus
- `AutomationProperties.Name` on every icon-only button, plus a tooltip
- no state shown only by colour (shuffle/repeat use a glyph plus tooltip text such as "Repeat: One")
- text wraps or truncates with a tooltip showing the full value
- works at 1024×760 and at 125/150/200% scaling
- `ReduceMotion` → no marquee, no animated visualizer, no smooth lyric scrolling (jump instead)
- all four themes: System, Dark, Light, Glass.

### 14.1 Navigation
- **`MainWindow.xaml` sidebar, CONTROL group, after "Battery":** a button with `Style="{Binding MusicActiveStyle}"`, `Command="{Binding NavigateCommand}"`, `CommandParameter="Music"`, glyph `&#xE8D6;` (Segoe MDL2 "Audio") and text "Music".
- **Height check:** confirm the sidebar still fits at `MinHeight="760"` in all themes and at 125/150% scaling. If it does not, raise `MinHeight` by exactly one sidebar item's height and note it in the phase report.
- **`MainViewModel`:**
  - inject `MusicViewModel`
  - add `"Music" => MusicVm` to `Navigate`
  - add `MusicActiveStyle => GetStyleForView(MusicVm)`, raised in `OnCurrentViewChanged`
  - accept deep links `Music:Albums`, `Music:Playlists`, `Music:NowPlaying`.
- **`MainWindow.xaml` resources:** `<DataTemplate DataType="{x:Type music:MusicViewModel}"><music:MusicView/></DataTemplate>`.

### 14.2 Music page
```
┌ Music ──────────────────────────────────────── [ 🔍 Search library…      ] [ + Add folder ] ┐
│ [Tracks] [Albums] [Artists] [Genres] [Folders] [Playlists]      ⟳ Scanning 1,204 / 5,310  ✕ │
├───────────────────────────────────────────────────────────────────────┬──────────────────────┤
│                                                                       │ [Queue] [Lyrics]     │
│   virtualized content for the selected tab                            │  drawer (toggle,     │
│   (list, album grid, tree, …)                                         │  width 320, resizable)│
├───────────────────────────────────────────────────────────────────────┴──────────────────────┤
│ [art] Title                        ⤮  ⏮  ⏯  ⏭  ↻      0:42 ━━━━━●──────── 3:51     🔊 ━━●━━   │
│       Artist — Album          [FLAC · 24/96 · Shared]   ⏱ Sleep   ☰ Queue   ♪ Lyrics   ⧉ Mini │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```
- **Tabs (Phase 1 shows only "Now Playing / Queue" and the open/drop actions; the library tabs arrive in Phase 2):**
  - **Tracks:** `ListView` + `GridView` columns (#, Title, Artist, Album, Year, Duration, ♥). Virtualized (`VirtualizingPanel.IsVirtualizing="True"`, `VirtualizationMode="Recycling"`, `ScrollUnit="Pixel"`). Sortable column headers.
  - **Albums:** `VirtualizingWrapPanel` grid of 160 px cards (cover, title, artist, year). Click → album detail (header plus track list, "Play", "Shuffle").
  - **Artists / Genres:** virtualized list → detail (albums and tracks).
  - **Folders:** tree of library folders → tracks in the folder; "Play folder".
  - **Playlists:** list plus detail; New / Import M3U / Export / Rename / Delete (with confirmation). Liked, Recently played, Most played (P6) are pinned at the top.
- **Track actions:**
  - double-click or Enter plays from that track in the current list context
  - context menu: Play, Play next, Add to queue, Add to playlist ▸, Go to album, Go to artist, Like/Unlike, Show in Explorer, Edit tags… (P6), Hide from library.
  - **Never offer "Delete file".**
- **Empty states:**
  - no folders → a card: "Add your music folder to build your library" + [Add Music folder] (preselects `%USERPROFILE%\Music` when it exists) + [Choose folder…]
  - scanning with nothing yet → progress with counts
  - no search results → "No matches for '…'"
  - queue empty → "Nothing queued — pick something from your library or drop files here."
- **Errors:** inline `WarningBrush`/`ErrorBrush` banners with a plain message and a retry where it makes sense.
- **Open and drop:**
  - "Open files…" (`OpenFileDialog`, multi-select, audio filter) and "Open folder…" (`OpenFolderDialog`, available in .NET 8 WPF).
  - **Drag-and-drop must work while elevated:** `FileDropHelper` calls `DragAcceptFiles(hwnd, true)` and `ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES (0x233), MSGFLT_ALLOW)`, plus `WM_COPYDATA (0x4A)` and `0x0049`, then handles `WM_DROPFILES` via `DragQueryFile`.
  - Dropping on the Music page → `OpenPathsAsync`. Dropping on the queue drawer → append.
- **Keyboard** (page focused and no text box focused):
  - Space: play/pause
  - Ctrl+→ / Ctrl+←: next/previous
  - Ctrl+↑ / Ctrl+↓: volume ±5%
  - Ctrl+F: search
  - Enter: play selection
  - Delete: remove from queue (queue focused)
  - Ctrl+L: like.
- **Now Playing bar:**
  - seek slider: keyboard steps of 5 s; drag only seeks on release
  - volume slider: keyboard steps of 5%; mute toggle
  - **Format badge:** built only from `OutputInfo` + `Track.Format`; "Bit-perfect" appears **only** when `OutputInfo.BitPerfect`
  - sleep timer button shows the remaining time when active
  - "⧉ Mini" opens the mini player.
- **Accent from artwork:** when `AccentFromArtwork` is on, set the page-local resource `MusicAccentBrush` to the dominant colour if contrast against `CardBackgroundBrush` is ≥ 3:1; otherwise use `AccentBrush`. Add `MusicAccentBrush` = `AccentBrush` to all three theme dictionaries as the fallback. **Never recolour the app shell.**

### 14.3 Mini player window (`Windows/MiniPlayerWindow.xaml`)
```
Compact 360×84                                   Expanded 360×220
╭─────────────────────────────────────────╮     ╭─────────────────────────────────────────╮
│ [art] Title – Artist      ⏮  ⏯  ⏭  ⤢ ✕ │     │ [ art ] Title                     ⤡  ✕ │
│       ━━━━━━●──────── 1:12 / 3:40  ▁▃▅▂ │     │ [ 96  ] Artist — Album                  │
╰─────────────────────────────────────────╯     │ ━━━━━━━━━●────────────── 1:12 / 3:40    │
                                                │ ⤮   ⏮   ⏯   ⏭   ↻        🔊 ━━━●━━    │
                                                │ Up next: Song name – Artist             │
                                                │ ♪ current lyric line (if available)     │
                                                ╰─────────────────────────────────────────╯
```
- **Window properties:**
  - `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `Topmost="True"`, `ShowInTaskbar="False"`, `ShowActivated="False"`, `ResizeMode="NoResize"`
  - rounded card using `ElevatedCardBackgroundBrush` + `CardBorderBrush`, corner radius 14
  - in `SourceInitialized`, add `WS_EX_TOOLWINDOW (0x80) | WS_EX_NOACTIVATE (0x08000000)` so it never steals focus from games or other apps.
- **Drag and position:**
  - drag with `DragMove()` from the art or title area
  - on release, snap to the nearest corner of the current monitor's **work area** if within 48 px of it
  - save the position per monitor (fraction of the work area); off-screen positions (monitor removed) → reset to `Corner`.
- **Behaviour:**
  - scroll wheel over the window changes volume
  - double-click the art → open AxePower on the Music page
  - hovering shows the controls; with `ReduceMotion` there are no fade animations.
- **Source switching:** shows the AxePower player or the external session per [§17](#17-universal-media-controller) priority, with a small source label ("AxePower", "Spotify", "Chrome"…).
- **Click-through (`ClickThrough`):** adds `WS_EX_TRANSPARENT | WS_EX_LAYERED`. It can **always** be turned off from the tray menu item "Mini player: click-through ✓" and by pressing the hotkey twice within 1 s. Document both escapes next to the setting.
- **Limitation (shown in Settings):** "The mini player appears over borderless and windowed games, not over exclusive-fullscreen games."
- **`MiniPlayerService`** (App) creates the window lazily, shows or hides it, and restores `ShowOnStartup`. `HotkeyService` toggles it.

### 14.4 Tray menu
Add a section at the **top** of the existing tray `ContextMenu`, visible only when there is a current item (AxePower or external). Follow the existing `MenuItem` style and brushes:
```
♪ Title – Artist            (header, disabled item, trimmed to 40 chars with a tooltip)
  ⏯ Play / Pause
  ⏭ Next
  ⏮ Previous
  ⧉ Mini player             (checkable)
  ⏱ Sleep timer ▸  15 min · 30 min · 60 min · End of track · Cancel
───────────────
(existing items unchanged)
```
- Bind these to `NowPlayingViewModel` commands. The tray's `DataContext` is `MainViewModel`, so expose `NowPlaying` as a property there.
- **Tray tooltip:** "AxePower — ▶ Title – Artist" while playing. Otherwise keep the existing tooltip.

### 14.5 Dashboard card
- Before editing, read `Views/DashboardView.xaml` and `ViewModels/DashboardViewModel.cs`. It is a large layout with a 2×2 performance grid.
- Add `NowPlayingCard` (a `UserControl` bound to `NowPlayingViewModel`, 72–88 px tall):
  - art, title/artist, play/pause, next, and a thin progress bar
  - when nothing is playing: "Nothing playing · Open Music" (a button that navigates to Music).
- Place it where it does not push existing cards below the fold at 1280×840 and does not clip at 1024×760. If there is no clean spot, add it as a full-width row at the bottom of the scrollable content.
- **Hide it entirely when `Music` is unused** (no library folders and nothing ever played).

### 14.6 Settings → "Music" section
Hosted in `SettingsView` as `MusicSettingsSection` bound to `SettingsViewModel.Music` (`MusicSettingsViewModel`). Save through `SettingsService.SaveSettings` using the same pattern as `SettingsViewModel` (partial `OnXChanged` → save).

Groups (each control only appears once its phase has shipped):
1. **Library:** folders list (add/remove), scan now, scan on startup, watch folders, minimum file size, split artists on "&" and ",", rebuild library (with confirmation).
2. **Playback:** resume on startup, allow volume boost, ReplayGain mode/preamp/prevent clipping/fallback, crossfade seconds, skip crossfade within albums.
3. **Audio output:** device (Windows default + list), exclusive mode, match source sample rate, pause when device is removed. The explanation text covers exclusive mode blocking other apps' sound.
4. **Mini player:** show on startup, hotkey (a capture box, validated with `RegisterHotKey`, conflicts reported), opacity, click-through + the escape instructions, show other apps' media, the fullscreen limitation note.
5. **Automations:** game-mode behaviour (Off/Duck/Pause) + duck level, use fullscreen detection, power-mode profiles, pause on low battery (uses the existing threshold, shown read-only with a link to Battery), sleep fade seconds and default action.
6. **Online features** (each with a "What is sent" line):
   - lyrics from LRCLIB: sends artist, title, album and duration to lrclib.net
   - Discord status: shares the current track with your Discord friends
   - Last.fm: sends every played track to your Last.fm profile
   - Spotify (P7).
7. **Phone remote:** enable music on the phone portal (applies while LAN Drop is running).

### 14.7 Taskbar (`TaskbarMediaIntegration`)
- On `MainWindow.TaskbarItemInfo`: three `ThumbButtonInfo`s (Previous, Play/Pause, Next) using `DrawingImage`s made from Segoe MDL2 glyphs `E892`/`E768`/`E769`/`E893`, bound to commands, with the description text as tooltip.
- Previous and Next are disabled when unavailable (first or last item without Repeat All).
- `ProgressState` = `Normal` while playing, `Paused` while paused with progress, `None` when idle. `ProgressValue` = position ÷ duration, updated at most once per second.
- Note: thumbnail buttons are only visible while the main window is on the taskbar, not while it is hidden in the tray. That is expected.

---

## 15. Windows integration: media controls, media keys, taskbar

**`SmtcIntegration`** (Core; requires D2):
- **Obtaining SMTC:** `Windows.Media.SystemMediaTransportControlsInterop.GetForWindow(hwnd)`. If that helper is not in the SDK projection, fall back to creating a `Windows.Media.Playback.MediaPlayer`, setting `CommandManager.IsEnabled = false`, and using `player.SystemMediaTransportControls`. Document which path was used in the phase report.
- **Setup:** `IsEnabled = true`, `IsPlayEnabled`, `IsPauseEnabled`, `IsNextEnabled` and `IsPreviousEnabled` follow the queue.
- **`ButtonPressed`** → marshal to the player (Play/Pause/Next/Previous/Stop). **`PlaybackPositionChangeRequested`** → `SeekAsync`.
- **On current-item change:**
  - `DisplayUpdater.Type = MediaPlaybackType.Music`
  - `MusicProperties.Title`, `.Artist` (artists joined with ", "), `.AlbumTitle`, `.AlbumArtist`, `.TrackNumber`
  - `Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(cover512Path))` when a cover exists
  - then `DisplayUpdater.Update()`.
- **`PlaybackStatus`** = `Playing`/`Paused`/`Stopped`/`Closed` (empty queue).
- **Timeline:** `UpdateTimelineProperties` with Start 0, End = duration, Position, MinSeek 0, MaxSeek = duration. Call it on state change, on seek, and every 5 s while playing. **Not** on every tick.
- **Media keys:** hardware media keys and the Windows media flyout then work automatically. **Do not add a keyboard hook.**
- **Disposal:** set `IsEnabled = false` and clear the display on shutdown.

Also note: the LAN portal's existing `/api/remote` simulated media keys (`play-pause`, `next`, `prev`) go to whichever media session Windows considers current. That now includes AxePower Music. No change is needed for that.

---

## 16. AxePower automations

`MusicAutomationService` (Core) wires the following. Each rule is individually switchable in Settings and tested with fakes.

### 16.1 Game Mode auto-duck / pause
- **Add to `AppMonitorService`:**
  - `public event EventHandler<string>? TrackedAppStarted;`
  - `public event EventHandler<string>? TrackedAppStopped;`
  - raise them where the rule match and revert happen today (process name as argument).
  - **Do not change the existing behaviour.**
- **Fullscreen detection** (when `UseFullscreenDetection` and the player is **playing**):
  - poll `IFullscreenDetector` every 5 s
  - `SHQueryUserNotificationState` returning `QUNS_RUNNING_D3D_FULL_SCREEN (3)` or `QUNS_BUSY (2)` means "game/fullscreen active"
  - active only after 2 consecutive positive checks (10 s) to avoid flapping.
- **When a game starts:**
  - `Duck` → `DuckAsync(DuckLevelPercent / 100, 300 ms)`
  - `Pause` → pause, and remember that *we* paused
  - external sessions ([§17](#17-universal-media-controller)) can only be paused, not ducked; in `Duck` mode they are left alone.
- **When the game ends:** after a 2 s grace period, `UnduckAsync(1 s)`, or resume if *we* paused. **Do not resume if the user paused or changed tracks manually during the game.**
- Log each transition. No user notification by default (it would appear in-game).

### 16.2 Power-mode playback profiles
- **Add to `PowerPlanService`:** `public event EventHandler<PowerMode>? ModeApplied;`, raised **only** after a verified successful apply (inside `ApplyModeAsync` where `success` is true, after the verification branch). Keep the current return values.
- **Profiles** (when `PowerProfilesEnabled`):

  | Mode | Shared latency | UI position rate | Visualizer | Artwork accent | Watcher debounce | Scan parallelism |
  |---|---|---|---|---|---|---|
  | Eco | 200 ms | 1 Hz | off | off | 10 s | 1 |
  | Smart | 60 ms | 4 Hz | per setting | per setting | 3 s | default |
  | Beast / GameMode | 60 ms | 4 Hz | per setting | per setting | 3 s | default |

- **Latency changes** apply at the next safe point (§8.6).
- **The UI must not claim battery savings.** The Settings text says "Reduces CPU wake-ups while in Eco mode." Numbers may only be quoted after measurement with the method recorded in `WORK_LOG.md`.

### 16.3 Sleep timer
- **Options:** 15 / 30 / 45 / 60 / 90 minutes, custom (1–480), end of current track, end of queue.
- **Ending:** fade out over `SleepFadeSeconds`, then pause.
- **After-actions:**
  - **Stop** (default)
  - **Sleep PC:** `SetSuspendState(false, false, false)`
  - **Hibernate PC:** `SetSuspendState(true, false, false)`, offered only when `IsPwrHibernateAllowed()` is true; otherwise disabled with "Hibernate is turned off on this PC".
- **Safety for Sleep/Hibernate:** 30 s before the action, show a notification "PC will sleep in 30 s — open AxePower to cancel", and keep a Cancel command in the tray. Never act if the timer was cancelled. Tests use a fake `ISystemPowerActions`, **never the real one**.
- **Display:** remaining time in the Now Playing bar, the mini player (expanded) and the tray submenu.

### 16.4 Low-battery rule
When `PauseOnLowBattery` is on and the PC is on battery at or below `AppSettings.LowBatteryThresholdPercent`, pause once and notify "Music paused — battery at 18%". Do not auto-resume.
- **Reuse `BatteryService`'s existing data or events.** Read it first; do not add a second polling loop if one already exists.
- On desktops without a battery the setting is disabled with "No battery detected".

### 16.5 Device switching and removal
Implemented through `IAudioDeviceService` (`MMDeviceEnumerator` + `IMMNotificationClient`):
- The picker lists active render devices plus "Windows default (follows changes)".
- **Removed device:** if it was the selected device → pause, notify "Output device disconnected", and fall back to Windows default.
- **Default device changed** while "Windows default" is selected: move the output to the new device, keep the position, and keep the play state (unless `PauseOnDeviceRemoved` applies).

---

## 17. Universal media controller

`UniversalMediaService` uses `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (Windows 10 1809+).
- **Setup:** `RequestAsync()` once; subscribe to `SessionsChanged` and `CurrentSessionChanged`, and to each session's `MediaPropertiesChanged`, `PlaybackInfoChanged` and `TimelinePropertiesChanged`.
- **`ExternalMediaSession` snapshot:** `SourceAppUserModelId`, friendly app name (map known ids such as `Spotify.exe`→"Spotify", `chrome`→"Chrome", `msedge`→"Edge"; otherwise the id), title, artist, album, thumbnail (read once per track into a temp file or bytes), position, duration, playing, can-next, can-previous, can-seek.
- **Excluding AxePower's own session:**
  - ignore sessions whose `SourceAppUserModelId` contains `AxePower` (case-insensitive)
  - in Phase 3, log the actual id Windows reports for AxePower and tighten the filter to it
  - **never show AxePower twice.**
- **Choosing the external session:** the playing session that changed most recently; else the manager's `CurrentSession`.
- **Display priority** for the mini player, tray, dashboard and phone:
  1. AxePower playing
  2. external playing
  3. AxePower paused with a current item
  4. external paused
  5. nothing.

  Commands go to the source being displayed.
- **Honest limits:** external apps decide what they allow. If `TryXxxAsync` returns false, show "Spotify didn't accept the command" and do not pretend it worked.

---

## 18. Phone remote (LAN portal) and security fix

### 18.1 Security fix (required before any music endpoint ships)
**The problem today:** the portal answers anyone on the network and sends `Access-Control-Allow-Origin: *`. `POST /api/remote` with a `text/plain` body is a "simple" request that browsers send cross-origin without a preflight. So **any web page the user visits** could make the PC lock or skip tracks by posting to `http://<lan-ip>:5050/api/remote`, and anyone on the same Wi-Fi can call every endpoint.

**Fix:**
- **Token:** generate a 128-bit random access token (base64url) on first use and store it through `ISecretStore` as `landrop.token`. Add "Reset phone access" in the Phone Control page, which regenerates it and invalidates old phones.
- **QR code and `ServerUrl`:** `http://<ip>:<port>/#t=<token>` (a URL fragment, so it is not sent in request lines or logged).
- **Portal JS:** reads the token from `location.hash` into `sessionStorage`, then removes the hash with `history.replaceState`. It sends `X-AxePower-Token: <token>` on **every** `/api/*` call.
- **Server:**
  - requires the header on all `/api/*` routes (existing and new), compared with `CryptographicOperations.FixedTimeEquals`
  - missing or wrong → `401` with no body details
  - `GET /` and `GET /qr` stay open (the QR only renders on the PC's own screen and page).
- **CORS:** stop sending `Access-Control-Allow-Origin: *` (the portal is same-origin). Answer `OPTIONS` preflights with `403`, so browsers cannot send the custom header cross-origin.
- **Communicate the change:** update the Phone Control UI copy, the patch notes and `LanDropServiceTests`. Existing behaviour for a phone that scans the new QR code is unchanged.

### 18.2 Music API (served by `LanDropService`, logic in `MusicRemoteApi`)
All routes require the token and return `application/json; charset=utf-8`. Every route answers `404 {"error":"music_disabled"}` when `RemoteControlEnabled` is off.

| Method | Route | Behaviour |
|---|---|---|
| GET | `/api/music/state` | `{ source: "axepower"\|"external"\|"none", app, title, artists, album, durationMs, positionMs, playing, shuffle, repeat, volume, muted, trackId, hasCover, coverVersion, badge, sleepTimerEndsUtc, canNext, canPrev, canSeek }` |
| GET | `/api/music/cover?v=<coverVersion>&s=256` | JPEG of the **current** item's cover from the cover cache only. `s` is limited to 128/256/512. Never accepts a path |
| GET | `/api/music/queue?offset=0&limit=100` | `{ total, index, items: [{ i, trackId, title, artists, durationMs }] }` (limit ≤ 200) |
| GET | `/api/music/search?q=…&limit=30` | Library search (limit ≤ 50) |
| GET | `/api/music/playlists` | `[{ id, name, count, kind }]` |
| POST | `/api/music/command` | JSON `{ action, value? }`. Actions: `toggle`, `play`, `pause`, `next`, `prev`, `seek` (ms), `volume` (0–100, or 0–200 if boost is allowed), `mute`, `shuffle` (bool), `repeat` (`off\|all\|one`), `jump` (index), `sleep` (minutes, 0 = cancel) |
| POST | `/api/music/play` | JSON `{ kind: "track"\|"album"\|"playlist", id, shuffle? }`. IDs are resolved through the DB only |

- **Validation:** unknown actions → `400`. The body limit is 4 KB.
- **Where logic lives:** all handler logic goes in `MusicRemoteApi` (unit-testable). `LanDropService` only routes.
- **Portal UI:** replace the current "🎵 Media & Audio Controls" card with a Now Playing card:
  - cover, title/artist, source label, seek bar, transport buttons, volume, shuffle/repeat
  - collapsible "Up next" and a "Search library" box, and a playlists list
  - polls `/api/music/state` every 1 s while `document.visibilityState === "visible"`, every 5 s otherwise
  - uses the portal's existing styling
  - the old buttons remain as a fallback when `source` is `none`.

---

## 19. Lyrics
- **`LyricsService.GetAsync(track)`:** try each source in order and stop at the first hit:
  1. `<same folder>/<same file name>.lrc` (UTF-8, else Windows-1252)
  2. embedded synced (SYLT, converted to LRC)
  3. embedded unsynced (USLT / `LYRICS` Vorbis comment)
  4. **LRCLIB, only when `LyricsOnline`:** `GET https://lrclib.net/api/get?artist_name=&track_name=&album_name=&duration=<seconds>`
     - `User-Agent: AxePower/<version> (https://github.com/mrAbhimanyuVishwakarma/AxePower)`
     - 8 s timeout; results cached in `lyrics_cache`
     - a 404 is cached as a negative result for 7 days
     - network errors are not cached.
- **`LrcParser`:**
  - handles `[mm:ss]`, `[mm:ss.xx]`, `[mm:ss.xxx]` and several timestamps per line
  - applies the `[offset:±ms]` tag
  - ignores metadata tags (`[ar:]`, `[ti:]`…) and word-level `<mm:ss.xx>` tags (strip them)
  - sorts by time.
- **`LyricsPanel`:**
  - synced: highlight the current line, auto-scroll to keep it centred (a jump with `ReduceMotion`), click a line to seek there
  - unsynced: plain scrollable text
  - per-track offset buttons (±0.5 s) stored in memory for the session.
- **Mini player (expanded):** optionally shows the current line.

---

## 20. Discord Rich Presence and Last.fm

**Discord (`DiscordRpcIntegration`):**
- **Setup:** the owner creates a Discord application (Developer Portal) and puts its **Application ID** in `AxePower.Core/Services/Music/Integrations/DiscordConfig.cs`. The ID is public, not a secret. Upload an `axepower` art asset.
- **Presence:** `Details` = title, `State` = "by <artists>", timestamps from position and duration, large image = `axepower` asset (local cover art cannot be shown without uploading it somewhere, and the plan does not do that).
- **Paused:** clear the presence.
- **Robustness:** reconnect lazily and never block playback. When the setting is off, dispose the client.

**Last.fm (`LastFmScrobbler`, `LastFmClient`):**
- **API credentials:** API key and shared secret come from a git-ignored `LastFmSecrets.cs`, generated at build time from environment variables `AXEPOWER_LASTFM_KEY` / `AXEPOWER_LASTFM_SECRET`, with an MSBuild target that writes empty strings when they are unset. **Never commit them.** If they are empty, the Last.fm setting is disabled with "Not available in this build".
- **Auth (desktop flow):**
  1. `auth.getToken`
  2. open the browser to `https://www.last.fm/api/auth/?api_key=…&token=…`
  3. the user clicks "I've authorized" in AxePower
  4. `auth.getSession` → store the session key in `ISecretStore` (`lastfm.session`) and the username in settings.
- **Requests:** signed with MD5 per the Last.fm spec, over HTTPS only.
- **Scrobbling:**
  - `track.updateNowPlaying` on track start
  - scrobble when the track is longer than 30 s **and** played for ≥ 50% or ≥ 4 minutes
  - the timestamp is the track start time (UTC)
  - failed scrobbles go to `scrobble-queue.json` (max 500), retried in batches of 50 with backoff
  - "Sign out" clears the secret and the queue.

---

## 21. Tag editor
- **`TagEditorDialog`:** single or multiple selection.
  - fields: Title, Artists (comma separated → split), Album, Album artist, Track #, Disc #, Year, Genres, Cover (replace from file / remove), Lyrics
  - multi-selection edits only fields the user changes; mixed values show "(multiple values)".
- **Saving:**
  1. refuse if the file is read-only (explain why)
  2. copy the original to `Music\tag-backups\<timestamp>_<filename>` (keep the newest 20)
  3. if the file is the current track, remember the position and state, unload the engine, write, then reload at the same position
  4. write with TagLib#
  5. rescan that file and refresh the UI.
- **"Undo last tag edit"** restores from the backup.
- **Never touch audio data.** Errors are shown and logged with the path **redacted to the file name**.

---

## 22. Mix (auto-extending queue)
- **Toggle:** Queue drawer → "Mix ∞". `SetMix(true)` sets `MixOffset = Queue.Count`.
- **When ≤ 3 items remain after the current one,** `MixGenerator` appends 20 library tracks. Candidates, in order:
  1. same artists as the last 5 played
  2. same genres
  3. album artist's other albums
  4. year within ±3.
- **Filters:** exclude tracks played in the last 2 hours and anything already in the queue; weighted random, deterministic under a test seed.
- **`SetMix(false)`:** if `Index < MixOffset`, remove the auto-added items; otherwise just clear `MixOffset`.
- **Everything is local.** No online "similar artists" lookups.

---

## 23. Open with AxePower (file arguments)
- **Arguments:** `App.Application_Startup` collects `e.Args` that are existing audio files or folders (ignore flags such as `--force`). If this is the primary instance, call `OpenPathsAsync(args)` after the player initialises.
- **Single-instance pipe protocol:**
  - the client writes byte `2`, a 4-byte little-endian length, then UTF-8 JSON `{"paths":[…]}`
  - the server reads byte `1` as the legacy "show window" and byte `2` as "show window + open paths"
  - validate: max 64 KB, max 500 paths, each must exist and be a supported file or a folder.
- **Elevation:** `TryElevateAndExit` already forwards `args`, so paths survive the elevated relaunch. When AxePower is **not** running and `RunAsAdministrator` is on, opening a file triggers a UAC prompt. Mention this next to the setting.
- **Optional registration** (Settings → Music → "Show AxePower in 'Open with'"), explicit user action only, HKCU only, fully removable with "Remove from 'Open with'":
  - `HKCU\Software\Classes\Applications\AxePower.exe\shell\open\command` = `"<exe path>" "%1"`
  - `HKCU\Software\Classes\Applications\AxePower.exe\SupportedTypes` with a value name for each supported extension
  - `FriendlyAppName` = "AxePower Music".
  - **Never** change default-app associations (Windows forbids it for good reason) and never touch HKLM.

---

## 24. Spotify Connect remote (optional Phase 7)

### What it is and is not
- ✅ **Is:** logging in to a real Spotify account to see what is playing, browse your playlists, and control playback on any **Spotify Connect device**: the Spotify desktop app, phone, web player, speakers, consoles. That includes starting a playlist on a device and moving playback between devices.
- ❌ **Is not:** playing Spotify audio *through AxePower's engine*. Spotify audio is DRM-protected and only official Spotify clients can decode it. The Web Playback SDK needs Widevine DRM, which WebView2 does not support. Third-party clients such as librespot violate Spotify's terms and must not be used. Consequently AxePower's EQ, exclusive mode and crossfade cannot apply to Spotify.

### Spotify developer rules (as of 2026; re-check at build time)
- Since **Feb 2026**, a *new* Development Mode app is limited to **5 users**, and the **app owner must have Spotify Premium**. Batch-fetch, browse/new-releases and some user endpoints were removed, and search `limit` is capped at 10.
- Extended quota (a public app for everyone) is only granted to **registered organisations with ≥ 250k monthly active users** (since May 2025).
- July 2026: up to 25 Client IDs per developer account; the quota is shared per developer account; a `429` with `"reason": "QUOTA_EXCEEDED"` distinguishes quota from rate limits.
- Playback-control endpoints require the **user** to have Premium.
- **Consequence:** AxePower cannot ship one shared Client ID for all users. **Each user brings their own Client ID** (they are the owner and only user of their own dev-mode app).

### User setup (shown as a step-by-step card in the Spotify panel)
1. Have Spotify Premium.
2. Go to `developer.spotify.com/dashboard` → Create app → name "AxePower (personal)".
3. Redirect URI: **`http://127.0.0.1:43821/callback`** (loopback IP literal, not `localhost`). APIs: Web API.
4. Copy the **Client ID** into AxePower → Settings → Music → Spotify → Log in.

### Implementation
- **`SpotifyAuthService`:** Authorization Code with **PKCE** (no client secret).
  - `HttpListener` on `http://127.0.0.1:43821/callback/`, started only during login, with a 3-minute timeout
  - verify the `state` parameter; open the system browser to the authorize URL
  - scopes: `user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative user-library-read`
  - store the refresh token in `ISecretStore` (`spotify.refresh`); handle refresh-token rotation (save the new one when returned)
  - "Log out" deletes it.
- **`SpotifyApiClient`:** `HttpClient` with bearer auth and automatic refresh on 401.
  - honour `Retry-After` on 429; show "Spotify quota reached — try later" on `QUOTA_EXCEEDED`
  - map 403 `PREMIUM_REQUIRED` to "Spotify Premium is required to control playback"
  - **before implementing each endpoint, confirm it still exists for Development Mode apps in the current Web API reference.**
- **Endpoints (verify at build time):**
  - `GET /me/player`, `GET /me/player/devices`
  - `PUT /me/player` (transfer)
  - `PUT /me/player/play` (with `context_uri` / `uris`, optional `device_id`), `PUT /me/player/pause`
  - `POST /me/player/next`, `POST /me/player/previous`
  - `PUT /me/player/seek`, `PUT /me/player/volume`, `PUT /me/player/shuffle`, `PUT /me/player/repeat`
  - `GET /me/playlists`, `GET /playlists/{id}/items` (or the current equivalent), `GET /me/tracks` (Liked Songs, if still available).
- **`SpotifyConnectService`:** polls `GET /me/player` every 2 s while the Spotify panel is visible or Spotify is the active source; every 10 s otherwise while logged in; not at all when logged out.
  - It feeds a `MusicSource.Spotify` snapshot into the same display priority as §17. When the Spotify desktop app is running, the SMTC session and this snapshot may describe the same playback: prefer the Spotify API snapshot and hide the duplicate SMTC session.
- **UI (`SpotifyPanel`, a "Spotify" tab on the Music page, only when enabled):**
  - device picker, now playing, controls, the user's playlists → "Play on <device>"
  - a clear banner: "Audio plays on your Spotify device, not inside AxePower."

---

## 25. Apple Music: what is and is not possible
**Not built.** Reasons:
- **No SDK:** Apple provides **no MusicKit SDK for Windows desktop apps.**
- **MusicKit on the Web** would need all of the following:
  - an **Apple Developer Program** membership (paid, yearly)
  - a **developer token** (JWT signed with a MusicKit private key). The key must never ship inside the app, so a server is needed to mint tokens
  - an **Apple Music subscription** for the user
  - DRM playback inside WebView2. WebView2 supports PlayReady but not Widevine, and whether MusicKit web playback works in WebView2 is **unverified**; it would need a prototype.
- **Audio could still not pass through AxePower's engine** (DRM).

**What users get instead:** the **universal media controller** ([§17](#17-universal-media-controller)) controls the **Apple Music app for Windows** (and iTunes) through Windows media sessions: now playing, play/pause, next/previous and seek where the app allows it. Verify with the Apple Music app during Phase 3 and record the result in the phase report.

---

## 26. Build phases with checklists

Every phase ends with the verification loop in [§0](#0-how-to-use-this-document-read-first), a commit and a completion report.

### Phase 0 — Foundation
- [ ] Branch `feature/music-player`; confirm `git status`, leave unrelated changes alone.
- [ ] Change the target framework in all three projects (§6.3) and update every `net8.0-windows\` publish path (§4). Verify `dotnet publish` output and Inno Setup script paths.
- [ ] Add Phase 0–1 packages (§6.2). Create `THIRD_PARTY_NOTICES.md`; add it to both `.iss` `[Files]` sections.
- [ ] Create `MusicSettings` (all properties in §12, including later-phase ones, since settings are harmless data) + `AppSettings.Music` + `Clone()` update.
- [ ] Add `PowerPlanService.ModeApplied` and `AppMonitorService.TrackedAppStarted/Stopped` events (no behaviour change).
- [ ] Create the folders and empty interfaces from §6.1/§7; register what exists in DI; extend `RunSelfTest`.
- [ ] Tests: `MusicSettingsTests` (defaults, deep clone, JSON without a `music` key loads defaults), the DI test resolves the new services, `ModeApplied` raised only on verified success (fake-friendly; if `PowerPlanService` is not testable without touching real power plans, test only through a seam and never apply a real scheme).
- **Exit:** app launches, looks and behaves exactly as before; all builds and tests green; publish and installer paths correct.

### Phase 1 — Playback core and minimal Music page
- [ ] `DecoderFactory`, pipeline stages needed now (`FormatAdapter`, `GaplessSourceChain`, `VolumeStage`, `SoftLimiter`, `PcmOutputConverter`), `OutputFactory` (shared only), `NAudioEngine`.
- [ ] `PlaybackQueue` (all §9 rules except Mix), `MusicPlayerService`, `PlaybackStateStore`.
- [ ] `PlayerIntegrationHost`, `SmtcIntegration`, `TaskbarMediaIntegration`, `MusicDispatcher`.
- [ ] `ITagReader` read path (needed for titles/artists/covers of opened files); `CoverArtService` embedded + folder covers with cache.
- [ ] UI: sidebar "Music" item + navigation; `MusicView` with Now Playing bar, queue drawer, "Open files…", "Open folder…", elevated drag-and-drop via `FileDropHelper`, empty state; `NowPlayingViewModel` singleton.
- [ ] Volume boost setting (Settings → Music → Playback: "Allow volume boost up to 200%").
- [ ] Startup and shutdown wiring (§6.5); resume state paused.
- [ ] Tests: `PlaybackQueueTests`, `PlaybackStateStoreTests`, `DecoderFactoryTests`, `EnginePipelineTests` (gapless sample count, resampling, volume curve, limiter bound), `MusicPlayerServiceTests` with a fake engine.
- **Exit:**
  - two FLACs in a row play with no audible gap
  - media keys and the Windows media flyout control playback; the taskbar buttons work
  - after quit and relaunch the same queue and position are restored paused
  - drag-and-drop works while running as admin.

### Phase 2 — Library
- [ ] `MusicLibraryRepository` (schema §10.1 + FTS), `LibraryScanner`, `LibraryWatcher`, `ArtistGenreSplitter`, `MusicLibraryService`.
- [ ] `PlaylistService`, `M3uSerializer`, `HistoryIntegration`, Liked / Recently played.
- [ ] UI tabs: Tracks, Albums (`VirtualizingWrapPanel`), Artists, Genres, Folders, Playlists; search; sort; context menus; scan progress and cancel; first-run folder card; Settings → Library group.
- [ ] Tests: repository (temp DB file), scanner (generated WAVs with TagLib-written tags: add/update/remove/min size/cancel/unreadable tags), splitter, M3U round-trip, FTS search, corrupt DB recovery.
- **Exit:**
  - scanning a 10,000-track library shows live progress, can be cancelled, and a rescan of an unchanged library finishes quickly (report the measured time)
  - scrolling the Tracks and Albums tabs stays smooth; memory stays bounded (report the measured working set before and after browsing).

### Phase 3 — AxePower surfaces
- [ ] `MiniPlayerWindow` + `MiniPlayerService` (compact/expanded, snap, per-monitor position, opacity, click-through with escapes), `HotkeyService` (`Ctrl+Shift+M`, conflict reporting).
- [ ] Tray "Now Playing" section + tooltip; Dashboard `NowPlayingCard`.
- [ ] `UniversalMediaService` + display priority (§17); mini player, tray and dashboard show external media.
- [ ] Accent from artwork (`MusicAccentBrush`, contrast check).
- [ ] LAN portal security fix (§18.1) **first**, then `MusicRemoteApi` + routes + portal Now Playing card (§18.2).
- [ ] Settings → Mini player + Phone remote groups.
- [ ] Tests: `MusicRemoteApiTests` (401 without or with a wrong token, state JSON shape, cover only by current item, limits, bad actions → 400), updated `LanDropServiceTests`, display-priority tests with fakes, hotkey string parsing.
- **Exit:**
  - the mini player never steals focus from a focused text editor or game
  - the mini player controls the Spotify desktop app and a YouTube tab in a browser
  - the phone shows live now playing and can seek and change volume
  - requests without a token get 401.

### Phase 4 — Automations and devices
- [ ] `AudioDeviceService` + output picker + device-removed pause + default-device follow.
- [ ] `MusicAutomationService`: game duck/pause (app-rule events + fullscreen detection), power profiles via `ModeApplied`, low-battery rule.
- [ ] `SleepTimerService` + UI in the bar, mini player and tray; `SystemPowerActions` with the 30 s warning.
- [ ] Settings → Audio output + Automations groups.
- [ ] Tests (fakes only): duck and restore; user-paused-during-game not resumed; flapping protection; power profile switching applies at a safe point; sleep timer fires fade and action exactly once, Cancel prevents it; low-battery pause once.
- **Exit:**
  - launching a fullscreen game (or a rule app) ducks music within ~10 s and restores after exit
  - unplugging headphones pauses playback
  - the sleep timer's Sleep action works on a real machine (manual check only).

### Phase 5 — Audiophile features
- [ ] Exclusive output negotiation + rate matching + honest fallback; `ReplayGainStage`; `CrossfadeMixer`; `TempoPitchStage` (SoundTouch.Net); `SpectrumTap` + visualizer control; format/quality badge.
- [ ] Settings → Playback + Audio output (exclusive) completed; speed and pitch controls in the queue drawer's "⋯" menu with a Reset.
- [ ] Tests: ReplayGain math incl. prevent-clipping; crossfade equal-power sum within tolerance; pass-through detection (`BitPerfect` true only with neutral DSP + exclusive + native rate); tempo stage length ratio; spectrum returns 32 bars in 0–1.
- **Exit:**
  - with a USB DAC, exclusive mode switches the DAC's sample rate per track (check the DAC display or Windows device properties)
  - the badge says "Bit-perfect" only when §7's condition holds
  - crossfade is smooth; albums with "skip same album" stay gapless.

### Phase 6 — Extras
- [ ] Lyrics (§19), tag editor (§21), Discord (§20), Last.fm (§20), Mix (§22), Most played, track-change notification, Open with + pipe protocol (§23), `DpapiSecretStore`.
- [ ] Settings → Online features group with "What is sent" lines, all off by default.
- [ ] Tests: `LrcParserTests`, LRCLIB client with a fake `HttpMessageHandler` (hit, 404 negative cache, timeout), Last.fm signature generation + scrobble rules + offline queue, Mix selection determinism, pipe message parsing and validation, tag edit backup and undo on temp files, secret store round-trip.
- **Exit:** every online feature makes **zero** network calls until switched on (verify with a fake handler and a manual check with a network monitor).

### Phase 7 — Spotify Connect remote (optional; skip if the owner says so)
- [ ] §24 in full, behind `Integrations.SpotifyEnabled` (off by default).
- [ ] Tests: PKCE verifier/challenge, state mismatch rejected, token refresh + rotation, 429/`QUOTA_EXCEEDED`/`PREMIUM_REQUIRED` mapping, duplicate-session suppression.
- **Exit:** with a Premium account and the user's own Client ID, the playlist starts on a chosen device, device transfer works, and controls work.

### Phase R — Release (see §29)

---

## 27. Automated test plan

Put all tests in `AxePower.Tests/Music/`. Rules:
- **Fakes only.** Never touch real power plans, never sleep or hibernate the PC, never write to the real `%LOCALAPPDATA%\AxePower` (inject temp directories), never open real audio devices (use a fake output that *pulls* samples).
- **`TestAudio` helper** generates WAV files in code: sine 440 Hz, 1 s, at 44.1 kHz and 48 kHz, 16/24-bit, mono and stereo. Write tags with TagLib# into copies. **No binary audio fixtures in the repo.**

| Test class | Key cases |
|---|---|
| `MusicSettingsTests` | defaults; `Clone` deep copy (mutating a clone's lists doesn't affect the original); JSON without `music` → defaults; round-trip |
| `PlaybackQueueTests` | open/jump; next/prev incl. 3 s rule; repeat off/all/one at both ends; shuffle keeps current, unshuffle restores order incl. items added or removed while shuffled; insert/remove/move index maths incl. removing the current item and the last item; cap at 10,000; duplicates |
| `MusicPlayerServiceTests` | engine calls sequence (fake engine); `SetNext` updated after each change; 3 consecutive failures → paused with error; state events raised once per change |
| `PlaybackStateStoreTests` | atomic write; corrupt file → defaults; missing files dropped on restore |
| `DecoderFactoryTests` | extension mapping; Ogg sniffing (Opus vs Vorbis header bytes); unsupported → exception with the extension |
| `EnginePipelineTests` | gapless: output sample count = sum of inputs; resampling 44.1→48 kHz length ratio; volume curve; limiter never exceeds ±1.0 with boost 200%; neutral stages are bit-identical pass-through |
| `ReplayGainTests` | track/album/auto selection; preamp; prevent clipping with peak; fallback |
| `CrossfadeTests` | equal-power: a² + b² ≈ 1 across the fade; skip same album; short tracks not crossfaded |
| `LibraryScannerTests` | add/update/remove; min size; unreadable tags → filename title; cancellation leaves DB consistent; hidden files skipped |
| `MusicLibraryRepositoryTests` | migrations from empty; FTS prefix search; parameterised queries (search text with quotes); corrupt DB → rebuilt |
| `ArtistGenreSplitterTests` | `;` `/` feat./ft./featuring; `&`/`,` only when enabled; dedupe; trimming |
| `M3uSerializerTests` | relative/absolute; BOM; `#EXTINF`; export round-trip |
| `CoverArtServiceTests` | folder-image priority; cache key stability; dominant colour picks the saturated colour, returns null for grey |
| `MusicAutomationTests` | duck/restore; pause/resume only if we paused; flapping guard; power profile switching; low battery once |
| `SleepTimerTests` | minutes (fake clock), end of track, end of queue, fade then action once, cancel, hibernate unavailable |
| `UniversalMediaDisplayPriorityTests` | the §17 ordering; self-session filter |
| `MusicRemoteApiTests` | token required on all `/api/*`; constant-time compare used; JSON shapes; limits; `400` on bad input; covers only for the current item |
| `LanDropServiceTests` (update) | existing behaviour with a valid token; 401 without; no `Access-Control-Allow-Origin: *` |
| `LrcParserTests` (P6) | formats, multi-timestamps, offset, metadata ignored, word tags stripped, sorting |
| `LyricsServiceTests` (P6) | source order; online only when enabled; negative cache 7 days; no network when disabled |
| `LastFmTests` (P6) | signature; scrobble threshold; queue persistence and batching |
| `SingleInstanceMessageTests` (P6) | byte 1 legacy; byte 2 + JSON; size and path validation |
| `TagEditTests` (P6) | backup created; undo restores; read-only refused |
| `SpotifyAuthTests` (P7) | PKCE; state mismatch; refresh rotation; error mapping |
| `DependencyInjectionTests` (update) | every new service and view model resolves |

---

## 28. Manual verification checklist

Run before each phase commit where relevant, and fully before release.

- [ ] Fresh profile (rename `%LOCALAPPDATA%\AxePower` temporarily): first-run card, add folder, scan, play.
- [ ] Formats: MP3, M4A (AAC), M4A (ALAC), FLAC 16/44.1 and 24/96, WAV, AIFF, WMA, OGG, OPUS. APE shows "Unsupported format".
- [ ] Gapless album (a live or classical album) in shared mode.
- [ ] Media keys, the Windows media flyout (Win+A / volume flyout), taskbar thumbnail buttons.
- [ ] Running elevated: drag files from Explorer into the Music page and the queue.
- [ ] Mini player over Notepad (no focus steal), over a borderless game, snap to corners, two monitors, remove the second monitor.
- [ ] Tray section and tooltip; Dashboard card at 1024×760 and 1280×840.
- [ ] Themes: System/Dark/Light/Glass; scaling 125/150/200%; keyboard-only through the Music page; Narrator reads button names.
- [ ] `ReduceMotion` on: no animations, no visualizer.
- [ ] Unplug headphones while playing → pause + notification.
- [ ] Switch Eco ↔ Beast while playing → no glitch mid-song; latency changes at the next track.
- [ ] Game with a Game Mode rule, and a fullscreen game → duck and restore.
- [ ] Sleep timer 1 min + Sleep PC (30 s warning, cancel works; then let it sleep once).
- [ ] Phone: scan the QR, now playing updates, seek, volume, search, play a playlist. `curl` without a token → 401.
- [ ] Exclusive mode with a USB DAC; the badge is honest; another app's sound is blocked while exclusive (expected, as explained in Settings).
- [ ] Online features off → a network monitor (e.g. Resource Monitor) shows no connections from music features.
- [ ] Publish + installer build + install on a clean Windows 10 1809+ VM or machine.

---

## 29. Release and documentation checklist
- [ ] Version `1.9.0` in `AxePower.App.csproj` and `AxePower.Core.csproj` (all four version properties).
- [ ] `README.md`:
  - Key Features entry for AxePower Music; Music row in the navigation table
  - architecture diagram (a MUSIC subgraph)
  - publish path updated
  - Third-Party Credits: NAudio, NVorbis/NAudio.Vorbis, Concentus, TagLib#, SQLite/Microsoft.Data.Sqlite, SoundTouch.Net, DiscordRichPresence, VirtualizingWrapPanel, LRCLIB, plus "Feature set inspired by Harmonoid (no code used)"
  - privacy line reworded to: "Zero telemetry. Optional, off-by-default integrations (Last.fm, Discord, online lyrics, Spotify) only send data when you turn them on."
  - fix the inaccurate "SettingsService (Encrypted JSON)" wording.
- [ ] `AboutView.xaml` credits updated to match.
- [ ] `THIRD_PARTY_NOTICES.md` complete (license texts for LGPL components) and installed.
- [ ] `RELEASE_README.md` and patch notes: features, the **LAN portal token change** (rescan the QR once), and known limitations:
  - no APE/DSD
  - mini player not over exclusive-fullscreen games
  - Spotify requires your own Client ID + Premium
  - no Apple Music login.
- [ ] `idea.txt`: move #10 to "Completed" and list precisely what shipped and what did not (streaming audio, ASIO, DSD).
- [ ] `WORK_LOG.md`: a v1.9.0 entry, including the measured performance numbers from Phases 2 and 4.
- [ ] Screenshots: real ones only (release-discipline rule).
- [ ] `dotnet test -c Release` green; Debug and Release builds; `--self-test`; publish; Inno Setup compile; install test.
- [ ] **Ask the owner** which publication steps to perform (repo root file `commit command`): README release commit to `AxePower_Download`, website update (`AppWebsite/`), Discord post. **No push without approval.**
- [ ] Label the build as unsigned if no code-signing certificate is present.

---

## 30. Never do / out of scope
- ❌ Copy, port or open Harmonoid source, or use its assets or branding (§2).
- ❌ Play, capture, record or re-route audio from Spotify, Apple Music, Tidal, YouTube Music or any DRM service. Never use librespot or similar unofficial clients.
- ❌ Delete, move or rename user audio files (only tag edits, with backup and undo).
- ❌ Change the Windows master volume, other apps' volume, or default-app associations.
- ❌ Send any data over the network unless the specific opt-in setting is on. No telemetry or analytics.
- ❌ Log secrets, tokens or full personal file paths in exported diagnostics.
- ❌ Show fake metrics (battery savings, "Hi-Res" or "Bit-perfect" when it isn't, RAM figures not measured).
- ❌ Show UI for features that don't work yet.
- ❌ Add ASIO output, DSD/APE decoding, lyrics translation, artist images or online "similar artists" in this release.
- ❌ Push, merge or publish without the owner's explicit approval.

---

## 31. Known risks

| Risk | Mitigation |
|---|---|
| Framework change breaks something unrelated (WinRT projection, build paths) | Phase 0 is only this change + settings; full build, tests, publish and installer verification before any feature work |
| Publish size grows (`Microsoft.Windows.SDK.NET.dll`) | Accepted (D2). Record the before/after installer size in `WORK_LOG.md` |
| `SystemMediaTransportControlsInterop` not available in the projection | Use the documented `MediaPlayer` + `CommandManager.IsEnabled = false` fallback (§15) |
| Media Foundation COM apartment issues | Decoders only on MTA/background threads (§6.6) |
| Sidebar height at small screens | One button only (D3); verify at `MinHeight`; bump `MinHeight` by one item if required |
| LAN token change confuses existing phone users | Patch-notes line + Phone Control UI copy: "Scan the QR again after updating" |
| Exclusive mode blocks all other sound | Off by default; Settings explains it; honest fallback reasons |
| Large libraries and memory | Virtualized lists, cover cache with decode size, LRU of 300 bitmaps, measure in Phase 2 |
| Spotify changes its developer rules again | Phase 7 is optional and isolated; endpoints verified at build time; BYO Client ID |
| Global hotkey conflicts | `RegisterHotKey` failure shown in Settings with a prompt to choose another |
