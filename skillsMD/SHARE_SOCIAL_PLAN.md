# Friends and Screen Share delivery status

Updated: 2026-09-25

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

- Identity is account-free. Saved contacts are local labels plus direct room links. There is no
  cloud friend graph or true online presence.
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
