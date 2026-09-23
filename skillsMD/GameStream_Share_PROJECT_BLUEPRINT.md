# GameStream Share — Project Blueprint

> **Status:** Ready to implement
> **Last verified:** 2026-09-22
> **Primary platform:** Windows 11
> **Primary use case:** Smooth, low-latency gaming screen sharing to one or more viewers
> **Hard requirement:** 1920×1080 @ 60 FPS
> **Optional tier:** 3840×2160 @ 60 FPS
> **Project type:** Personal-use, self-hosted, free/open-source-first

---

# 1. Project Mission

Build a personal gaming-first screen-sharing application that allows a host PC to share gameplay smoothly to remote viewers with very low latency.

This is **not** a Zoom/Teams-style meeting product.

The priorities are:

1. Smooth gaming motion.
2. Stable 60 FPS.
3. Low end-to-end latency.
4. High image quality.
5. Low impact on game performance.
6. Hardware GPU encoding.
7. Multiple viewers.
8. Simple room-code / join-link experience.
9. 1080p60 must work reliably before adding advanced features.
10. 4K60 is optional and must never compromise the 1080p60 baseline.

---

# 2. Non-Negotiable Requirements

## 2.1 Video

### Required

- 1920×1080 output.
- 60 FPS target.
- Hardware accelerated capture where possible.
- Hardware accelerated encoding.
- No intentional 30 FPS fallback during normal operation.
- Stable frame pacing.
- Full-screen monitor capture.
- Select-monitor support.
- Game-focused quality tuning.

### Optional / later

- 2560×1440 @ 60 FPS.
- 3840×2160 @ 60 FPS.
- 1080p120.
- HDR.
- Individual-window capture.
- Region capture.

---

## 2.2 Latency

The design must prioritize realtime delivery.

### Target latency budget

| Stage             |                                Target |
| ----------------- | ------------------------------------: |
| Capture           |                        < 8 ms typical |
| Encode            |                       < 10 ms typical |
| Network           |                      Depends on route |
| Server forwarding | As small as practical; no transcoding |
| Decode            |                       < 10 ms typical |
| Render buffer     |            1–2 frames where possible |

### Practical goals

**LAN**

- Excellent: < 50 ms end-to-end.
- Acceptable: < 80 ms.

**Internet**

- Excellent: < 100 ms when network RTT is low.
- Acceptable gaming-view latency: roughly 100–200 ms depending on route.

Do not artificially add multi-second buffering.

---

# 3. Core Design Rule

## FPS is more important than resolution.

When bandwidth or packet loss becomes problematic:

```text
1080p60
   ↓
900p60
   ↓
720p60
```

Preferred over:

```text
1080p60
   ↓
1080p45
   ↓
1080p30
```

For games, motion smoothness is a primary requirement.

---

# 4. Initial Supported Quality Profiles

## Balanced — default

```yaml
resolution: 1920x1080
fps: 60
video_codec: H264
bitrate_target: 16 Mbps
bitrate_min: 8 Mbps
bitrate_max: 22 Mbps
keyframe_interval: 1 second
b_frames: 0
audio_codec: Opus
audio_bitrate: 160 kbps
audio_sample_rate: 48000
```

## High

```yaml
resolution: 1920x1080
fps: 60
video_codec: H264
bitrate_target: 22 Mbps
bitrate_min: 12 Mbps
bitrate_max: 30 Mbps
keyframe_interval: 1 second
b_frames: 0
audio_codec: Opus
audio_bitrate: 192 kbps
audio_sample_rate: 48000
```

## 1440p60 — later

```yaml
resolution: 2560x1440
fps: 60
video_codec: H264_or_HEVC
bitrate_target: 28 Mbps
bitrate_max: 40 Mbps
```

## 4K60 — optional

```yaml
resolution: 3840x2160
fps: 60
video_codec: HEVC_or_AV1_preferred
bitrate_target: 45 Mbps
bitrate_range: 30-70 Mbps
```

4K60 must only appear as available when:

- GPU encoder supports it.
- Viewer decoder supports it.
- Network test passes.
- Capture path sustains 60 FPS.
- Encoder does not overload.
- No significant game-performance regression is detected.

---

# 5. Codec Strategy

## Version 1

Use **H.264** as the mandatory baseline.

Why:

- Widest hardware decoding support.
- Widest browser compatibility.
- Easy WebRTC interoperability.
- Mature hardware encoding.
- Excellent for 1080p60.

## Version 2

Add:

- HEVC / H.265.
- AV1.

Use HEVC/AV1 when both sender and receiver support them.

### Preferred order

```text
AV1
↓
HEVC
↓
H.264
```

for compression efficiency.

For compatibility:

```text
H.264 = mandatory fallback
```

---

