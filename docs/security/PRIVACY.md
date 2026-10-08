# AxePower data-flow inventory

Last reviewed: 6 October 2026 (Prompt 26), against the code at the commit this file ships with.

This is the engineering source for every privacy statement AxePower makes: the README, the release
notes, the website's privacy page, the in-app Profile › Privacy section and the Trust & Permissions
page. The Trust page reads the same list from `AxePower.Core/Services/Trust/TrustInventory.cs`.
`AxePower.Tests/Trust/PrivacyClaimsTests.cs` fails if this file and that list disagree, if a public
surface makes an absolute claim ("100% local", "zero telemetry", …), or if an analytics library
appears in the code.

## The claims AxePower makes, and why they are true

| Claim | Evidence |
|---|---|
| **No ads.** | No ad SDK or ad host in the code or packages (`PrivacyClaimsTests.NoAnalyticsOrAdLibraries`). |
| **No usage analytics sent to the developers.** | No analytics or crash-reporting SDK (Google Analytics, Firebase, App Center, Sentry, Mixpanel, Segment, Amplitude, PostHog) in the code or packages, and no AxePower-run server receives usage data. Diagnostics stay on the PC; a bundle exists only when the user saves one ([DIAGNOSTICS.md](../engineering/DIAGNOSTICS.md)). |
| **Accounts are optional.** | Every feature except Friends works signed out; without an account, nothing about the user is stored in Supabase. |
| **Online features contact other services only for that feature,** with one exception at start-up: the update check, while "Check for updates automatically" is on. A new install asks on its welcome screen (recommended on) and checks nothing before then; existing installs keep their setting. | The connections table below, which lists every file that opens a connection (enforced by `TrustInventoryTests`). |
| **The website is separate.** It uses Google Analytics only after "Accept" in its cookie banner. | `AppWebsite/consent.js`; nothing on the website affects the desktop app, and the desktop app contains no analytics. |

Claims AxePower no longer makes, because they were not accurate: "100% local", "zero remote data
collection", "zero telemetry". AxePower has optional online features, and an account stores the
data listed below.

## Data stored on this PC

All under the Windows user's profile. Purpose is to make the feature work; nothing here is uploaded
unless the user saves a diagnostic bundle and shares it themselves.

| Store | Contents | Where | Retention | User control |
|---|---|---|---|---|
| Settings | settings.json and its backup | `%LOCALAPPDATA%\AxePower` | Until changed or the app is removed | Settings; open the folder from Trust & Permissions |
| Diagnostics logs | Redacted logs (no tokens, links, emails, user or computer name) | `%LOCALAPPDATA%\AxePower\Diagnostics` | 14 days or 20 MB | Inspect, export, delete: Settings › Diagnostics & Privacy, or Trust & Permissions |
| Change history | Every change AxePower made to Windows, with rollback snapshots | `%LOCALAPPDATA%\AxePower\Operations` | Newest 300 | Settings › Change history (open only: it is the undo data) |
| Music history | Recently played songs, recent YouTube searches with results | `%LOCALAPPDATA%\AxePower\Music` | 20 local and 20 streamed plays, 7 searches | Delete from Trust & Permissions |
| Music caches | Cover thumbnails, streaming cache | `%LOCALAPPDATA%\AxePower\Music\covers, stream-cache` | Streaming cache capped by the size set in Music settings; rebuilt as needed | Delete from Trust & Permissions |
| Friends chat | Saved room chat and attachments, encrypted with the Windows account (DPAPI) | `%LOCALAPPDATA%\AxePower\Chat` | Until deleted | Delete from Trust & Permissions |
| Account session | Sign-in tokens, encrypted with the Windows account (DPAPI) | `%LOCALAPPDATA%\AxePower\Account` | Until sign-out | Sign out |
| Uninstaller history and backups | Sessions, files and registry keys backed up before cleanup, Windows Installer logs | `%LOCALAPPDATA%\AxePower\UninstallerHistory, UninstallLogs` | Until deleted | Open from Trust & Permissions; restore in Uninstaller Pro |
| Received files | Files sent from the phone through Phone Hub / LAN Drop | `%USERPROFILE%\Downloads\AxePower_LanDrop` (or the chosen folder) | The user's files | File Explorer |
| Battery history | Capacity and charge readings for the Battery page | `%LOCALAPPDATA%\AxePower\BatteryHistory` | Rolling window | Delete from Trust & Permissions |
| Downloaded installers | Graphics driver installers | `%LOCALAPPDATA%\AxePower\Downloads` | Until deleted | Delete from Trust & Permissions |
| Downloaded tools | FFmpeg, MediaMTX, scrcpy, portable apps | `%LOCALAPPDATA%\AxePower\tools, Tools` | Until removed | Software Hub |
| Game artwork | Posters for installed games | `%APPDATA%\AxePower\CoverArt` | Until deleted | Delete from Trust & Permissions |
| Custom skins | Skins the user added | `%APPDATA%\AxePower\Skins` | Until deleted | File Explorer |
| Share Screen working files | Media server settings for the hosted room, Tailscale HTTPS certificate | `%LOCALAPPDATA%\AxePower\share` | Rewritten per room | Open from Trust & Permissions |
| Driver update script | The PowerShell script used for a driver update | `%LOCALAPPDATA%\AxePower\Scripts` | Until deleted | Delete from Trust & Permissions |
| Share Screen viewer data | The embedded browser's cache | `%LOCALAPPDATA%\AxePower\webview2` | Until deleted | Delete from Trust & Permissions |

