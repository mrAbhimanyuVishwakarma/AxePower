# Friends and Screen Share delivery status

Updated: 2026-09-26

## Implemented and automated-test covered

- One Friends surface combining a collapsible saved-contact roster, call stage, chat, and files.
- Host rooms that can remain open for voice/chat/files while screen capture is stopped.
- Separate Call and Watch Screen actions; screen sharing is not required for a call.
- Full-duplex Opus voice with mute and deafen controls.
- Authenticated room WebSockets for chat, chunked files, and voice.
- AES-GCM protection for chat, files in transit, and voice packets.
- DPAPI encryption for local chat history and received attachments.
- File selection, 8 MB limit, progress/status cards, integrity checks, and explicit Save As export.
- Existing FFmpeg + MediaMTX WebRTC screen-share path and PIN-gated remote control.
- Fake camera, friend requests, unused SignalR client, duplicate Friends view model, and decorative
  quick actions were removed.

Automated tests are evidence for program logic only. They do not verify microphones, speakers,
capture hardware, firewall behavior, or a real second computer.

## Product decisions

- ~~Identity is account-free.~~ **Changed 2026-09-26 (owner):** Friends and Share Screen are one
  feature, Friends. It now uses the optional AxePower account (see
  [ACCOUNTS_PROFILE_PLAN.md](ACCOUNTS_PROFILE_PLAN.md)): friends by @username request, presence, and
  in-app invites. Old local contacts stay as "Saved rooms". Signed out, room links and codes work as before.
- Topology is a direct AxePower host room. There is no AxePower relay, SFU, TURN server, or hosted
  signalling service.
- Remote internet use requires a trusted VPN/mesh network. The UI does not claim end-to-end safety
  for plain HTTP signalling over an untrusted network.
- Webcam video is out of scope until a real camera transport exists; no fake camera toggle is shown.

## Open live verification

- **SS-1 Screen:** On two PCs, verify start, watch, stop-share-without-ending-call, restart share,
  1080p60 playback, system audio, adaptive quality, and encoder failure recovery.
- **SS-2 Voice:** Verify two-way microphone audio, mute, deafen, device loss/reconnect, latency,
  clipping, and echo behavior with headphones and speakers.
- **SS-3 Chat/files:** Verify simultaneous messages, 8 MB transfer, tamper/failure display, Save As,
  reconnect, and multiple sequential files between two installed clients.
- **SS-4 Network/security:** Verify Windows Firewall prompts, LAN and Tailscale links, invalid and
  expired secrets, room shutdown, and remote-control PIN rejection.
- **SS-5 Performance:** Measure game FPS, CPU/GPU use, audio dropouts, and memory during a sustained
  screen + voice + file-transfer session.

These items remain open until tested on actual AxePower clients. Do not mark them complete based on
builds, unit tests, or single-process loopback checks.


## Friends with accounts: plan (owner decisions 2026-09-26)

| Question | Decision |
|---|---|
| Adding friends | **Requests by @username.** Search, request, accept/decline; block and remove. Both sides see each other only after accepting. |
| What friends see | **Online/Away/Offline plus activity** ("Playing <game>" from Game Mode, "In a call", "Sharing screen"), with a privacy switch to hide activity or appear offline. |
| Call / Watch screen | **Invite through the account:** the friend gets a pop-up in AxePower with Join. The call itself still connects directly (same Wi-Fi or Tailscale) until the Remote Play relay exists. |
| Old saved contacts | **Kept as "Saved rooms"**, a small section under the account friends. |

| Friends footer | **Removed** (owner: "no need of 2 separate"). The sidebar profile already shows who you are. Its gear moves to the **roster header, next to +**. |

### Phases (each ends with tests, a live check and a pause)

- **F1 - Backend.**
  - Tables `friendships` (pending/accepted/blocked), `presence` and `friend_invites` (10-minute expiry).
  - RLS so that only the two people involved see a friendship, only friends see presence (and none
    of it while the user appears offline), and only a friend can invite you.
  - Changes go through RPCs (`send_friend_request(username)`, `respond`, `remove`, `block`), so the
    rules sit in one place. Realtime publication for the three tables.
  > **Status 2026-09-26: built and pushed** (migration `friends`). It has 37 rule checks on PGlite
  > (`supabase/tests`, `npm test`): requests, mutual accept, presence only for friends, invites only
  > between friends, undetectable blocks, and the delete cascade. Live: anonymous callers are refused
  > on every table and action, and the linter is clean. **Not yet run live:** any of it between two
  > real signed-in accounts (F4). Realtime delivery is untested until F2.
