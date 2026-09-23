# Share Screen — social layer plan

> **Status:** Design agreed, nothing built
> **Decided:** 2026-09-23
> **Supersedes:** parts of [`GameStream_Share_PROJECT_BLUEPRINT.md`](GameStream_Share_PROJECT_BLUEPRINT.md) — see *Blueprint amendments*
> **Nothing in this document has run.** Everything here is a plan, not a claim.

---

## 1. What changed

The blueprint built a one-way gaming stream: host captures, MediaMTX fans out, viewers watch in a
browser. §31 explicitly excluded voice chat, chat messages, webcam and remote input.

The owner has asked for the Discord model instead: a room you and your friends are *in*, with
two-way voice, cameras, text chat and an invite flow. That is a different product, and it
invalidates the delivery architecture rather than extending it.

## 2. Decisions

| Question | Decision | Consequence |
|---|---|---|
| Identity | **No accounts.** Rooms and invite links only | No friend list is possible — see *Open contradiction* |
| Reach | **Internet, anyone with a link** | Needs a small relay + TURN VPS (~$5/mo) |
| Voice | **Full duplex, host mixes**, 4–6 people | MediaMTX cannot do this. In-process WebRTC required |
| Camera | **Everyone** — viewers send camera too | Forces a topology choice, see §4 |
| Quality | **Everything user-choosable** — resolution, FPS, bitrate, codec | Contradicts blueprint §3; amend it |

### Assumed unless contradicted

- Chat is ephemeral and lives inside the room. No history, nothing stored. Forced by "no accounts".
- Push-to-talk binds a global hotkey, so it works while a game holds focus.
- Remote control keeps its existing per-session code gate. Rooms becoming internet-reachable is a
  reason to tighten that, never to loosen it.

## 3. Open contradiction — the friend system

Image 6 asked to "invite friends and be friends, so we can chat, screen share, or be on voice
call". A persistent friend list requires a persistent identity, and "no accounts" rules that out.
These cannot both be true.

Without accounts, the most that can be built is:

- Saved invite links, under a name the host types in.
- A "recent rooms" list.
- A room code that stays stable between sessions, so the same link keeps working.

That gives *"send Rahul the same link again"*. It does not give presence, offline invites, or
knowing who is online. **This needs an owner decision before Phase H.** Reopening it means
reopening the accounts question, and a backend that stores user data.

Note also: Discord OAuth cannot shortcut this. `identify` (username, avatar) is freely available,
but the friends list needs the `relationships.read` scope, which is Social-SDK partner-only and
requires Discord's approval. We cannot borrow the graph.

## 4. Topology — needs confirmation

"Everyone sends camera" plus "host mixes" plus 4–6 people does not resolve to one obvious shape.

- **Host as SFU** — every camera goes to the host, which forwards to everyone. Simple mentally,
  but the host decodes and re-encodes 5 streams *while gaming*. Directly violates blueprint
  priority #5, low impact on game performance.
- **Relay as SFU** — put the routing on the VPS. Clean, but then the VPS carries all media, and
  the bandwidth bill stops being $5/mo.
- **Split (recommended)** — the screen share goes host → everyone, one upstream from the host.
  Camera and mic go full mesh between participants at 360p. Each browser uploads to 5 peers,
  which is fine at that resolution, and the host's game keeps its CPU.

Assume Split unless told otherwise.

## 5. Phases

Each phase must be demonstrably working before the next starts. Phases A and B touch only local
capture and are safe; Phase C tears out a path that currently works.

### Phase A — source picker and tile chrome
Applications / Entire Screen / Devices tabs, live window thumbnails, per-window capture, capture
device sources. Quality flyout exposing resolution, FPS, bitrate and codec directly. `720P 30FPS
LIVE` badge on the tile, lit share button.
**Pass:** a named window can be shared without the rest of the desktop, and every quality control
visibly changes the encoder arguments.

### Phase B — audio
Mix mic *and* system audio into one track, replacing the current either/or `ShareAudioMode`.
Output device selection, live input level meter, push-to-talk on a global hotkey, deafen. The
flyout from image 3.
**Pass:** the level meter moves with real speech, and PTT gates the mic while a game is focused.

### Phase C — WebRTC rewrite ⚠ highest risk
Replace MediaMTX and `ShareViewerHost` with in-process WebRTC (SIPSorcery or libdatachannel), one
peer connection per viewer. Host → viewer only. **Target is parity with today, nothing more.**
**Pass:** a viewer sees 1080p60 with audio at no worse latency than the MediaMTX path. If this
fails, the old path must still be revertable.

### Phase D — relay and TURN
Signalling endpoint plus coturn on a VPS. Room codes. Links that work from outside the LAN.
**Pass:** a phone on mobile data, not on the LAN, joins from a link.

### Phase E — the return path
Viewer mic → host. Host mixes. This is the moment it becomes a call.
**Pass:** four people talk over a live game stream without echo or drift.

### Phase F — cameras and the grid
Host camera as a second track, then viewer cameras per the Split topology. Tile grid, name tags,
speaking indicator. Camera flyout from image 4.
**Pass:** four cameras and a screen share render as a grid without the host's game dropping frames.

### Phase G — chat
Text over a WebRTC data channel. Docked right panel, composer, emoji. Ephemeral.
**Pass:** messages arrive with the panel closed and show an unread badge.

### Phase H — invites
Blocked on §3. Do not start until the friend question is answered.

## 6. Blueprint amendments needed

- §3 "FPS is more important than resolution" — no longer absolute. Adaptive fallback still
  prefers dropping pixels over frames, but an explicit user choice wins.
- §31 MVP scope — voice chat, chat messages and webcam move out of *DO NOT INCLUDE*.
  Remote keyboard/mouse was already built in `ac55f30`, so that guard is already stale.
- §10 MediaMTX — retired by Phase C.

## 7. Open

- **SS-1** — No part of the existing Share Screen pipeline has been verified running by an agent.
  The app relaunches elevated, so capture, encode, MediaMTX and the viewer page have only ever
  been built and unit-tested against fakes. Owner verification needed before Phase C tears any
  of it out, or we will not know whether a regression is new.
- **SS-2** — The friend system contradiction in §3.
- **SS-3** — Topology in §4 assumed, not confirmed.
- **SS-4** — Echo cancellation is unaddressed. Host loopback audio plus host mic plus viewer mics
  is a feedback loop; browsers cancel their own echo, the host's WASAPI mix will not.
- **SS-5** — TURN bandwidth cost is unmodelled. A relayed 1080p60 viewer is ~16 Mbps through the
  VPS, which most $5/mo plans will not absorb quietly.
