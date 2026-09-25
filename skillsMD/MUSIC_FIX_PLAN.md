---
name: music-fix-plan
description: Live list of open issues and unverified work in AxePower Music and the LAN portal. Read at the start of a session that touches either; update it before finishing.
---

# AxePower Music — Fix Plan & Verification Ledger

> **Branch:** `v2.0.6` · **Last updated:** 2026-09-25 · **Last commit covered:** the v2.0.6 release commit (music rework, recent searches, lyrics fixes, library folders, session restore; M14 noted)

This file is the running list of what is **not** finished or **not** proven in AxePower Music
and the LAN portal it shares with Phone Hub. It exists because a lot of this work compiles and
passes tests while never having been executed against the real thing — UI that has not been
rendered, sign-in flows that have not been run, hotkeys that have not been registered.

For what the feature *is meant to be*, read [MUSIC_PLAYER_PLAN.md](MUSIC_PLAYER_PLAN.md).
For what it currently *does*, read [music-player.md](music-player.md).
This file is only for the gap between them.

---

## How to use this file

**At the start of any session that touches Music, the providers, or the LAN portal:**

1. Read this file first. Everything under [Open](#open) is unproven or broken.
2. Do not re-raise something already listed. Add to the entry instead.

**Before finishing that session:**

3. **Delete** every entry that is now fixed and verified. Do not tick it, strike it through or
   move it to a "done" section — remove it. The commit that fixed it is the record, and a file
   that only ever grows stops being read.
4. **Add** every new issue found, including ones found and fixed in the same session if the fix
   is partial. Use the entry shape below.
5. Update the header's date and last-covered commit.
6. An entry that turned out not to be a problem is deleted too, with one line in the commit
   message saying why.

**Rules for what belongs here**

- Anything that has never actually run counts as open, even if it builds and its tests pass.
  "Tested with fakes" is not verification of the real path.
- A known behaviour change that will surprise a user belongs in
  [Behaviour changes](#behaviour-changes-to-tell-the-user-about) until it has been released
  and mentioned.
- Things the plan simply has not reached yet belong in [Not built yet](#not-built-yet), not in
  Open. Open means it exists and is untrustworthy.

**Entry shape**

```
### <id> — <one line, what is wrong or unproven>
**Where:** file/path or feature area
**Why it matters:** the consequence if it is broken, in a sentence
**How to verify:** the exact thing someone has to do to close it
**Found:** date / commit
```

Ids are `M<n>` for Music, `P<n>` for the portal, and are never reused.

---

## Open

### M1 — No part of the Music UI has ever been rendered
**Where:** [MusicView.xaml](../AxePower.App/Views/Music/MusicView.xaml), [AmbientModeOverlay.xaml](../AxePower.App/Views/Music/AmbientModeOverlay.xaml), [DashboardViewModel.cs](../AxePower.App/ViewModels/DashboardViewModel.cs)
**Why it matters:** This is the largest untested surface in the feature. The Music Settings
dialog grew from one 480px panel to a 640px scrolling dialog with five sections (Sources,
streamed audio, Discovery, Shortcuts, Control other players). None of it, nor the Discovery
toggle added to the transport row, the "Play from" submenu on queue
rows, nor the four new What's New cards, has been seen. The 2026-09-25 page rework is also unseen:
the compact Now Playing card (112px art, transport and volume on one row, the "Up next" line and
volume percentage label removed — volume shows in the tooltip), the YT Music / Local Music header
buttons that replaced the lower tabs, the icon-only Ambient button, the Local-only Create Playlist /
Add Folder toolbar, the Last played list, and the new Ambient Mode section in Music Settings.
Binding errors and layout breakage would not show up in any test; XAML compiles fine with a
misspelled binding path.
**How to verify:** Launch the app (it relaunches elevated, so this has to be done by the owner),
open Music → settings, scroll the whole dialog, right-click a streamed queue row,
enter Ambient Mode with F11, and check the Dashboard's What's New dropdown. Switch between the YT
Music and Local Music header buttons and check the active one is lit, the cards and toolbar vanish
in YT Music, and the transport row does not clip at the narrowest window width (the left column's
MinWidth is 420). Resize the window and compare against `ui reference/`.
**Found:** 2026-09-23 / `3689ff4`–`141ebca`

### M3 — Global hotkeys have never been registered against Windows
**Where:** [HotkeyService.cs](../AxePower.App/Services/HotkeyService.cs)
**Why it matters:** `RegisterHotKey` needs a real window with a running message loop. The
self-test builds `MainWindow` but never shows it, so registration, the `WM_HOTKEY` hook and the
conflict path have not run once. Windows refuses a taken hotkey silently; the honest reporting
built for that has never been exercised against a real competing application.
**How to verify:** Turn on Music Settings → Shortcuts → "Work outside AxePower", then use
play/pause and next from another focused application. Bind something already owned by another
app (Win+arrow keys, or whatever the owner has running) and confirm Settings names the clash
instead of failing quietly. Rebind, reset one, reset all.
**Found:** 2026-09-23 / `9708096`

### M4 — Discovery recommendation quality is unknown
**Where:** `GetRecommendationsAsync` and `Queries` in [YouTubeProvider.cs](../AxePower.Core/Services/Music/Providers/YouTubeProvider.cs)
**Why it matters:** The search phrasings (`"<artist> songs"`, `"artists similar to <artist>"`,
`"<artist> radio mix"`) are a guess at what returns decent results, tested only against a fake
provider. If they return compilations, hour-long mixes or the same artist repeatedly, the
feature works mechanically and is useless in practice. The variety slider's three bands are
likewise untuned.
**How to verify:** Queue a few tracks, turn Discovery on, let it extend the queue, and look at
what it added at variety 0, 50 and 100. Judge whether the additions are plausible.
**Found:** 2026-09-23 / `9708096`

### M5 — Stream expiry and re-resolution never seen with a genuinely expired URL
**Where:** `PrepareAsync` / `RefreshAsync` in [StreamResolver.cs](../AxePower.Core/Services/Music/Providers/StreamResolver.cs), `OnEngineErrorAsync` in [MusicPlayerService.cs](../AxePower.Core/Services/Music/MusicPlayerService.cs)
**Why it matters:** Expiry is tested with a fake clock. A real signed YouTube URL that has aged
past the hour may fail differently in production. The player now avoids resolving queued remote
tracks early and refreshes the current candidate once when open/decode reports an error, including
an asynchronous 403, before downloading or skipping. That recovery is covered with fakes but has
not been observed against a genuinely expired YouTube URL.
**How to verify:** Resolve a streamed track, leave the queue untouched for over an hour, then
play it. It should re-resolve and play rather than error.
**Found:** 2026-09-23 / `3689ff4`

### M7 — Kugou, YouTube captions and artwork-driven Ambient colors are not live-verified
**Where:** [LrcLibLyricsService.cs](../AxePower.Core/Services/Music/Lyrics/LrcLibLyricsService.cs), [MusicPaletteExtractor.cs](../AxePower.App/Services/MusicPaletteExtractor.cs), [AmbientModeOverlay.xaml](../AxePower.App/Views/Music/AmbientModeOverlay.xaml)
**Why it matters:** LRCLIB is now proven live (2026-09-25): until then every lookup had failed,
because the exact endpoint answers 400 without an artist and that exception ended the lookup
before the search ran. With the fix, two of the owner's three real Punjabi uploads got synced
lyrics from the live service; the third ("Nakhre", Mankirt Aulakh) is not in LRCLIB and was
correctly not matched to another artist's song. Kugou and the YouTube caption fallback have
still only run against fakes, as have palette selection, real embedded covers and GPU/render
behaviour.
**How to verify:** Play a YouTube song LRCLIB does not have (e.g. "Nakhre" by Mankirt Aulakh) and
confirm Kugou or the video's captions supply lyrics, click lines to seek, then enter Ambient Mode
and confirm each cover produces readable, non-black glows without animation or scrolling stalls.
**Found:** 2026-09-25 / working tree after `4a004bb`

### M8 — Full screen Ambient window and its screensaver mode have never run
**Where:** [AmbientWindowService.cs](../AxePower.App/Services/AmbientWindowService.cs), [AmbientModeViewModel.cs](../AxePower.App/ViewModels/Music/AmbientModeViewModel.cs)
**Why it matters:** Ambient Mode is now its own borderless, topmost window instead of an overlay
on the Music page. Covering the taskbar relies on maximizing a borderless window on the monitor
AxePower is on; a wrong monitor, a visible taskbar or a window that never closes would all pass
the build. The screensaver path (idle check every 5 s with `GetLastInputInfo`, skipped when
`SHQueryUserNotificationState` reports a full screen game, video or presentation) has no test at
all. Gamepad input does not reset Windows' idle time, so it may still open over a windowed game
played with a controller.
**How to verify:** Press F11 on the Music page on each monitor: it must cover the whole screen,
taskbar included; Esc, F11, the close button and Space (play/pause) must work, and closing must
return to AxePower. Turn on Music Settings → Ambient Mode → "Open automatically", pick 1 minute,
play a song and leave the PC alone: it must open, and a small mouse move, a click or a key must
close it without bringing AxePower forward. Repeat with a full screen game or video playing: it
must not open.
**Found:** 2026-09-25 / working tree after `7822d50`

### M9 — Last played history is only tested with fake player states
**Where:** [RecentlyPlayedService.cs](../AxePower.Core/Services/Music/RecentlyPlayedService.cs), `LoadRecentPlays` / `PlayRecent` in [MusicViewModel.cs](../AxePower.App/ViewModels/Music/MusicViewModel.cs)
**Why it matters:** A song is recorded the first time its queue entry reports `Playing`. That is
unit tested with hand-built `PlayerState`s, not the real player, so a buffering or error state that
briefly reports playing could record songs that never played. Replaying a YouTube row rebuilds the
track from its provider id only (no candidates), relying on the resolver to search again, which
has not been exercised from this list.
Partial evidence (2026-09-25): the owner's running build recorded 3 local and 7 streamed plays
in recently-played.json, so recording from real playback happens for both kinds; replaying a row
and the list surviving a restart are unchecked.
**How to verify:** Play a few local songs and a few YouTube songs, restart AxePower, and check
each mode's Last played list shows its own songs newest first. Click a YouTube row and confirm it
resolves and plays; right-click → Add to queue on a local row.
**Found:** 2026-09-25 / working tree after `7822d50`

### M10 — Recent YouTube Music searches dropdown: seen open once, behaviour since changed
**Where:** [RecentSearchesService.cs](../AxePower.Core/Services/Music/RecentSearchesService.cs), recent-search handlers in [MusicView.xaml.cs](../AxePower.App/Views/Music/MusicView.xaml.cs), `ShowRecentSearches` / `OnSearchYouTubeTextChanged` / `OpenRecentSearch` in [MusicViewModel.cs](../AxePower.App/ViewModels/Music/MusicViewModel.cs)
**Why it matters:** The owner's screenshot (2026-09-25) showed the list open, but see-through on
their skin, and reopening on click was unreliable. It is now `StaysOpen="True"`, closed by the
view (click elsewhere, Tab away, Esc, choosing a search, window deactivated or moved), drawn on
the new opaque `PopupBackgroundBrush`, opens on every click with the full history, and filters to
matching searches while typing. None of that has been seen. The saved lists are rebuilt without
stream candidates, so playing one searches again for audio (the same path as M9).
Partial evidence (2026-09-25): the owner's build saved 2 real searches to recent-searches.json, so
saving works from real use; the dropdown's behaviour is still unseen.
**How to verify:** Click the search box repeatedly: the full list must show every time. Type part
of an earlier search: only matches remain; type something unmatched: the list closes. Click the
page, Alt+Tab away and move the window: it must close each time. On a glass skin nothing behind
it may show through. Down enters the list, Enter picks, Esc returns to the box; × forgets one.
**Found:** 2026-09-25 / working tree after `d574fd8`

### M11 — Lyric timing in Ambient Mode has not been watched against the music
**Where:** `EstimatedPosition` / `UpdateLineTimer` in [LyricsViewModel.cs](../AxePower.App/ViewModels/Music/LyricsViewModel.cs), [AmbientModeOverlay.xaml.cs](../AxePower.App/Views/Music/AmbientModeOverlay.xaml.cs)
**Why it matters:** The player reports its position every 250 ms, so lines used to change up to a
quarter second late. Lyrics now carry the position forward between reports on a 40 ms clock
(capped at 1 s, scaled by playback speed) and centre the sung line as soon as Ambient opens.
None of that has been seen. Two things it cannot fix are also unmeasured: audio device latency
(the engine position may run ahead of what is heard) and uploads whose intro differs from the
recording LRCLIB timed, which shifts every line by a constant.
**How to verify:** Open Ambient on a song with synced lyrics, from the start and mid-song. The
current line must be centred immediately and change as each line is sung. Seek, pause and
resume; each must re-align at once. If lines are consistently early or late, measure by how much
— that decides whether a per-song offset control is needed.
**Found:** 2026-09-25 / working tree after `d574fd8`

### M12 — Added library folders have never been added or removed in the app
**Where:** `AddFolderToLibrary`, `RemoveFolder`, `AddFolderCard`, `FillFolderCardAsync` in [MusicViewModel.cs](../AxePower.App/ViewModels/Music/MusicViewModel.cs)
**Why it matters:** Add Folder used to open a *file* picker, did nothing when the folder had no
music, and kept nothing, so an added folder vanished on the next visit. It now uses a
multi-select folder picker, saves to `music.libraryFolders`, shows each saved folder as a card
with a × to remove it (files untouched), and scans in the background. None of that has run in
the app. The owner's settings already list `Desktop` (added by an earlier playlist-folder change)
and it has no music, so it will appear as a "No music files found" card.
**How to verify:** Add two folders at once, leave the Music page and come back, restart
AxePower: both cards must still be there with their songs. Remove one with ×: it must go, stay
gone after a restart, and its files must still be on disk. Add a folder that is already listed:
it must just be selected.
**Found:** 2026-09-25 / working tree after `d574fd8`

### M13 — Restoring a streamed session after a restart has only run against fakes
**Where:** `InitializeAsync` / `Snapshot` in [MusicPlayerService.cs](../AxePower.Core/Services/Music/MusicPlayerService.cs), `PersistedQueueEntry.Stream` in [PersistedPlaybackState.cs](../AxePower.Core/Models/Music/PersistedPlaybackState.cs)
**Why it matters:** Streamed songs used to be saved as empty file paths and dropped on the next
start ("30 saved queue entries were skipped because the file is gone" in the owner's log), so the
player always came back with nothing playing. Each streamed entry now saves its provider id and
display details, and the current one is restored paused *without* going online; Play resolves it
at the saved position. Proven with a fake resolver and engine only. Sessions saved before this
change carry no stream details and are still dropped once.
**How to verify:** Play a YouTube song, seek to the middle, exit AxePower from the tray and start
it again: the header player must show the song, its cover and position. Press Play: it must
resolve and continue from that position. Repeat with "resume playback on startup" on.
**Found:** 2026-09-25 / working tree after `d574fd8`

### M14 — YouTube Music streaming carries a Terms-of-Service risk (owner's decision, no change made)
**Where:** [YouTubeProvider.cs](../AxePower.Core/Services/Music/Providers/YouTubeProvider.cs), [StreamResolver.cs](../AxePower.Core/Services/Music/Providers/StreamResolver.cs), [StreamCache.cs](../AxePower.Core/Services/Music/Providers/StreamCache.cs)
**Why it matters:** Playing YouTube audio outside YouTube's own player, and keeping downloaded
audio in the local stream cache, goes against YouTube's Terms of Service. It is common in
open-source players, but it is the kind of thing that can draw a takedown request. Raised during
the 2026-09-25 privacy and legal review; the owner asked for it to be noted, not changed.
**How to verify:** Not a test. The owner decides whether to keep the feature as is, keep it but
turn the on-disk cache off by default, or remove it; the privacy policies already describe the
network traffic accurately either way.
**Found:** 2026-09-25 / working tree after `d574fd8`

### P1 — The phone remote has never been opened on a phone
**Where:** [LanDropService.cs](../AxePower.Core/Services/LanDropService.cs) (embedded portal HTML), [MusicRemote.cs](../AxePower.Core/Services/Music/Providers/MusicRemote.cs)
**Why it matters:** Everything is verified over loopback with `HttpClient`, which does not
exercise the actual browser path: the QR code now carries the access token, and the portal's
own scripts were changed to append it to every request (`axeFetch`, `axeUrl`). A mistake there
means the page loads and then every button returns 401. Upload, clipboard and download were all
touched by that change and are existing shipping features.
**How to verify:** Start the portal, scan the QR from a phone on the same network, and confirm
the page loads and that file upload, clipboard send, download and the media buttons all work.
Then confirm the music controls drive AxePower's own player and stay in sync with the desktop.
**Found:** 2026-09-23 / `9708096`

### P2 — `/api/remote` still drives Windows media keys, not AxePower's player
**Where:** `HandleRemoteActionAsync` in [LanDropService.cs](../AxePower.Core/Services/LanDropService.cs)
**Why it matters:** This route predates the music work and sends global media keys, so it
controls whatever application currently owns the Windows media session — which may not be
AxePower. The portal now has `/api/music/command` for the real player, so there are two remotes
with different behaviour and nothing says which is which. It also still exposes `lock`, which
locks the workstation.
**How to verify:** Decide whether the old route stays for controlling other players or is
folded into the music route, then make the portal UI say which one a button uses.
**Found:** 2026-09-23 / pre-existing, surfaced by `9708096`

### M6 — Phase 2a's database is built but nothing writes to it
**Where:** [MusicLibraryRepository.cs](../AxePower.Core/Services/Music/Library/MusicLibraryRepository.cs), [MusicViewModel.cs](../AxePower.App/ViewModels/Music/MusicViewModel.cs)
**Why it matters:** The library schema, repository and search exist and are tested, and no code
path populates them. The Music page still builds its cards in `InitializeLibrary()` on every
visit (`MusicViewModel` is transient): since 2026-09-25 the tag reading runs on a worker thread
and each folder is capped at 2000 files, but nothing is cached, so every visit re-reads every
folder. Phase 2a has no user-visible effect until 2b and 2d land.
**How to verify:** Closed when the scanner populates the database and the Music page reads from
it instead of re-scanning folders on each visit.
**Found:** 2026-09-23 / `e335528`

---

## Behaviour changes to tell the user about

Delete each line once it has shipped and been mentioned in release notes.

- **Old LAN portal links stop working.** The portal now requires an access token, carried by the
  QR link. A bookmark of the plain `http://<ip>:5050/` address returns 401. Covered by a What's
  New card badged "SECURITY FIX"; still needs a line in the release notes.
  (`9708096`, 2026-09-23)

---

## Not built yet

Planned work that has not started. Not defects — listed so nothing here is mistaken for a gap
in what exists. Phases are from [MUSIC_PLAYER_PLAN.md §26](MUSIC_PLAYER_PLAN.md).

| Step | What |
|---|---|
| 2b | Library scanner: incremental by size and mtime, cancellable, `BelowNormal`, batched transactions, live progress |
| 2c | `LibraryWatcher`: one `FileSystemWatcher` per root, debounced, buffer overflow → rescan |
| 2d | Browse UI: Tracks / Albums / Artists / Genres / Folders, search, sort. **Needs a virtualising wrap panel package, which the App does not have** |
| 2e | Playlists, M3U import/export, Liked, Recently played on the library database (the Music page's Last played list is a separate 20-per-mode JSON file, see M9) |
| — | Listening stats page (top lists, time of day, day of week, 12-month calendar) — depends on 2a history, which is already recorded |
| 3–7 | Mini player, automations, audiophile features, tag editor, Discord, Last.fm — see the plan. Synced lyrics and Ambient Mode now exist but remain under M1/M7 until live-verified. |

---

## Verified — do not re-test without reason

Kept deliberately short. These were proven against the real thing, not with fakes, so a session
does not need to re-prove them. Add to this list only what was checked end to end.

| What | How it was proven | When |
|---|---|---|
| YouTube progressive streaming | Live service: opened in 288 ms, decoded real samples, seek to 0:60 worked | 2026-09-23 |
| Candidate ranking picks the right take | Live searches for Blinding Lights, Yesterday, Despacito each ranked the correct recording first; hour-long loop ranked last | 2026-09-23 |
| Portal rejects requests without the token | Real loopback sockets, including `/api/remote` and the SSE stream | 2026-09-23 |
| Portal responses grant no other origin | Asserted over a real response | 2026-09-23 |
| DI hands the portal its token and music remote | Resolved from the app's real container | 2026-09-23 |
| Library survives a corrupt database | Real SQLite file replaced with junk, then rebuilt; damaged file kept | 2026-09-23 |
| Tests do not touch the real machine | `settings.json` byte-identical after a full run; no `library.db` created in `%LOCALAPPDATA%` | 2026-09-23 |