# 6. Hardware Encoder Priority

Detect available encoder automatically.

Order:

```text
NVIDIA NVENC
AMD AMF
Intel Quick Sync / QSV
Software encoder only as emergency fallback
```

Software encoding must not be the default for gaming.

The host UI must show:

```text
Encoder: NVIDIA NVENC
```

or equivalent.

---

# 7. Windows Capture Architecture

Use a GPU-native Windows capture pipeline.

## Primary choices

1. DXGI Desktop Duplication.
2. Windows Graphics Capture.

For the first gaming-focused build, start with **DXGI Desktop Duplication**.

FFmpeg's current `ddagrab` implementation is useful as both:

- a prototype mechanism;
- a reference implementation.

It captures through the Windows Desktop Duplication API and returns Direct3D 11 hardware frames, allowing an on-GPU path into hardware encoding.

### Performance rule

Avoid this:

```text
GPU
↓
CPU RAM
↓
GPU
↓
Encoder
```

Prefer:

```text
Game GPU framebuffer
↓
D3D11 texture
↓
GPU color conversion / resize
↓
NVENC / AMF / QSV
```

The long-term implementation should minimize unnecessary GPU→CPU→GPU frame copies.

---

# 8. Audio

Required:

- Game / system audio.
- Stereo.
- 48 kHz.
- Audio/video synchronization.

Use **WASAPI loopback capture** for Windows system/game audio.

Later:

- Microphone toggle.
- Separate microphone volume.
- Separate game volume.
- Push-to-talk.
- Viewer voice chat.

Preferred transport codec:

```text
Opus
48 kHz
128–192 kbps stereo
```

---

# 9. Recommended Architecture

```text
┌───────────────────────────────────────────────┐
│               HOST WINDOWS PC                │
│                                               │
│  Game                                         │
│    ↓                                          │
│  DXGI Desktop Duplication / WGC               │
│    ↓                                          │
│  D3D11 GPU texture                            │
│    ↓                                          │
│  Resize / pixel conversion on GPU             │
│    ↓                                          │
│  NVENC / AMF / QSV                            │
│    ↓                                          │
│  H.264 / HEVC / AV1                           │
│                                               │
│  WASAPI loopback → Opus                       │
└────────────────────┬──────────────────────────┘
                     │
                     │ low-latency uplink
                     ▼
┌───────────────────────────────────────────────┐
│             MEDIA ROUTER / SERVER             │
│                                               │
│                MediaMTX                       │
│                                               │
│   IMPORTANT: ROUTE/FORWARD MEDIA ONLY         │
│   DO NOT TRANSCODE NORMAL STREAMS             │
└───────┬──────────────────┬────────────────────┘
        │                  │
        ▼                  ▼
┌──────────────┐    ┌──────────────┐
│ Viewer #1    │    │ Viewer #2    │
│ WebRTC/WHEP  │    │ WebRTC/WHEP  │
│ HW decoding  │    │ HW decoding  │
└──────────────┘    └──────────────┘
        │                  │
        ▼                  ▼
   Viewer #3          Viewer #4
```

The host should upload **one main stream** to the media server whenever possible.

The server distributes the stream to viewers.

This avoids:

```text
Host → viewer 1 = 20 Mbps
Host → viewer 2 = 20 Mbps
Host → viewer 3 = 20 Mbps
Host → viewer 4 = 20 Mbps
```

which would require 80 Mbps of host upload.

Instead:

```text
Host → server = ~20 Mbps
Server → each viewer
```

---

# 10. Media Server

## Initial choice: MediaMTX

Use MediaMTX as the first self-hosted media router.

Reasons:

- Open source.
- MIT licensed.
- Single executable.
- Windows/Linux/macOS support.
- WebRTC support.
- WHIP/WHEP support.
- SRT support.
- H.264, H.265 and AV1 support in appropriate protocols.
- Browser playback support through WebRTC.
- Authentication support.
- Metrics/control API.
- No need to build an SFU/media router from zero during MVP.

### Important

The media server should **not transcode** the gaming stream during normal operation.

Transcoding increases:

- latency;
- CPU/GPU usage;
- complexity;
- frame delay.

Prefer packet forwarding / protocol routing.

---

# 11. Why Not Build Everything From Zero?

Do not implement these from scratch during MVP:

- ICE.
- STUN.
- TURN.
- DTLS.
- SRTP.
- WebRTC congestion-control basics.
- Browser media decoder.
- Full SFU.
- NAT traversal stack.

Use tested open-source components.

We are building the **product and optimized capture/encode path**, not reimplementing the internet.

---

# 12. Reference Projects

## Sunshine

Reference:

https://github.com/LizardByte/Sunshine

Use it as an architectural/performance reference for:

