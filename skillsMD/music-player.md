---
name: music-player
description: AxePower Music (Phase 1) — NAudio engine with gapless playback on a dedicated MTA worker, queue rules, session restore, Windows media controls (SMTC) and taskbar buttons, the glass title-bar player toggled by "Music ▾", the Music page, and elevated-safe file drop.
---

# AxePower Music (Phase 1: playback core)

A built-in local music player. Phase 1 plays files and folders you open or drop; the library (albums, artists, playlists, search) arrives in Phase 2. The full build plan is [MUSIC_PLAYER_PLAN.md](MUSIC_PLAYER_PLAN.md).

## Where it lives

| Piece | File |
|---|---|
| Engine (decoders, pipeline, WASAPI) | [AxePower.Core/Services/Music/Engine/](../AxePower.Core/Services/Music/Engine/) |
| Queue rules (pure logic) | [PlaybackQueue.cs](../AxePower.Core/Services/Music/PlaybackQueue.cs) |
| Player (drives the engine) | [MusicPlayerService.cs](../AxePower.Core/Services/Music/MusicPlayerService.cs) |
| Session file | [PlaybackStateStore.cs](../AxePower.Core/Services/Music/PlaybackStateStore.cs) |
| Tags, covers, loose files | [Library/](../AxePower.Core/Services/Music/Library/) |
| Media keys / Windows flyout | [SmtcIntegration.cs](../AxePower.Core/Services/Music/Integrations/SmtcIntegration.cs) |
| Taskbar buttons + progress | [TaskbarMediaIntegration.cs](../AxePower.App/Services/TaskbarMediaIntegration.cs) |
| Shared UI state | [NowPlayingViewModel.cs](../AxePower.App/ViewModels/Music/NowPlayingViewModel.cs) (singleton) |
| Title-bar player | [HeaderPlayer.xaml](../AxePower.App/Views/Music/HeaderPlayer.xaml) |
| Music page | [MusicView.xaml](../AxePower.App/Views/Music/MusicView.xaml) |
| Styles / theme brushes | [MusicStyles.xaml](../AxePower.App/Resources/MusicStyles.xaml), `Music*Brush` in each theme |
| Settings model | [MusicSettings.cs](../AxePower.Core/Models/Music/MusicSettings.cs) (`AppSettings.Music`) |

## How playback works

```
DecoderFactory (MF / WAV / AIFF / Vorbis / Opus)
  → FormatAdapter (channel map, then WDL resample to the device mix format)
  → PrerollSampleProvider (0.5 s decoded ahead on the worker)
  → GaplessSourceChain (joins the next track inside the same buffer; silence when idle)
  → VolumeStage (volume³ curve, boost to 2×, duck/sleep fade, click-free transport fade)
  → SoftLimiter (only when gain > 0 dB; hard clamp at ±1)
  → PcmOutputConverter (float copied bit for bit; int with clamp/dither)
  → WasapiOut (shared, event-driven, 60 ms)
```

- **Threading:** every decoder and COM object lives on one MTA `EngineWorker` thread. WASAPI pulls samples on its render thread; events go out in order through a `SerialEventQueue`, never on the render thread.
- **Lazy device:** the device opens on the first Play, so restoring a paused session at startup creates no audio session.
- **Gapless:** within 10 s of the end, the engine opens and pre-rolls whatever the player passed to `SetNext`. Repeat One passes the same track under a new key.
- **Failures:** a track that can't be opened is skipped with "Couldn't play '…' — skipped". Three in a row pauses with an error.
- **Device loss:** the player pauses ("Output device disconnected — music paused."), and the next Play rebuilds on the default device at the same position.

## Surfaces
- **Sidebar → Music:** the Now Playing card, the queue drawer (double-click / Enter to play, Del to remove, Alt+↑/↓ or drag to reorder), Open files / Open folder, and an empty state.
- **Title bar "Music ▾":** opens and collapses the glass player; the state is saved in `music.headerPlayerExpanded`. The player sits beside the window controls in the tall Dashboard header and drops down below them elsewhere (`MainWindow.PositionHeaderPlayer`). It is hidden on the Music page and in the shrunk widget.
- **Windows:** media keys, the volume/media flyout and the lock screen, through SMTC `GetForWindow`; thumbnail buttons and progress on the taskbar icon.
- **Keyboard (Music page):** Space play/pause, Ctrl+←/→ previous/next, Ctrl+↑/↓ volume ±5 %.

## Files it writes
- `%LOCALAPPDATA%\AxePower\Music\playback-state.json`: queue paths, index, position, volume, shuffle and repeat (atomic write + `.backup`).
- `%LOCALAPPDATA%\AxePower\Music\covers\{sha1}_{128|256|512}.jpg`: resized cover cache.

## Gotchas
- **App-wide TextBlock style:** `Styles.xaml` has an implicit TextBlock style that overrides fonts. Icon buttons must render glyphs with an explicit TextBlock (see `MusicIconButtonStyle`), or they show empty boxes.
- **ToggleButtons bind TwoWay:** the view model's setters call the player and an `_applying` guard stops echoes. A OneWay binding breaks on the first click.
- **Drag-and-drop uses WM_DROPFILES:** `FileDropHelper` uses WM_DROPFILES and revokes the OLE drop target while the Music page is shown. Don't add WPF `AllowDrop` there; UIPI blocks OLE drops into an elevated window.
- **Settings page copy:** `SettingsViewModel.SaveCurrentSettings` must copy `Music` from the latest settings, or the Settings page's stale copy reverts music changes.

## Tests
[AxePower.Tests/Music/](../AxePower.Tests/Music/): queue rules, state store, decoder mapping, pipeline (gapless sample count, resampling, volume curve, limiter bound, bit-identical pass-through), player logic with a fake engine, and real-container registration. The tests never open an audio device.