## Data stored in AxePower's cloud (Supabase), only with an account

Purpose: sign-in, profile and Friends. Not sold, not used for advertising. Deleting the account
(Trust & Permissions or Profile) deletes all of it immediately (`on delete cascade`); Supabase may
keep short-lived technical logs under its own policy.

| Category | Contents | When | Retention |
|---|---|---|---|
| Sign-in | Email address and the providers used (Google, GitHub, Discord, Steam); passwords only as a hash held by Supabase Auth | Account creation, sign-in | Until the account is deleted |
| Profile | Username, display name, avatar link, findable or not, created/renamed dates | Creating or editing the profile | Until the account is deleted |
| Steam link | Steam ID | Signing in with Steam | Until the account is deleted |
| Friends | Friend requests, friendships, blocks | Adding, accepting, removing, blocking | Until removed or the account is deleted |
| Presence | Online/away; with "Show friends what I'm doing", the current activity | While signed in and Friends is active | Replaced as it changes |
| Room invites | One invitation to a Friends room, including its room link, to one friend | Inviting a friend | 10 minutes |

Visible to others: any signed-in user can see a username, display name and picture (friend search);
friends, presence and invites are visible only to the people involved.

## Network connections

"Data sent" excludes what every web request carries (the IP address and request headers), which
each service sees under its own privacy policy. AxePower runs no server of its own in between.