- low-latency game streaming;
- GPU hardware encoding;
- Windows screen capture;
- encoder tuning;
- game-oriented latency behavior.

Do **not copy Sunshine code into this project casually**.

Sunshine is GPLv3. If code is reused/derived, GPL obligations apply.

Architectural study is fine. Treat licensing deliberately.

---

## Moonlight

Reference:

https://github.com/moonlight-stream

Study:

- hardware decoding;
- low-latency presentation;
- performance metrics;
- game-stream client behavior.

---

## MediaMTX

Reference:

https://github.com/bluenviron/mediamtx

Use initially as the media router/server.

---

## FFmpeg

Reference:

https://ffmpeg.org/

Useful for:

- prototype capture;
- `ddagrab`;
- GPU encoder testing;
- codec experiments;
- diagnostics.

Current FFmpeg `ddagrab` captures the Windows desktop using Desktop Duplication and emits D3D11 hardware frames.

---

## libdatachannel

Reference:

https://github.com/paullouisageneau/libdatachannel

Possible future option if we replace more of the transport layer with our own native WebRTC implementation.

---

## LiveKit

Reference:

https://github.com/livekit/livekit

Do not make this a mandatory MVP dependency.

Evaluate it later if we need:

- rooms;
- participant management;
- scalable SFU infrastructure;
- simulcast;
- selective subscriptions;
- TURN/UDP/TCP infrastructure;
- advanced WebRTC features.

---

# 13. Technology Stack

## Host application

### Recommended

```text
Language: C++20
Build: CMake
OS: Windows 11
Graphics: D3D11 / DXGI
Capture: Desktop Duplication initially
Audio: WASAPI
Encoding:
  NVIDIA → NVENC
  AMD → AMF
  Intel → QSV
Transport prototype:
  external MediaMTX + standards-based ingest
```

Why C++:

- Direct access to Direct3D.
- Direct GPU SDK support.
- Mature Windows APIs.
- Lowest overhead.
- Best control of frame lifetime.
- Easier zero-copy optimization.

---

## Host UI

For MVP keep UI simple.

Options:

### Preferred

- WinUI 3 front-end with native C++ core.

Alternative:

- Qt 6.

Do not build a heavy Electron host for the performance-critical application.

---

## Viewer

### MVP

Browser viewer.

```text
HTML
CSS
TypeScript
WebRTC / WHEP
```

Benefits:

- no viewer installation;
- easy testing;
- easy sharing;
- hardware decoding in modern browsers.

### Later

Native viewer:

```text
C++ / Rust
D3D11 rendering
hardware decoder
fullscreen
advanced latency controls
```

---

# 14. Repository Structure

Create:

```text
gamestream-share/
│
├─ README.md
├─ PROJECT_BLUEPRINT.md
├─ LICENSE
├─ .gitignore
├─ CMakeLists.txt
│
├─ docs/
│  ├─ architecture.md
│  ├─ protocol.md
│  ├─ performance.md
│  ├─ testing.md
│  └─ troubleshooting.md
│
├─ host/
│  ├─ CMakeLists.txt
│  │
│  ├─ src/
│  │  ├─ main.cpp
│  │  │
│  │  ├─ capture/
│  │  │  ├─ capture_manager.cpp
│  │  │  ├─ dxgi_capture.cpp
│  │  │  └─ display_enumerator.cpp
│  │  │
│  │  ├─ video/
│  │  │  ├─ encoder.cpp
│  │  │  ├─ nvenc_encoder.cpp
│  │  │  ├─ amf_encoder.cpp
│  │  │  ├─ qsv_encoder.cpp
│  │  │  └─ video_scaler.cpp
│  │  │
│  │  ├─ audio/
│  │  │  ├─ wasapi_loopback.cpp
│  │  │  └─ audio_encoder.cpp
│  │  │
│  │  ├─ transport/
│  │  │  ├─ publisher.cpp
│  │  │  ├─ reconnect.cpp
│  │  │  └─ congestion_controller.cpp
│  │  │
│  │  ├─ telemetry/
│  │  │  ├─ statistics.cpp
│  │  │  └─ latency_tracker.cpp
│  │  │
│  │  └─ config/
│  │     └─ settings.cpp
│  │
│  └─ include/
│
├─ viewer/
│  ├─ package.json
│  ├─ src/
│  │  ├─ main.ts
│  │  ├─ player.ts
│  │  ├─ stats.ts
│  │  └─ ui.ts
│  └─ public/
│     └─ index.html
│
├─ server/
│  ├─ mediamtx.yml
│  ├─ docker-compose.yml
│  └─ README.md
│
├─ scripts/
│  ├─ setup-windows.ps1
│  ├─ run-dev.ps1
│  ├─ benchmark.ps1
│  └─ package.ps1
│
└─ tests/
   ├─ capture/
   ├─ encoder/
   ├─ network/
   └─ end_to_end/
```

