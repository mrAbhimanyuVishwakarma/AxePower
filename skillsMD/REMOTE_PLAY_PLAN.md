---
name: remote-play-plan
description: Plan for reaching and controlling the PC from AxePower Mobile anywhere, Steam Remote Play style — bridge, in-app WebRTC desktop, Apollo/Moonlight game stream, gamepad, Tailscale, then accounts and our own relay.
---

# AxePower Remote Play — Plan

> **Status:** planned, nothing built · **Written:** 2026-09-26 · **Desktop:** v2.0.8 (branch `v2.0.8`) · **Mobile:** v0.0.1

Goal: from the phone, see the PC's screen, hear it, and control it with touch, keyboard and a
gamepad, whether the phone is on the same Wi-Fi or somewhere else entirely — the way Steam Remote
Play / Steam Link does it.

## Decisions (owner, 2026-09-26)

| Question | Decision |
|---|---|
| Streaming engine | **Hybrid.** In-app remote desktop over WebRTC (our Share Screen stack) **and** a *Game Stream* button that runs Apollo on the PC and hands off to Artemis / Moonlight on the phone. |
| Reaching the PC from elsewhere | **Tailscale first, then Steam-style.** Remote access ships over Tailscale; our own rendezvous + STUN + TURN relay comes after. |
| Who may reach which PC | **AxePower account**, like a Steam account, on **Supabase**. Built by [ACCOUNTS_PROFILE_PLAN.md](ACCOUNTS_PROFILE_PLAN.md), which comes first; same-Wi-Fi QR pairing keeps working without an account. |
| Server hosting | **Decide later**, at the start of phase 7. |
| Extras | **Gamepad, Steam-style** (on-screen pad + Bluetooth controllers → virtual Xbox pad on the PC). |

## How Steam does it, and our equivalent

| | Steam Remote Play | AxePower |
|---|---|---|
| Video | Valve's closed protocol, GPU-encoded H.264/HEVC over UDP 27031 | WebRTC (in app) or the Moonlight protocol (Apollo) — both GPU-encoded over UDP |
| Same network | UDP broadcast discovery on 27036, then direct | UDP broadcast discovery, then direct |
| Elsewhere | Steam account + Valve servers introduce the devices; ICE tries direct, falls back to Valve's relay (3478) | Tailscale first; later AxePower account + our rendezvous, ICE direct, coturn relay fallback |
| Controller | Steam Input → virtual Xbox pad | ViGEmBus virtual Xbox pad |

Valve's networking library is open ([GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets),
BSD-3), but the relay network behind it is Steam-only, so it gives us nothing WebRTC does not.

## Where we start from

- **Share Screen** exists: FFmpeg `ddagrab` + NVENC/AMF/QSV → RTSP → MediaMTX → WebRTC (WHEP) →
  browser, with PIN-gated control through `RemoteInputService`. Its live checks (SS-1…SS-5 in
  [SHARE_SOCIAL_PLAN.md](SHARE_SOCIAL_PLAN.md)) have **never been run** on two machines.
- **The desktop half of the mobile bridge does not exist.** `PairedDevice` and `BridgeServerState`
  in [BridgeModels.cs](../AxePower.Core/Models/BridgeModels.cs) are unused; the phone's
  `WebSocketPcBridge` has nothing to talk to. The app has only ever run against its simulated bridge.
- MediaMTX runs with no STUN/TURN; internet use today needs a VPN.

## Architecture

```
 PHONE — AxePower Mobile (Flutter)                   PC — AxePower (.NET)
 ┌─────────────────────────────────┐               ┌─────────────────────────────────────┐
 │ Dashboard / modes               │◄── bridge ───►│ Bridge server  (NEW, :8765)          │
 │                                 │   WebSocket   │  pairing · telemetry · commands     │
 │ Remote Desktop (NEW)            │               │  input → RemoteInputService          │
 │  flutter_webrtc player ◄────────┼─ WebRTC ──────┤ FFmpeg → MediaMTX (WHEP)             │
 │  touch · keys · on-screen pad ──┼─ input ──────►│ ViGEmBus virtual Xbox pad            │
 │                                 │               │                                     │
 │ [Game Stream] ── launches ──►  Artemis/Moonlight ◄─ Moonlight protocol ─ Apollo (service)│
 └─────────────────────────────────┘               └─────────────────────────────────────┘
   Path 1  same Wi-Fi ............ direct
   Path 2  elsewhere (phase 6) ... Tailscale tailnet IP, direct — both engines
   Path 3  elsewhere (phase 7) ... AxePower account → rendezvous → ICE direct, else TURN relay
                                    (WebRTC only; Game Stream stays on Tailscale)
```

