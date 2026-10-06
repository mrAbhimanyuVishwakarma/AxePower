# Share Screen threat model

**Scope:** Share Screen video, room chat and files, room voice, and remote control
**Last updated:** 2026-10-04
**Status:** Describes the code in this repository. Every mitigation listed as *implemented* has a
test named in [§8](#8-verification). Anything not implemented is listed as *residual* in
[§6](#6-residual-risk) instead of being described as a mitigation.

> An earlier version of this file, written the same day, claimed protections the code did not
> have: chat/voice encryption that a network observer could undo, replay protection that could be
> bypassed, a host-side viewer list that no UI showed, and a public-network warning that did not
> exist. This version replaces it. If you change Share Screen, keep this file true.

---

## 1. What is being protected

| Asset | Why it matters |
|---|---|
| The host's keyboard and mouse | Remote control is full control of the signed-in Windows session. This is the highest-value asset. |
| The host's screen | Anything on screen: mail, passwords being typed, documents. |
| Room chat and file attachments | Private conversation and files between friends. |
| Room voice | Live audio from the host and participants. |
| Session secrets | They gate everything above (see below). |

### Session secrets

Created per room by `ShareRoomFactory` and the viewer host, from `RandomNumberGenerator`:

| Secret | Size | Purpose | Where it lives |
|---|---|---|---|
| `PathName` | 80 bits (`axe` + 20 hex) | Names the room. Required for every request. | In the viewer URL path. **Visible to a network observer** (plain HTTP). |
| `ViewerToken` | 128 bits (32 hex) | Proves a participant was given the link; root of the chat, voice and control keys. | In the URL fragment `#v=`, which browsers never send. Since this change it is **never sent on the wire** by AxePower or the viewer page. |
| Control code | 8 symbols from a 31-symbol alphabet, about 39.6 bits | The host reads it out to the one person who may take control. Issued each time control is switched on. | Typed by the controller. **Never sent on the wire.** |
| `PublisherToken` | 128 bits | FFmpeg's credential to publish into MediaMTX. | Loopback only. |

## 2. Components and trust boundaries

```
 Host PC                                                             Network (untrusted)        Viewer
 ┌─────────────────────────────────────────────────────────────┐
 │ FFmpeg ──RTSP (loopback, publisher creds)──► MediaMTX        │◄── WHEP over HTTP ─────── browser / AxePower
 │                                              :WebrtcPort     │    WebRTC media (DTLS-SRTP)
 │                                                              │
 │ ShareViewerHost  :ViewerPort  (raw HTTP + WebSocket)         │
 │   GET  /{path}           viewer page                         │◄── HTTP ───────────────── browser
 │   GET  /{path}/nonce     single-use challenge                │◄── HTTP ───────────────── browser / AxePower
 │   WS   /{path}/ws        remote control  ──► RemoteInputService ──► SendInput
 │   WS   /{path}/chat      chat + files (AES-GCM, relayed)     │◄── ws:// ──────────────── AxePower
 │   WS   /{path}/voice     Opus voice (AES-GCM, relayed)       │◄── ws:// ──────────────── AxePower
 └─────────────────────────────────────────────────────────────┘
```

Trust boundaries:

1. **Host session ↔ network.** Everything arriving on `ViewerPort` and `WebrtcPort` is untrusted
   until it proves knowledge of the session secrets.
2. **Viewer ↔ controller.** Holding the link makes you a viewer. It must never make you a
   controller: that needs the control code *and* the host having switched control on.
3. **The viewer's browser ↔ other sites in that browser.** A page on another origin can open
   WebSockets to the host (CSWSH) or rebind a hostname to the host's address (DNS rebinding).

## 3. Threat actors

| Actor | Capabilities assumed |
|---|---|
| **A1 Passive network observer** | Sees all LAN traffic (open Wi-Fi, a shared hotel or office network, a compromised router). Cannot modify it. |
| **A2 Active network attacker** | A1 plus ARP/DNS spoofing, injecting and dropping packets, man-in-the-middle. |
| **A3 Malicious website** | Runs script in a browser on the host's or a viewer's network: cross-site WebSockets, DNS rebinding. |
| **A4 Link holder** | A legitimate or former viewer who has the full link, including `#v=`. May try to take control or keep access after leaving. |
| **A5 Scanner** | Anyone on the network probing ports, without any secret. |

Out of scope: malware already running on the host (it does not need Share Screen), and the
security of Tailscale or of the friend's own PC.

## 4. Threats and mitigations

Status: **Implemented** means in code and covered by a test in §8. **Partial** and **Residual**
are explained in §6.

### 4.1 Exposure on untrusted networks (prompt item 1)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| N1 | Host shares on a Public or unclassified network, where A1/A2 are likely. | Before the viewer host starts (room or share), the network of the address viewers will use is classified through the Windows Network List Manager. Public or unclassified → the start is **refused**, and the UI offers *Share anyway for this session*. The override ends when the session stops. | Implemented |
| N2 | The ports answer anyone who can route to them. | MediaMTX and the viewer host still listen on all interfaces (Tailscale and LAN viewers both have to reach them). Every request needs the 80-bit path; a wrong path gets a bare 404. | Partial (§6 R5) |

### 4.2 Viewer-token and control-code transport (prompt item 2)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| S1 | A1 reads the viewer token or control code from a URL or header and uses it. | Neither is ever sent. A client first `GET /{path}/nonce` (16 random bytes, single use, 30 s lifetime, at most 64 outstanding and 8 per client address), then opens the WebSocket with `?n=<nonce>&p=<proof>` where `proof = HMAC-SHA256(channelKey, "axe-auth|<channel>|<nonce>")`. The server consumes the nonce and checks the proof in constant time. | Implemented |
| S2 | A1 replays a captured upgrade request. | The nonce is consumed on first use, so a replayed request fails. | Implemented |
| S3 | A1 brute-forces the viewer token offline from a captured proof. | 128-bit secret; not feasible. | Implemented |
| S4 | A4 captures a legitimate controller's handshake and brute-forces the ~40-bit control code offline. | A correct code is not enough: every control request needs the host to click **Allow** on the Friends page (30 s, then refused; refused when nobody is there to answer). A cracked code gets the attacker a prompt the host can deny, not the keyboard. | Implemented |
| S8 | Someone with a valid code takes control away from the current controller. | While a controller is connected, further control attempts get 409 and are not admitted. The slot frees when that controller disconnects or the host switches control off. | Implemented |

### 4.3 Origin validation and cross-site attacks (prompt item 3)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| X1 | A3 opens `ws://host/…` from another origin (CSWSH). | Browser WebSocket upgrades carry `Origin`; one whose host does not match the request host, the viewer host name, or loopback gets 403. Requests without `Origin` (native clients) still need the proofs, which a website cannot compute without the secrets. | Implemented |
| X2 | A3 uses DNS rebinding so `Origin` and `Host` both name the attacker's domain. | Every request's `Host` must be an IP literal assigned to this PC, loopback, or the configured viewer host name. Anything else gets 421 before routing. | Implemented |

### 4.4 Control-code brute force (prompt item 4)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| B1 | Online guessing of the control code. | Per-IP: 5 failed proofs within 1 minute → 2-minute lockout (429). Per session: 20 failed control attempts in total switch control off, which voids the code and tells the host. | Implemented |
| B2 | Guessing the room path. | 80 bits, and no response distinguishes a wrong path from no service. | Implemented |

### 4.5 Key derivation and domain separation (prompt item 5)

All keys are HKDF-SHA256 (RFC 5869) over the viewer token, with a distinct `info` per use:

| Use | IKM | salt | info |
|---|---|---|---|
| Chat/file encryption | ViewerToken | none | `AxePower-ShareScreen-Chat-v1` |
| Voice encryption | ViewerToken | none | `AxePower-ShareScreen-Voice-v1` |
| Chat/voice WebSocket admission proof | ViewerToken | none | `AxePower-ShareScreen-Auth-v1` |
| Control admission proof | ViewerToken | control code (UTF-8) | `AxePower-ShareScreen-Control-v1` |
| Control frame AES-256-GCM key (per connection) | control admission key | nonce of that connection | `AxePower-ShareScreen-ControlFrames-v2` |
| WHEP read proof (media server) | ViewerToken | none | `AxePower-ShareScreen-Auth-v1` (channel `whep`) |

The proof strings also name their channel (`chat`, `voice`, `control`, `whep`), so a proof for one channel is
never valid for another. Every room has a fresh viewer token, so every key is per session.

### 4.6 Replay resistance and ordering (prompt item 6)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| P1 | A2 or A4 replays a captured chat message or voice packet. | Each sender has a random 64-bit id per process. Chat envelope v2 and voice packet v3 carry `sender` and a per-sender sequence number, and both are **authenticated as AES-GCM associated data**. Replay is checked only after decryption succeeds, with a per-sender 1024-entry sliding window (at most 64 senders tracked). The host and every AxePower client run the check. Older envelope versions without an authenticated sequence are rejected. | Implemented |
| P2 | Forged sequence numbers poison the window and cause legitimate messages to be dropped. | The window is updated only for packets that decrypt, so forging one needs the key. | Implemented |
| P3 | Control frames injected, replayed or reordered (A2 in the TCP stream). | Each control frame is `<seq>|<base64(AES-256-GCM(event json))>` under the per-connection frame key, with the sequence as both nonce and associated data. `seq` must increase strictly. A frame that fails to open, or arrives out of order, closes the connection and releases held input. | Implemented |
| P4 | Chat, voice and control *content* are confidential to A1. | Yes for all three: their keys never cross the wire. In the browser, the audited `@noble/ciphers` and `@noble/hashes` (vendored, see THIRD_PARTY_NOTICES.md) do the work, because browsers withhold `crypto.subtle` on plain-HTTP pages. | Implemented |

### 4.7 Frame size, rate, connection and idle limits (prompt item 7)

| Limit | Value |
|---|---|
| Request head | 8 KB, and must arrive within 10 s |
| Concurrent sockets on the viewer host (including half-open) | 64; more get 503 |
| Controllers | 1 |
| Chat sockets / voice sockets | 10 / 10 |
| Control frame / chat frame / voice frame | 8 KB / 64 KB / 4 KB; larger closes the socket |
| Control events | 180/s applied, extras dropped; more than 500 in one second disconnects |
| Chat frames / voice frames | more than 100 / 200 in one second disconnects |
| WebSocket idle | the server pings every 20 s; no inbound frame for 60 s closes the socket |
| Outstanding nonces | 64 in total and 8 per client address, 30 s each, single use |
| Control approval | the host has 30 s to click Allow; then refused |

### 4.8 Revocation and held input (prompt item 8)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| R-1 | A key or button is left held when the controller vanishes. | Every exit path of the control socket (close, error, bad MAC, out-of-order frame, rate abuse, idle timeout) calls `ReleaseHeld()`. The page also sends a release on blur. | Implemented |
| R-2 | Control continues while the host is away. | A Windows session lock calls `DisableControl()`: the code is voided, input is revoked, and the controller is disconnected. AxePower has no separate app-lock feature, so the workstation lock is the lock this covers. | Implemented |
| R-3 | A former participant keeps access. | Stopping the room closes every socket and discards the room. The next room has new secrets, so old links, proofs and keys do not work. Switching control off disconnects the controller and voids the code. | Implemented |

### 4.9 Host-side visibility (prompt item 9)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| V1 | Someone is watching, listening or controlling without the host knowing. | The viewer host tracks every admitted chat, voice and control connection. The service also polls MediaMTX's loopback API for WebRTC sessions on the room's path, so **video-only viewers** are listed too. The Friends page shows the list with kind, address and time, and marks the controller. | Implemented in code; UI unverified (§6 R7) |

## 5. Migration path to TLS / an authenticated encrypted tunnel (prompt item 10)

Secrets never cross the wire and chat, voice and control are encrypted end to end, but on a plain
LAN the page and signaling are still HTTP. Status of each step:

1. **Tailscale, with HTTPS (implemented).** Viewers on a tailnet reach the host over WireGuard,
   which encrypts and authenticates every byte. When the viewer address is this PC's Tailscale
   address or `.ts.net` name, AxePower also runs `tailscale cert` and serves the room as
   `https`/`wss` with that Let's Encrypt certificate, and MediaMTX serves WHEP over HTTPS with the
   same files, so browsers trust the page with no warning (closes R6 on a tailnet). This needs
   MagicDNS and HTTPS certificates switched on in the tailnet's admin console; without them the
   room falls back to plain HTTP inside the tunnel, and the log says why. Plain HTTP is still
   accepted from this PC only, for MediaMTX's auth check.
2. **Per-session TLS for the viewer host.** Create an ephemeral ECDSA certificate per room
   (`CertificateRequest.CreateSelfSigned`), serve `https`/`wss` from `ShareViewerHost` through
   `SslStream`, and put its SHA-256 fingerprint in the link fragment (`#v=…&c=<fp>`). AxePower
   clients pin the fingerprint in `RemoteCertificateValidationCallback`. Browsers will show a
   certificate warning for a self-signed certificate, so browser viewers stay on the current
   protocol until (3).
3. **Browser-trusted TLS.** Either a locally trusted CA installed by the owner (a privileged,
   per-machine step that has to be opt-in), or relaying through an AxePower-operated endpoint
   with a public certificate (see the remote play plan). Then `webrtcEncryption: yes` in
   MediaMTX with the same certificate.
4. **WHEP read authorization (implemented).** MediaMTX runs with `authMethod: http` pointed at a
   random per-session path on the viewer host (loopback only). Publishing needs the encoder's
   credentials from this PC; reading needs a single-use nonce proof (channel `whep`) in the WHEP
   request's query. Verified against the real MediaMTX 1.21.1 and FFmpeg: the query arrives in
   the auth call verbatim, the encoder still publishes, and a reader without a fresh proof gets 401.

The handshake in §4.2 stays useful under TLS. It keeps secrets out of any proxy or log on the
way, so none of these steps replace it.

## 6. Residual risk

Known and disclosed. Closed items keep their number so older references still resolve.

- **R1. (Closed for passive observers.)** Control frames are now encrypted (P3/P4). An *active*
  attacker who can alter the plain-HTTP page could still capture input; see R6.
- **R2. (Closed.)** Reading the video needs a fresh proof of the viewer token (§5 step 4); the
  path alone gets 401.
- **R3. (Closed.)** A guessed or cracked control code only produces an Allow prompt (S4).
- **R4. An active attacker can disrupt sessions.** A2 can drop or reset connections, a nuisance
  but not a takeover. Without the secrets they cannot forge chat, voice or control content, or
  admission proofs.
- **R5. Listening on every interface.** Needed for LAN plus Tailscale viewers. The path secret
  and the proofs gate access, but the ports are reachable from any network the PC is on, which
  is why N1 refuses untrusted networks by default.
- **R6. Viewer page script integrity on a plain LAN.** Outside Tailscale the page is still served
  over plain HTTP, so A2 can modify it in transit, for example to capture the code or the input
  a controller types. Over Tailscale with HTTPS this is closed. Fix for plain LANs: §5 steps 2–3.
- **R7. Unverified UI.** The participant list, the untrusted-network refusal and the override
  have not been rendered by a person yet (tracked as open in `skillsMD/MUSIC_FIX_PLAN.md`).
- **R9. (Reduced.)** Nonces are capped at 8 per client address as well as 64 in total, so one
  address can no longer lock others out. Someone with many addresses on the network and the room
  path could still exhaust them briefly; a nuisance, not a way in.
- **R10. IPv4 only.** The viewer host listens on IPv4. A Tailscale name also resolves to an IPv6
  address, so a client that tries IPv6 first falls back after a short delay. Serving dual-stack
  needs every loopback and per-address check to normalise IPv4-mapped addresses first.
- **R11. Real `tailscale cert` not yet run.** The provider is tested against a fake `tailscale`
  and the servers over real TLS with a self-signed certificate, but no Tailscale machine has
  issued a certificate for AxePower yet.
- **R8. MediaMTX session polling is unverified against a live MediaMTX.** The response shape
  of `/v3/webrtcsessions/list` is taken from MediaMTX's API documentation and tested against a
  recorded sample. If it differs, video-only viewers will not appear in the list.

## 7. Wire formats

**Admission.** `GET /{path}/nonce` returns `{"n":"<base64url 16 bytes>"}`. Then the client opens
`/{path}/{chat|voice|ws}?n=<n>&p=<hex HMAC-SHA256(key, "axe-auth|<channel>|<n>")>`.
The control channel uses the control admission key; chat and voice use the auth key. Watching
uses the same scheme on the media server: `POST <whep>?n=<n>&p=<proof over channel "whep">`.

**Chat envelope v2** (WebSocket text): JSON `{"Version":2,"Sender":"<16 hex>","Sequence":<n>,
"Nonce":"<b64>","Ciphertext":"<b64>","Tag":"<b64>"}`. AES-256-GCM, with associated data
`axe-chat|2|<Sender>|<Sequence>`.

**Voice packet v3** (WebSocket binary): `0x03 | sender (8) | sequence (8, big-endian) | nonce (12) |
tag (16) | ciphertext`. AES-256-GCM, with the first 17 bytes as associated data.

**Control frame** (WebSocket text): `<seq>|<base64(ciphertext || 16-byte tag)>`. AES-256-GCM under
the per-connection frame key; the 12-byte nonce is four zero bytes then `seq` as a 64-bit
big-endian number, and the associated data is `axe-ctl|<seq>`. `seq` starts at 1 and increases
strictly, so a nonce never repeats under one key. The events are unchanged: `m` move, `d`/`u`
button, `w` wheel, `k` key, `r` release all.

**Rebuilding the browser crypto.** `Vendor/noble.min.js` is `@noble/ciphers` 2.4.0 (`gcm`) and
`@noble/hashes` 2.4.0 (`sha256`, `hmac`, `hkdf`) bundled with `esbuild 0.25.10 --bundle --minify
--format=iife --target=es2019` from an entry that sets `globalThis.AxeNoble`. Keep the versions
pinned and update THIRD_PARTY_NOTICES.md with them.

## 8. Verification

All in `AxePower.Tests/ShareScreen/ShareScreenSecurityTests.cs`, run against a real viewer host over loopback sockets. `ShareViewerHostTests.cs` additionally checks that the old `?v=` / `?k=` forms are refused.

| Threat | Test |
|---|---|
| N1 | `UntrustedNetwork_RefusesRoom_UnlessAllowedForSession` |
| S1 | `UpgradeRequests_CarryNeitherViewerTokenNorControlCode` |
| S2 | `ReplayedUpgrade_WithConsumedNonce_IsRejected`, `Nonces_AreSingleUse_ExpireAndAreCapped` |
| X1 | `WrongOrigin_IsRejectedWith403_OnControlChatAndVoice` |
| X2 | `ReboundHostHeader_IsRejected` |
| B1 | `BruteForceGuessing_TriggersLockoutAndReturns429`, `TooManyFailedControlAttempts_SwitchControlOff` |
| §4.5 | `KeyDerivation_IsDomainSeparated` |
| P1/P2 | `ChatReplay_IsRejected_AfterAuthentication`, `VoiceReplay_IsRejected_AfterAuthentication`, `ForgedSequence_DoesNotPoisonTheWindow`, `LegacyEnvelopes_AreRejected`, `ReplayFilter_AcceptsReorderingInsideTheWindow_AndRejectsTheRest` |
| P3/P4 | `ControlFrame_WithBadMac_DisconnectsAndReleases`, `ControlFrame_OutOfOrder_Disconnects`, `ControlFrames_FromAnotherConnection_DoNotVerify`, `ControlFrames_AreNotReadableOnTheWire` |
| S4 (approval) | `ControlRequest_IsRefusedWhenTheHostDenies`, `ControlRequest_IsRefusedWithNobodyToApprove`, `ControlRequest_LapsesWithoutAnAnswer`, `ControlRequest_WhileAwaitingApproval_HoldsTheSlot`, `Service_RefusesControlWithoutAnswer_AndPassesTheHostsChoice` |
| R9 | `Nonces_AreCappedPerClientAddress` |
| R2 | `MediaAuthTests.cs`: `Reads_NeedAFreshWhepProof`, `Publishing_NeedsTheEncodersCredentialsFromThisPc`, `AuthEndpoint_AnswersOnlyAtItsSecretPath`, `GeneratedConfig_HasNoAnonymousReaders`, `Live_MediaMtx_PublishesForTheEncoder_AndAdmitsOnlyProvenReaders` (real MediaMTX and FFmpeg when installed) |
| §5 step 1 | `ShareTlsTests.cs`: provider with a fake `tailscale`, viewer host over real TLS, chat over WSS, MediaMTX config, `Live_MediaMtx_ServesWhepOverHttps_AndStillChecksProofs` |
| Tampering | `ChatTamperedCiphertextTagOrHeader_FailsDecryption`, `VoiceTamperedCiphertextTagOrHeader_FailsDecryption` |
| §4.7 | `OversizedControlFrame_TerminatesConnection`, `OversizedVoiceFrame_TerminatesConnection`, `SlowRequestHead_IsDropped`, `IdleSocket_IsClosed`, `ServerPings_KeepAnAnsweringClientAlive` |
| S8 | `SecondController_IsRejectedWhileControlIsHeld` |
| Stale sessions | `StaleSessionSecrets_FromPreviousRoom_AreRejected` |
| R-1/R-2 | `ControllerDisconnect_ReleasesHeldKeysAndUpdatesControlState`, `DisableControl_ImmediatelyRevokesInputGate` |
| Exit criterion | `RemoteInput_NeedsCurrentSessionProofAndHostEnabledControl` |
| V1 | `ParticipantTracking_ListsActiveParticipants`, `MediaMtxSessions_AreParsedIntoVideoViewers` |
| Page ↔ host agreement | `ViewerPageHmac_MatchesHostDerivation` (runs the page's own SHA-256/HMAC/HKDF script under Node when Node is installed) |