---

# 15. Application UX

## Host

```text
╔════════════════════════════════════════════╗
║             GAMESTREAM SHARE               ║
╠════════════════════════════════════════════╣
║ Display       [ Display 1              ▼ ] ║
║                                            ║
║ Resolution    [ 1920×1080              ▼ ] ║
║ FPS           [ 60                     ▼ ] ║
║ Quality       [ High                   ▼ ] ║
║ Codec         [ H.264                  ▼ ] ║
║ Encoder       [ NVIDIA NVENC           ▼ ] ║
║                                            ║
║ Game Audio     [ ON ]                     ║
║ Microphone     [ OFF ]                    ║
║                                            ║
║             [ START STREAM ]               ║
║                                            ║
║ Room: 482-731                              ║
║ Viewers: 3                                 ║
║                                            ║
║ 1080p60 • 17.8 Mbps • Encoder 4.1 ms       ║
║ RTT 31 ms • Loss 0.1% • Drops 0.0%         ║
╚════════════════════════════════════════════╝
```

---

## Viewer

```text
╔══════════════════════════════╗
║       GAMESTREAM SHARE       ║
║                              ║
║ Room code                    ║
║ [ 482-731                  ] ║
║                              ║
║          [ JOIN ]            ║
╚══════════════════════════════╝
```

After joining:

```text
Fullscreen gaming video
1080p60
Game audio

Optional stats overlay:
FPS
resolution
bitrate
RTT
packet loss
decoder latency
dropped frames
```

---

# 16. Performance Telemetry

Never debug streaming quality by visual feeling alone.

Measure:

```text
Capture FPS
Capture frame time
Encode FPS
Encode latency
Encoded bitrate
Upload bitrate
Network RTT
Jitter
Packet loss
Retransmissions
Decode latency
Render FPS
Frames dropped by capture
Frames dropped by encoder
Frames dropped by network
Frames dropped by decoder
End-to-end frame latency
```

Host overlay example:

```text
CAPTURE
60.00 FPS
3.2 ms

ENCODE
NVENC H.264
4.8 ms
18.3 Mbps

NETWORK
RTT 29 ms
Jitter 2.1 ms
Loss 0.12%

VIEWER
60 FPS
Decode 3.6 ms
Dropped 0.04%
```

---

# 17. Adaptive Quality Logic

Pseudo-logic:

```cpp
if (packetLoss < 1.0 && rttStable && encoderHealthy) {
    slowlyIncreaseBitrate();
}

if (packetLoss > 2.0 || repeatedQueueGrowth()) {
    reduceBitrate(10_percent);
}

if (networkStillBadForSeveralSeconds()) {
    reduceResolution();
}

KEEP_TARGET_FPS(60);
```

Quality ladder:

```text
1080p60 High
1080p60 Balanced
900p60
720p60
```

4K profile:

```text
4K60
↓
1440p60
↓
1080p60
```

Never immediately destroy picture quality because of a single short network spike.

Use hysteresis.

---

# 18. Encoder Tuning Principles

Gaming realtime mode:

- Hardware encoder.
- Low-latency preset.
- No look-ahead where it creates meaningful delay.
- B-frames disabled initially.
- Small VBV buffer.
- 1-second keyframe interval initially.
- CBR or low-latency constrained VBR.
- Realtime priority.
- Avoid multipass features that add significant latency.

Do not blindly optimize for PSNR/VMAF.

Optimize for:

```text
motion quality
+
frame latency
+
stable delivery
```

---

# 19. Bandwidth Planning

Approximate planning values:

| Mode             | Suggested upload target |
| ---------------- | ----------------------: |
| 720p60           |              6–10 Mbps |
| 1080p60 Balanced |             12–18 Mbps |
| 1080p60 High     |             18–30 Mbps |
| 1440p60          |             25–40 Mbps |
| 4K60 HEVC/AV1    |             35–70 Mbps |

These are project design ranges, not universal guarantees.

A network speed test alone is insufficient.

Also test:

- packet loss;
- jitter;
- latency;
- sustained upload;
- Wi-Fi congestion.

Prefer Ethernet for the host.

---

# 20. Multi-Viewer Rules

Initial target:

```text
1 host
1–4 viewers
```

Later:

```text
10+ viewers
```

The host must not create a separate full-resolution encode for every viewer.

Preferred:

```text
ONE encode
ONE uplink
SERVER fan-out
```

If adaptive per-viewer quality becomes necessary later, add:

- simulcast; or
- scalable video coding; or
- server-side optional transcode workers.

Do not add those to MVP unless required.

---

# 21. Security

Even for personal use:

