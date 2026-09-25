# AxePower Friends

Friends is AxePower's built-in private room for saved contacts, full-duplex voice, text chat,
encrypted file transfer, and low-latency screen sharing. It is intentionally account-free: a
"friend" is a name and a saved AxePower room link, not a cloud identity or presence record.

## Product surface

- The existing AxePower navigation sidebar remains the application shell.
- A collapsible roster provides search plus Online, All, and Live filters.
- Call joins voice/chat/files without opening the screen player.
- Watch Screen joins the same room and opens the friend's shared screen.
- Invite opens a host room, starts local voice, and copies its authenticated link.
- Share Screen starts or stops capture without ending the call.
- Leave closes voice, chat, files, screen sharing, and the room.
- The right panel switches between Chat and Files and can be collapsed.
- There is no camera button because webcam transport is not implemented.

## Architecture

The host uses one `ShareViewerHost` room for three authenticated channels:

1. `/chat` relays encrypted text and chunked file messages.
2. `/voice` relays encrypted Opus packets for two-way audio.
3. The room viewer page embeds the existing MediaMTX WebRTC screen stream when capture is live.

Voice is captured and played with NAudio and encoded with Concentus Opus. Chat, file chunks, and
voice packets are protected with AES-GCM using the per-room viewer secret. Local chat history and
received attachments are encrypted at rest with Windows DPAPI. Screen media still uses the
existing MediaMTX/WebRTC pipeline; HTTP signalling means remote use should be through a trusted
LAN or VPN such as Tailscale.

Remote control remains separate, disabled by default, and protected by a second session PIN that
is never included in the invite link.

## Main files

- `AxePower.App/Views/ShareScreen/ShareScreenView.xaml`
- `AxePower.App/ViewModels/ShareScreen/ShareScreenViewModel.cs`
- `AxePower.Core/Services/ShareScreen/ShareScreenService.cs`
- `AxePower.Core/Services/ShareScreen/ShareViewerHost.cs`
- `AxePower.Core/Services/ShareScreen/RoomVoiceService.cs`
- `AxePower.Core/Services/ShareScreen/RoomMessageCrypto.cs`
- `AxePower.Core/Services/ShareScreen/RoomVoiceCrypto.cs`
- `AxePower.Core/Services/ShareScreen/ShareAttachmentStore.cs`
- `AxePower.Core/Services/ShareScreen/ShareChatStore.cs`

## Persistence

- Saved contacts: `%LOCALAPPDATA%\AxePower\settings.json`
- Encrypted chat history: `%LOCALAPPDATA%\AxePower\Chat\share-chat.json` (the legacy filename is
  retained for migration compatibility; its current contents are protected binary data)
- Encrypted received files: `%LOCALAPPDATA%\AxePower\Chat\Attachments`

## Deliberate limits

- No accounts, cloud friend graph, true presence, offline requests, or push notifications.
- Direct room links only; no hosted relay or TURN service.
- File transfers are capped at 8 MB.
- No webcam video.
- Live two-PC validation is tracked in `SHARE_SOCIAL_PLAN.md` and must not be inferred from unit
  tests.

## Verification

Focused automated coverage lives in `AxePower.Tests/Friends` and
`AxePower.Tests/ShareScreen`. It covers room authentication, encrypted message/file/voice packets,
attachment tamper rejection, storage encryption, filters, join/leave behavior, and viewer-host
endpoints. Hardware capture, real microphones, speakers, and two-PC networking require owner
verification because the application relaunches elevated.