- **F2 - Desktop core.**
  - `FriendsService`: the list, requests and actions.
  - A presence heartbeat (60 s, plus on every change; Away after 10 minutes idle; the activity from Game
    Mode and the call/share state).
  - A Supabase Realtime (Phoenix WebSocket) client for live presence, requests and invites.
  - The privacy switch.
  > **Status 2026-09-26: built.**
  > - `FriendsService` follows the account.
  > - `SupabaseRealtimeFeed` (Phoenix vsn 1.0.0: heartbeat 25 s, the token pushed onto the channel,
  >   back-off from 1 to 30 s).
  > - Presence every 60 s and on change. The game comes from Game Mode's detection; Away after 10
  >   idle minutes. The privacy switches (`shareActivityWithFriends`, `appearOffline`) are applied
  >   before publishing.
  > - Offline is published on exit. There is a self-test line.
  >
  > **Verified:** 34 tests, including a race found and fixed (an invite arriving during a reload was
  > dropped). Live as a signed-out caller: the socket, join and presence/friendships subscriptions
  > are accepted.
  > **Not yet run live:** anything as a signed-in user: publishing presence, the invites
  > subscription (Supabase refuses it for signed-out callers, as RLS intends; a refusal is now
  > logged instead of dropping the channel), and the token refresh on the channel.
  > **Known and minor:** a friend's presence arriving live during a reload can be replaced by the
  > older fetched value until their next heartbeat (60 s).
  > **Moved to F3:** feeding "In a call" / "Sharing screen" from the Friends page (`SetSessionActivity`).
  > **First live run (owner's app, 13:32-13:40 on 2026-09-26):** it started signed in as @mrabhimanyu
  > and ran for 7 minutes with no presence-publish failure and no Realtime refusal or disconnect
  > logged. Every failure path logs, so this points to both working as a signed-in user. It is not yet
  > confirmed by reading the `presence` row.
- **F3 - Friends UI.**
  - The account identity in the Friends footer: avatar and @username instead of the Windows name.
  - The roster from the account; Online/All/Live filters become real.
  - Add by @username; incoming and outgoing requests; block/remove.
  - An invite pop-up with Join; the "Saved rooms" section.
  - Signed out: "Sign in to add friends" while room codes keep working.
  > **Status 2026-09-26: built.**
  > - `FriendsHubViewModel` on the Friends page; the roster header is ⚙ + ‹ and there is no footer.
  > - Sections: a sign-in card (signed out), Requests (accept/decline, cancel), Friends (status dot,
  >   "Playing …", invite to call, invite to watch, ⋯ Remove/Block with a confirmation), and Saved rooms
  >   (the old contacts, unchanged).
  > - "+" dialog: add by @username above the room-link form.
  > - Invites: a pop-up on every page, bottom right, with Join/Decline. Join opens Friends and connects;
  >   it isn't saved as a room. A watch invite needs the screen shared first.
  > - Room and chat names use the account's display name when signed in. Call/share state feeds presence.
  > - Settings → Friends: Privacy (show activity, appear offline) and Blocked people (Show/Unblock).
  >
  > **Verified:** 21 page tests (lists, filters, requests, block confirmation, the invite pop-up and join,
  > invites, presence mapping, the view loading with no footer); off-screen render in Glass; the full
  > suite (1040 passed, 1 skipped).
  > **Not yet seen:** the invite pop-up and the Settings privacy section in the real window (not rendered).
  > **Not yet run:** anything between two accounts (F4).
- **Search suggestions** (owner request, 2026-09-26):
  - Typing in "Add a friend" suggests people by @username or display name. Each row has Add / Accept,
    or reads Requested / Friends / Blocked.
  - "Let people find me by name" opts out. Declining a request still deletes it, so it can be sent again.
  - Migration `friend_search`: 48 database checks. 8 app tests; off-screen render.
- **F4 - Live test** with a second account on a second PC or Windows user: request, accept, presence,
  invite, join, and the call/screen over the same Wi-Fi and over Tailscale.