- random room IDs;
- random stream tokens;
- short-lived access tokens;
- TLS for public internet;
- authentication for publisher;
- viewer authorization;
- no unauthenticated admin API exposure;
- TURN credentials if TURN is deployed;
- rate-limit join attempts;
- never expose debug/admin ports publicly without protection.

Example:

```text
Room ID:
482731

Viewer token:
random cryptographically secure token

Publisher token:
different random token
```

Do not use a six-digit room code as the only security control over the internet.

---

# 22. Development Phases

# PHASE 0 — Prove the PC and network

Goal:

Verify that the machine can sustain gaming capture at 1080p60 before writing large amounts of code.

Tasks:

- Install latest GPU driver.
- Install recent FFmpeg build.
- Verify 60 FPS display capture.
- Verify GPU encoder.
- Record 1080p60 locally.
- Play a demanding game during capture.
- Record:
  - game FPS;
  - capture FPS;
  - encoder latency;
  - GPU utilization;
  - dropped frames.

### NVIDIA prototype capture

Use FFmpeg `ddagrab` as a diagnostic starting point.

Example concept:

```powershell
ffmpeg -f lavfi -i "ddagrab=output_idx=0:framerate=60" `
       -c:v h264_nvenc `
       -an `
       test-1080p60.mkv
```

This command is a diagnostic baseline, not the final production pipeline.

### PHASE 0 PASS

- Capture reports ~60 FPS.
- Recorded video is actually smooth.
- Game remains playable.
- No sustained encoder overload.
- No large frame-drop bursts.

---

# PHASE 1 — Native capture MVP

Goal:

Render the captured desktop inside our own host application.

Implement:

- D3D11 device.
- DXGI adapter enumeration.
- Monitor enumeration.
- Desktop Duplication.
- Frame acquisition thread.
- FPS limiter / timing.
- D3D11 preview.
- dropped-frame counter.

No networking yet.

### PASS

- 1080p60 capture works for 30 minutes.
- No memory leak.
- No increasing GPU memory usage.
- No obvious stutter.
- Display reconnect works after fullscreen games / mode changes.

---

# PHASE 2 — GPU encoder

Goal:

Encode the captured frame without unnecessary CPU copies.

Implement:

1. NVIDIA NVENC first.
2. AMD AMF second.
3. Intel QSV third.

Output:

```text
H.264 Annex-B or transport-compatible encoded stream
```

Add:

- IDR request.
- bitrate change at runtime.
- resolution change.
- FPS stats.
- encode-time stats.

### PASS

1080p60:

- stable;
- encoder average safely below frame budget;
- no sustained output queue;
- minimal game FPS impact.

---

# PHASE 3 — System audio

Implement:

- WASAPI loopback;
- ring buffer;
- timestamping;
- Opus encode;
- sync clock.

### PASS

- Game audio audible.
- Lip/game synchronization remains stable over 30 minutes.
- No drift.

---

# PHASE 4 — Media server

Run MediaMTX locally first.

Configure one test path:

```text
game
```

Verify:

```text
publisher
→ MediaMTX
→ one viewer
```

Then:

```text
publisher
→ MediaMTX
→ four viewers
```

### PASS

- Adding viewers does not materially increase host encoder load.
- Host upload remains approximately one stream.
- Viewers receive smooth 1080p60.
- Server does not transcode during normal operation.

---

# PHASE 5 — Browser viewer

Implement:

- join page;
- WHEP/WebRTC connection;
- fullscreen video;
- audio;
- reconnect;
- stats overlay;
- keyboard shortcut to toggle stats.

Viewer stats:

```text
resolution
FPS
bitrate
RTT
jitter
packet loss
frames dropped
decoder performance
```

### PASS

Chrome/Edge:

- receives 1080p60;
- hardware decode active where supported;
- no large playback buffer;
- recovers after brief connection interruption.

---

# PHASE 6 — Host UI

Controls:

```text
Display
Resolution
FPS
Quality
Codec
Encoder
System Audio
Microphone
Start Stream
Stop Stream
Copy Viewer Link
Copy Room Code
```

Status:

```text
LIVE
duration
viewer count
resolution/FPS
bitrate
encoder
encode latency
packet loss
RTT
dropped frames
```

---

# PHASE 7 — Internet mode

Deploy MediaMTX to a VPS or another reachable server.

Configure:

- TLS.
- secure auth.
- firewall.
- WebRTC ports.
- TURN only if needed.

Test:

```text
Host on home internet
Viewer on different ISP / mobile network
```

### PASS

- stable 1080p60 on suitable bandwidth;
- reconnect works;
- no public unauthenticated publishing.

---

# PHASE 8 — Adaptive bitrate

Implement network feedback.

First tune bitrate.

Then add resolution ladder:

```text
1080p60
900p60
720p60
```

60 FPS remains the target.

---

# PHASE 9 — 4K60

Only begin after 1080p60 is production-quality.

Requirements:

- compatible capture resolution;
- GPU can sustain encode;
- viewer can sustain hardware decode;
- network test passes;
- no encoder overload;
- no significant dropped frames.

Add:

```text
3840×2160 @ 60
HEVC
AV1 where available
```

Maintain H.264 1080p60 fallback.

---

# 23. Threading Model

Suggested host threads:

```text
UI THREAD
    │
    ├─ Capture thread
    │
    ├─ Encoder thread
    │
    ├─ Audio capture thread
    │
    ├─ Audio encode thread
    │
    ├─ Network/transport thread
    │
    └─ Telemetry thread
