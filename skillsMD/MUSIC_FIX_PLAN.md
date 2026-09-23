---
name: music-fix-plan
description: Live list of open issues and unverified work in AxePower Music and the LAN portal. Read at the start of a session that touches either; update it before finishing.
---

# AxePower Music — Fix Plan & Verification Ledger

> **Branch:** `v1.9` · **Last updated:** 2026-09-23 · **Last commit covered:** `c4b2cb5`

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
**Where:** [MusicView.xaml](../AxePower.App/Views/Music/MusicView.xaml), [DashboardViewModel.cs](../AxePower.App/ViewModels/DashboardViewModel.cs)
**Why it matters:** This is the largest untested surface in the feature. The Music Settings
dialog grew from one 480px panel to a 640px scrolling dialog with five sections (Sources,
streamed audio, Discovery, Shortcuts, Control other players). None of it, nor the Discovery
toggle added to the transport row, nor the "Play from" submenu on queue rows, nor the four new
What's New cards, has been seen. Binding errors and layout breakage would not show up in any
test; XAML compiles fine with a misspelled binding path.
**How to verify:** Launch the app (it relaunches elevated, so this has to be done by the owner),
open Music → settings, scroll the whole dialog, right-click a streamed queue row, and check the
Dashboard's What's New dropdown. Compare against `ui reference/`.
**Found:** 2026-09-23 / `3689ff4`–`141ebca`

### M2 — Spotify sign-in has never been executed
**Where:** [SpotifyAuth.cs](../AxePower.Core/Services/Music/Providers/SpotifyAuth.cs), [SpotifyProvider.cs](../AxePower.Core/Services/Music/Providers/SpotifyProvider.cs)
**Why it matters:** The whole PKCE flow is unproven: the loopback listener on port 8898, the
browser hand-off, the state check, the code-for-token exchange, refresh-token rotation, and
every API call built on them. No Client ID has ever been configured, so not one line of the
network path has run. A wrong redirect URI or a missing scope fails only at runtime.
**How to verify:** Register an app at developer.spotify.com with redirect URI
`http://127.0.0.1:8898/callback`, paste the Client ID into Music Settings → Sources → Spotify,
sign in, then search and import a playlist. Also confirm signing out and back in works, and that
a stale refresh token is dropped rather than retried forever.
**Found:** 2026-09-23 / `3689ff4`

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
**Where:** `PrepareAsync` in [StreamResolver.cs](../AxePower.Core/Services/Music/Providers/StreamResolver.cs)
**Why it matters:** Expiry is tested with a fake clock. A real signed YouTube URL that has aged
past the hour may fail in a way the resolver does not recognise as expiry — for example a 403
during decode rather than at resolve time — in which case the track is reported as unplayable
instead of being re-resolved.
**How to verify:** Resolve a streamed track, leave the queue untouched for over an hour, then
play it. It should re-resolve and play rather than error.
**Found:** 2026-09-23 / `3689ff4`

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
path populates them. The Music page still builds its list in `InitializeLibrary()` — on the UI
thread, in the view model's constructor, reading TagLib tags synchronously, capped at 200 files,
persisting nothing. Because `MusicViewModel` is registered transient, that happens again on
every visit to the page. Phase 2a has no user-visible effect until 2b and 2d land.
**How to verify:** Closed when the scanner populates the database and the Music page reads from
it, and the synchronous scan in the view model is gone.
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
| 2e | Playlists, M3U import/export, Liked, Recently played |
| — | Listening stats page (top lists, time of day, day of week, 12-month calendar) — depends on 2a history, which is already recorded |
| 3–7 | Mini player, automations, audiophile features, lyrics, tag editor, Discord, Last.fm — see the plan |

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