| Connection | Hosts | Purpose and trigger | Data sent | User control |
|---|---|---|---|---|
| AxePower updates (GitHub) | api.github.com, github.com, objects.githubusercontent.com | Find and download AxePower updates. At start-up when "Check for updates automatically" is on (chosen on the welcome screen), and on Check for updates; downloads only when installing | AxePower's version | Settings › Updates & Maintenance |
| AxePower account (Supabase) | kjnqsgdlcytpooueymdt.supabase.co | Sign in, refresh the session, save the profile, delete the account; only when signed in | Email and password or the provider's result; username, display name, avatar link | Don't sign in, or sign out |
| Friends and presence (Supabase Realtime) | kjnqsgdlcytpooueymdt.supabase.co | Live friend requests, presence and invites while signed in | Online/away, activity if shared, requests, invites | Appear offline, stop sharing activity, or sign out |
| Google, GitHub, Discord or Steam sign-in | accounts.google.com, github.com, discord.com, steamcommunity.com (in the browser) | Sign in with that provider, only when chosen | What the user approves on the provider's page; AxePower receives a sign-in code on 127.0.0.1 | Use email or no account |
| Remote access for AxePower Mobile (Supabase Realtime) | kjnqsgdlcytpooueymdt.supabase.co | **Turned off in this version**; it never connects ([REMOTE_ACCESS.md](REMOTE_ACCESS.md)) | Nothing | Nothing needed |
| YouTube Music | youtube.com, music.youtube.com, googlevideo.com, i.ytimg.com | Search, play or save a song from YouTube; captions as lyrics; artwork | Search words and played songs | Use only This PC as the music source |
| Lyrics (LRCLIB, KuGou) | lrclib.net, lyrics.kugou.com | Find lyrics while the lyrics panel is open | Title, artist, album, length | Keep the panel closed |
| Game artwork (Steam CDN) | cdn.cloudflare.steamstatic.com, shared.cloudflare.steamstatic.com, steamcdn-a.akamaihd.net, store.steampowered.com | Posters for installed games in Game Mode, once per game | The game's Steam ID or name | Delete game artwork |
| Network latency on the Dashboard (ping) | 1.1.1.1 (Cloudflare) | Latency on the Dashboard's network tile: every 5 seconds, only while the Dashboard is on screen | An empty echo request (the IP address) | Leave the Dashboard |
| DNS benchmark (ping) | 1.1.1.1, 9.9.9.9, 8.8.8.8, 208.67.222.222 | Compare DNS providers, only when the benchmark is run in System Doctor | An empty echo request (the IP address), three per provider | Don't run the benchmark |
| Graphics drivers (NVIDIA, AMD) | nvidia.com, us.download.nvidia.com, amd.com | Check for or download a driver, when asked | Graphics card model, driver version | Don't use the driver check |
| Share Screen tools (FFmpeg, MediaMTX) | api.github.com, github.com, www.gyan.dev | Download the tools on first use of Share Screen, and updates | A download request | Don't use Share Screen |
| Portable tools and Android control (GitHub) | api.github.com, github.com | Install a portable tool or set up Android control (scrcpy) | A download request | Don't install them |
| Software Hub installs (WinGet, Microsoft Store) | Microsoft's WinGet and Store sources; each publisher's server | Install, update or remove software, when asked | Package names, Windows version (sent by WinGet) | Don't use Software Hub installs |
| Friends rooms and Share Screen viewers (direct) | The other devices in the room (local network or Tailscale) | Host or join a room | Screen and sound when sharing, voice, chat, remote input; chat, voice and control end-to-end encrypted | End the room |
| Phone Hub and LAN Drop (your network) | The user's phone on the same network | While the LAN Drop server runs | Files, text, music remote; plain HTTP on the local network, behind the access link | Stop the server |

## Ports AxePower opens on this PC

| Listener | Address | When | Who can connect |
|---|---|---|---|
| Phone Hub / LAN Drop portal | `http://<network address>:5050` | After Start, not on Public networks unless allowed for the session | Devices with the access link; 5 wrong tries lock an address out for a minute |
| Friends room / Share Screen viewer page | `http(s)://<network address>:<room port>` | While hosting a room | People with the room link; remote control approved each time |
| Share Screen media server (MediaMTX) | Local WebRTC ports | While sharing the screen | Viewers admitted by the room page |
| Sign-in callback | `http://127.0.0.1:<random port>` | During a provider sign-in | This PC only; one answer, then it closes |
| Android control (adb) | `127.0.0.1:5037` | While Android control runs | This PC only |

## System access

- **Administrator rights.** AxePower runs as administrator by default ("Run as administrator",
  Settings › Startup & Elevation). It starts at logon through the scheduled task "AxePower Startup",
  at highest privileges when that setting is on, so there is no UAC prompt. Pages, apps and folders
  it opens for the user start with normal rights.
- **Changes to Windows.** It changes the system only when the user chooses an action or sets up an
  automation. Changes on the change framework (tweaks, Game Mode services) are previewed, recorded,
  and labelled fully, partially or not reversible; see [SAFE_CHANGES.md](../engineering/SAFE_CHANGES.md).
  Third-party uninstallers and debloat scripts cannot be undone by AxePower, and the confirmations
  say so.

## Known gaps

- The Battery page fills an empty history with a synthetic curve when Windows' battery report is not
  available (`BatteryService.GenerateSeedHistory`). It is not labelled as estimated. Found
  2026-10-06; to be fixed separately.