```

Do not perform capture or encoding work on the UI thread.

Use bounded queues.

If an old video frame is late, prefer dropping the stale frame over building latency.

Bad:

```text
frame queue:
1
2
3
4
5
6
7
8
```

Good realtime behavior:

```text
frame queue:
current
next

late old frame → DROP
```

---

# 24. Frame Timestamping

Use a monotonic high-resolution clock.

On Windows:

```text
QueryPerformanceCounter
```

Each captured frame gets:

```cpp
struct VideoFrameTimestamp {
    uint64_t capture_qpc;
    uint64_t encode_start_qpc;
    uint64_t encode_end_qpc;
    uint64_t send_qpc;
};
```

Use this for latency diagnostics.

---

# 25. Failure Handling

Handle:

- monitor disconnected;
- game changes fullscreen mode;
- display resolution changes;
- GPU driver reset;
- encoder unavailable;
- network reconnect;
- media server restart;
- audio device change;
- viewer disconnect/reconnect;
- Windows lock/unlock;
- PC sleep/resume.

Never crash because a capture surface temporarily disappears.

---

# 26. Logging

Use structured logs.

Example:

```text
2026-09-22T22:45:12.182
capture.frame
fps=60.0
capture_ms=2.8

2026-09-22T22:45:12.186
encoder.frame
encoder=nvenc
encode_ms=4.1
size=37182