**Limitation:** the Moonlight protocol has no ICE/TURN. Game Stream from another location works
over Tailscale or a forwarded port only. Only the in-app Remote Desktop uses our relay.

## Phases

Each phase ends with a build, the full test suite, `--self-test`, a settings.json snapshot compare
and a check on a real phone. Then we stop for review. Anything that has not run on a real device is
reported as unverified.

### Phase 0 — Spikes (measure, no product code)
- Play the existing WHEP stream in a throwaway `flutter_webrtc` page on a real phone and measure
  glass-to-glass latency on Wi-Fi (stopwatch on PC, photo of both screens). Record the number here.
  This decides whether phase 7 needs a direct WebRTC sender (Pion in a Go sidecar, or SIPSorcery in
  C#) that carries input on a DataChannel, instead of MediaMTX plus input through the relay.
- Install Apollo by hand and confirm three things: pairing can be automated from its local API; it
  can be installed silently; and Artemis (Android) and Moonlight (iOS) can be launched straight into
  a host from another app. If they cannot, the phone opens the app and the user picks the PC.

### Phase 1 — Desktop bridge + QR pairing (same Wi-Fi)
- `BridgeService` in Core: raw-socket HTTP/WebSocket like `ShareViewerHost` (no admin URL
  reservation). Serves the `/telemetry` protocol of [design.md §4.1](../design.md) on 8765.
- Pairing: the PC shows a QR code containing the host, port, a one-time code and the PC's key
  fingerprint. The phone gets a bearer token, and the PC stores only its hash (as `PairedDevice`
  already intends).
- Per-device permissions: existing `AllowPowerControl` and `AllowSystemPower`, plus new
  `AllowScreenView` and `AllowRemoteControl`. The two new ones are off by default and are switched on
  on the PC.
- LAN discovery by UDP broadcast, as Steam does, with typing the IP as a fallback.
- Phone: a QR scan screen and the real bridge in release builds. The simulated bridge stays for
  demos and tests.
- A self-test line for the bridge (reports config only; starts nothing).

### Phase 2 — Remote Desktop in the app (same Wi-Fi)
- The phone asks the bridge to start a stream. The PC starts the Share Screen pipeline with a phone
  preset (720p60 or 1080p60, chosen to match the phone's screen) and returns the WHEP URL.
- `flutter_webrtc` player: full screen, landscape, PC audio, pinch-zoom and pan, a stats overlay
  (resolution, FPS, bitrate, RTT, encoder).

### Phase 3 — Mouse and keyboard control (same Wi-Fi)
- Compact input messages go over the bridge socket into the existing `RemoteInputService` gate.
  That gives the same stuck-key release on disconnect and binds input to the streamed monitor.
- Touch modes: **trackpad** (relative, tap to click) and **direct touch** (absolute).
  - Long-press gives a right-click.
  - Two-finger drag scrolls.
  - Three fingers bring up the keyboard.
- Key bar: Ctrl, Alt, Shift, Win, Esc, Tab, arrows, F1–F12, Ctrl+Alt+Del (it cannot reach the
  secure desktop; see phase 5).
- On the PC: a "<phone> is controlling this PC" banner, and a panic hotkey that revokes control
  and drops the stream.

### Phase 4 — Gamepad, Steam-style
- PC: ViGEmBus is installed on demand (UAC) with a clear prompt. `Nefarius.ViGEm.Client` creates
  one virtual Xbox 360 pad per phone. The pad is removed when the session ends.
- Phone: an on-screen controller overlay with layouts you can edit, plus Bluetooth/USB controllers
  read by a Flutter gamepad plugin (choice checked in this phase).
- Pad state is sent at up to 120 Hz and only when it changes. Rumble is sent back to the phone.
- ViGEmBus is archived (2023). Its successor, VirtualPad, is not out yet. Keep the pad behind an
  interface so the driver can be swapped.

### Phase 5 — Game Stream (Apollo + Artemis/Moonlight)
- AxePower downloads Apollo from its GitHub releases on demand, as it already does for scrcpy.
  **Apollo is never bundled in our installer** (GPL-3). Apollo runs as its own Windows service.
- Pairing is automated through the bridge: the phone requests pairing and AxePower submits
  Apollo's PIN locally, so the user never types it.
- Game list: AxePower's known games are exported to Apollo as apps.
- The phone's *Game Stream* button launches Artemis (Android) or Moonlight (iOS) at this PC. If the
  app is missing, it opens the store page.
- What Apollo brings with it: a virtual display at the phone's resolution and refresh rate (SudoVDA),
  and control at the lock screen and UAC prompts (it runs as a service). The in-app Remote Desktop
  does **not** get those.

### Phase 6 — Remote access over Tailscale
- PC: detect Tailscale (100.64.0.0/10 interface or `tailscale status --json`). Put the tailnet IP in
  the pairing QR and the device record. Make sure MediaMTX advertises it.
- Phone: try the LAN address first, then the tailnet address. Show which path is in use.
- Guided setup screens: install Tailscale on both devices and sign in with the same account.
- Both engines work here. This is the first release that works "from a different location".

### Phase 7 — AxePower accounts + Steam-style relay
Accounts already exist by now ([ACCOUNTS_PROFILE_PLAN.md](ACCOUNTS_PROFILE_PLAN.md)). Start by choosing TURN hosting, and whether the rendezvous runs on Supabase Realtime or our own service.
- **Accounts → devices:** the PC registers itself in the `devices` table, with a list of PCs and phones and revoke. A phone signed into the same account sees that PC, like the Steam Link PC list.
- **Rendezvous:** the PC keeps an outbound WSS connection (no port forwarding) and reports whether it
  is online. The server relays only the SDP offer/answer and ICE candidates.
- **STUN + TURN (coturn):** short-lived TURN credentials (TURN REST API) come from the account
  service, and the ICE server list is pushed to MediaMTX (`webrtcICEServers2`) and the phone.
- **Direct / Relay indicator:** bitrate and resolution adapt on mobile data (FPS is kept before
  resolution, per the GameStream blueprint).
- **Input:** if phase 0 showed the relay hop is too slow for input, add the direct sender with a
  DataChannel here.
- Being signed in does **not** by itself grant control. The per-device permissions from phase 1
  still apply, and control is still switched on on the PC.

### Phase 8 — Live test matrix and hardening
| Path | Remote Desktop | Game Stream |
|---|---|---|
| Same Wi-Fi | ☐ | ☐ |
| Tailscale, phone on 4G/5G | ☐ | ☐ |
| Relay, two different Wi-Fi networks | ☐ | n/a |
| Relay, CGNAT / TURN forced | ☐ | n/a |

Also test NVIDIA, AMD and Intel encoders; Android and iOS; audio; gamepad and rumble; the panic
hotkey; revoke while streaming; and expired TURN credentials.

## Security rules
- Control is off by default per device. It is turned on on the PC, never from the phone.
- The PC always shows when it is being viewed or controlled. The panic hotkey always works.
- Tokens are stored only as hashes on the PC and in the platform keystore on the phone.
- Media is DTLS-SRTP end to end. The relay forwards encrypted packets and cannot see the screen.
- The WHEP and bridge endpoints are gated by the session token. Nothing is served on a guessable
  path (as `ShareViewerHost` already does).

## Libraries and licences
| Use | Project | Licence | How |
|---|---|---|---|
| Phone WebRTC | [flutter-webrtc](https://github.com/flutter-webrtc/flutter-webrtc) | MIT | dependency |
| Media server | MediaMTX | MIT | already bundled |
| Relay | coturn | BSD-3 | on our server |
| Direct sender (only if needed) | Pion / SIPSorcery | MIT / BSD-3 | sidecar or dependency |
| Virtual pad | ViGEmBus + Nefarius.ViGEm.Client | BSD-3 / MIT | on-demand driver + NuGet |
| Game Stream host | [Apollo](https://github.com/ClassicOldSong/Apollo) (Sunshine fork) | GPL-3 | downloaded on demand, separate process, never linked or bundled |
| Game Stream client | Artemis / Moonlight | GPL-3 | separate apps, launched |
| Tailscale | Tailscale app; later maybe [`tailscale` Dart package](https://pub.dev/packages/tailscale) | BSD-3 / MIT | Dart package is pre-1.0 and has no Windows support |
| References only | Sunshine, Moonlight, RustDesk, GameNetworkingSockets | GPL-3 / AGPL-3 / BSD-3 | read, do not copy GPL/AGPL code |

## Open decisions (ask before the phase that needs them)
- **Phase 5:** what happens if Artemis/Moonlight cannot be launched straight into a host (found in
  phase 0).
- **Phase 7:** TURN hosting (small VPS with coturn, or Cloudflare's managed TURN), and rendezvous on Supabase Realtime or our own service. The auth backend is decided: Supabase.
- **Phase 7:** accounts change the account-free product decision in
  [SHARE_SOCIAL_PLAN.md](SHARE_SOCIAL_PLAN.md). Do Friends move to accounts too, or stay account-free?