2026-09-22T22:45:12.188
network.send
bitrate_mbps=17.9
queue_ms=1.2
```

Logging levels:

```text
ERROR
WARN
INFO
DEBUG
TRACE
```

---

# 27. Benchmark Games

Use more than one workload.

Test:

- GTA V / GTA Online.
- DCS World.
- fast camera-motion game.
- dark game scene.
- grass/foliage-heavy scene.
- high-particle-effects scene.

Why:

Video encoders behave differently on different image complexity.

---

# 28. Mandatory Test Matrix

## Resolution

- 1920×1080 @ 60 — REQUIRED.
- 2560×1440 @ 60 — later.
- 3840×2160 @ 60 — optional.

## GPU vendors

Eventually:

- NVIDIA.
- AMD.
- Intel.

MVP may begin with the developer's own GPU.

## Viewer

- Edge.
- Chrome.
- native viewer later.

## Network

- same PC.
- same LAN.
- Ethernet.
- Wi-Fi.
- internet.
- simulated packet loss.
- simulated jitter.
- limited bandwidth.

---

# 29. Definition of Done — 1080p60

The 1080p60 milestone is **NOT complete** merely because the UI says 60 FPS.

It is complete when all of these pass:

- [ ] Host captures 1920×1080.
- [ ] Capture stays approximately 60 FPS.
- [ ] Hardware encoder is active.
- [ ] Viewer renders approximately 60 FPS.
- [ ] System/game audio works.
- [ ] Audio remains synchronized.
- [ ] Stream runs 60 minutes.
- [ ] No growing latency over time.
- [ ] No major memory leak.
- [ ] No sustained encoder overload.
- [ ] No significant dropped-frame bursts on a healthy LAN.
- [ ] Game performance impact is acceptable.
- [ ] Viewer can reconnect.
- [ ] Four viewers can connect through the media server.
- [ ] Host does not perform four independent full-quality encodes.
- [ ] Host uplink does not scale linearly with viewer count in server-fanout mode.
- [ ] Stats accurately show FPS/bitrate/drops/latency indicators.

---

# 30. Definition of Done — 4K60

4K60 may be marked supported only when:

- [ ] 3840×2160 capture works.
- [ ] 60 FPS is sustained.
- [ ] hardware encoder handles the workload.
- [ ] viewer hardware decode works.
- [ ] network sustains selected bitrate.
- [ ] server does not transcode.
- [ ] 30-minute test has no growing latency.
- [ ] fallback to 1440p60 or 1080p60 works.

---

# 31. MVP Scope

## INCLUDE

- Windows host.
- Display capture.
- 1080p60.
- H.264.
- NVIDIA NVENC first.
- System audio.
- One stream.
- MediaMTX server.
- Browser viewer.
- Multiple viewers.
- Basic authentication.
- Stream stats.
- Fullscreen viewer.

## DO NOT INCLUDE YET

- remote keyboard/mouse.
- voice chat.
- chat messages.
- webcam.
- recording.
- clips.
- overlays.
- friend system.
- user accounts.
- cloud database.
- mobile app.
- PS5 capture.
- HDR.
- 4K before 1080p60 is stable.

Avoid feature creep.

---

# 32. Future Features

After the MVP:

```text
4K60
1440p60
1080p120
AV1
HEVC
HDR
remote keyboard/mouse
gamepad forwarding
native viewer
microphone/voice chat
recording
instant replay
multiple monitor selection
window capture
Tailscale/direct mode
P2P mode
TURN
simulcast
adaptive per-viewer layers
```

---

# 33. Performance Priority Order

When making an engineering decision, use this order:

```text
1. No crashes
2. Stable capture
3. Stable 60 FPS
4. Low latency
5. No growing buffers
6. Low game-performance impact
7. Audio sync
8. Picture quality
9. Bandwidth efficiency
10. Extra features
```

---

# 34. Important Engineering Rules

1. Never let queues grow indefinitely.
2. Drop stale video frames rather than adding seconds of latency.
3. Keep frames on the GPU whenever practical.
4. Use hardware encoding.
5. Do not transcode on the routing server by default.
6. Instrument everything.
7. Test under actual gaming load.
8. Never declare success based only on an FPS counter.
9. 1080p60 must remain the compatibility baseline.
10. 4K60 is an enhancement, not the foundation.
11. Network adaptation should preserve motion smoothness.
12. Build a minimal end-to-end vertical slice before adding UI polish.

---

# 35. FIRST DEVELOPMENT SPRINT

This is the exact order to start work.

## Task 1 — Repository

```powershell
mkdir gamestream-share
cd gamestream-share
git init
mkdir host
mkdir viewer
mkdir server
mkdir docs
mkdir scripts
mkdir tests
```

Add:

```text
README.md
PROJECT_BLUEPRINT.md
.gitignore
CMakeLists.txt
```

Commit:

```powershell
git add .
git commit -m "Initial GameStream Share project structure"
```

---

## Task 2 — Capture diagnostic

Install a recent FFmpeg build.

Check available filters:

```powershell
ffmpeg -filters | findstr ddagrab
```

Check NVIDIA encoder:

```powershell
ffmpeg -encoders | findstr nvenc
```

AMD:

```powershell
ffmpeg -encoders | findstr amf
```

Intel:

```powershell
ffmpeg -encoders | findstr qsv
```

Record a short 1080p60 test while a game is running.

Review:

- actual FPS;
- dropped frames;
- game FPS impact;
- visual smoothness.

Document results in:

```text
docs/performance.md
```

---

## Task 3 — C++ capture prototype

Create:

```text
host/src/capture/dxgi_capture.cpp
host/src/capture/display_enumerator.cpp
```

Implement:

```text
CreateDXGIFactory
enumerate adapters
enumerate outputs
D3D11CreateDevice
IDXGIOutput1::DuplicateOutput
AcquireNextFrame
ReleaseFrame
```

Capture into `ID3D11Texture2D`.

Print:

```text
capture FPS
frame time
dropped frames
```

Do not implement streaming yet.

---

## Task 4 — Preview window

Render the acquired texture to a local preview.

Target:

```text
1920×1080
60 FPS
```

Run for 30 minutes.

Fix capture stability before continuing.

---

## Task 5 — NVENC

Implement NVIDIA first if an NVIDIA GPU is available.

Input:

```text
D3D11 texture
```

Output:

```text
H.264 encoded frame
```

Target:

```text
1080p60
low-latency preset
B-frames = 0 initially
~16–22 Mbps
```

Measure encode time.

---

## Task 6 — MediaMTX

Download MediaMTX.

Place its config in:

```text
server/mediamtx.yml
```

First run locally.

Use a test publisher before integrating the custom native sender.

Validate browser WebRTC playback.

---

## Task 7 — Connect custom sender

Connect encoded media from the host to the MediaMTX ingest path.

Do not add accounts, fancy UI or remote control until this path works:

```text
GAME
↓
CAPTURE
↓
ENCODE
↓
SERVER
↓
BROWSER
```

at:

```text
1080p60
```

---

# 36. FIRST MAJOR MILESTONE

Name:

```text
M1 — 1080p60 Gaming Stream
```

Required demonstration:

```text
GTA V / another demanding game
running on Host PC
↓
1920×1080 @ 60 FPS capture
↓
hardware encoding
↓
MediaMTX
↓
different viewer PC
↓
fullscreen smooth playback
```

with telemetry visible.

Do not start 4K work before M1 passes.

---

# 37. Coding-Agent Instructions

The following block can be given directly to a coding agent.

---

## AGENT EXECUTION DIRECTIVE

You are implementing the **GameStream Share** project defined by this document.

### Primary objective

Build a Windows-first, low-latency gaming screen-sharing system.

### Hard requirement

```text
1920×1080 @ 60 FPS
```

must be the minimum supported high-quality stream.

### Optional requirement

```text
3840×2160 @ 60 FPS
```

will be added only after the 1080p60 pipeline is stable.

### Priorities

```text
stable 60 FPS
low latency
GPU capture
GPU encode
low game-performance impact
audio sync
multiple viewers
```

### Initial stack

```text
Windows 11
C++20
CMake
D3D11
DXGI Desktop Duplication
WASAPI
NVENC first
AMF later
QSV later
H.264 baseline
MediaMTX
WebRTC/WHEP browser viewer
TypeScript viewer
```

### Development rule

Work milestone-by-milestone.

Do not implement future features while the current critical path is broken.

Critical path:

```text
capture
→ encode
→ transport
→ server
→ viewer
```

### Performance rule

All queues must be bounded.

For realtime video:

```text
DROP OLD FRAME
```

is preferred to:

```text
BUFFER OLD FRAME AND INCREASE LATENCY
```

### Verification rule

Every milestone must produce measurable telemetry.

Never claim 1080p60 success unless:

- actual capture is ~60 FPS;
- actual viewer render is ~60 FPS;
- no sustained encoder overload;
- no growing buffer;
- no major frame-drop bursts on a healthy network.

### First task

Implement the DXGI Desktop Duplication capture prototype and display:

```text
monitor name
capture resolution
capture FPS
frame time
dropped frames
```

Do not begin networking until capture remains stable for a 30-minute gaming test.

---

# 38. Suggested Version Roadmap

```text
v0.0.1
DXGI capture prototype

v0.0.2
local 1080p60 preview

v0.0.3
NVENC H.264

v0.0.4
WASAPI system audio

v0.0.5
MediaMTX publishing

v0.0.6
browser viewer

v0.1.0
1080p60 end-to-end MVP

v0.2.0
multi-viewer + authentication

v0.3.0
adaptive bitrate/resolution

v0.4.0
AMD + Intel encoders

v0.5.0
native viewer

v0.6.0
HEVC / AV1

v0.7.0
1440p60

v1.0.0
stable personal release

v1.1+
4K60
```

---

# 39. Current Technical References

Verified 2026-09-22.

### Sunshine

https://github.com/LizardByte/Sunshine

Current project documentation describes Sunshine as a self-hosted low-latency game-streaming host with hardware encoding support for AMD, Intel and NVIDIA GPUs.

### MediaMTX

https://github.com/bluenviron/mediamtx

Documentation:

https://mediamtx.org/docs/

Current documentation supports realtime media routing using protocols including WebRTC, SRT, RTSP, RTMP and others. Browser WebRTC viewing and WHEP are supported.

### FFmpeg Desktop Duplication

https://ffmpeg.org/

`ddagrab` documentation:

https://ayosec.github.io/ffmpeg-filters-docs/8.0/Sources/Video/ddagrab.html

The filter captures the Windows desktop using Desktop Duplication and emits D3D11 hardware frames suitable for GPU-side encoding.

### Microsoft Windows Graphics Capture

https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture

Useful alternative/future capture API.

### NVIDIA Video Codec SDK

https://developer.nvidia.com/video-codec-sdk

Use for direct NVENC/NVDEC integration and detailed encoder control.

### libdatachannel

https://github.com/paullouisageneau/libdatachannel

Potential lightweight native WebRTC transport library if more custom transport work is needed.

### LiveKit

https://github.com/livekit/livekit

Potential later alternative for a larger room/SFU architecture.

---

# 40. Licensing Notes

Before distributing binaries, review licenses carefully.

Current references:

- Sunshine: GPLv3.
- MediaMTX: MIT.
- libdatachannel: MPL 2.0.
- FFmpeg: primarily LGPL 2.1+ by default, but configuration/options can make GPL apply.

For personal development this is straightforward, but do not copy GPL source into a differently licensed project without understanding the obligations.

This document is an engineering plan, not legal advice.

---

# 41. Final Project Rule

If a feature harms:

```text
1080p60 stability
or
low latency
```

it does not ship until fixed.

The project succeeds first as:

> **A smooth, low-latency 1080p60 gaming screen-share system.**

Everything else comes after that.

for reference-
Add as friends is a good option
call and screen share both to be implimented
https://github.com/steveseguin/vdo.ninja.git
https://github.com/moonlight-stream/moonlight-qt.git
