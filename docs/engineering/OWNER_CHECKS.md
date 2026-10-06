# Owner checks: everything that needs you

**For:** the owner. These items cannot be finished by code alone: they need a real phone, a second
PC, the elevated app on screen, an installer run, or something only you can buy or decide.
**Last updated:** 2026-10-06 (after Prompt 21).

Work through them in any order. For each one, tell me the item number and either "pass" or what
you saw (a screenshot helps). I then close it in `SOFTWARE_COMPLETION_PROMPTS.md` and
`skillsMD/MUSIC_FIX_PLAN.md`, or fix what you found.

**Before any of them:** build and run the current code (`dotnet build AxePower.sln -c Release`,
then start `AxePower.App\bin\Release\net8.0-windows10.0.19041.0\AxePower.exe`). The installed
2.1.0 does not have these changes.

---

## A. Checks you can do alone on this PC (about 30–45 minutes)

### A1. Visual check of the split main window and Software Hub (Prompt 20)
**Need:** this PC.
**Do:** follow `docs/engineering/VISUAL_CHECKS.md`: the full list in the Dark skin, then the quick
list in Light, Glass and one accent skin.
**Tell me:** "A1 pass", or the item numbers that look wrong.

### A2. "Chat ended" message on the Friends page (Prompt 21)
**Need:** this PC.
**Do:**
1. Friends → Invite, to open a room. Copy the room link.
2. Stop sharing, then Invite again, so a **new** room with a new link is open.
3. Friends → Join, paste the **old** link from step 1, and join.
4. Within a few seconds a message should say the room refused this link, and to ask for a new one.
5. Stop sharing completely, then join the old link again. This time nothing is listening, so the
   message should say the connection to the room was lost.
6. Open a room, join it with its **current** link, then leave: **no** message should appear.

**Tell me:** the messages you saw in steps 4 and 5, and whether step 6 was silent.

### A3. LAN Drop and Share Screen warning banners on a Public network (P6, P10)
**Need:** this PC, connected to a network that Windows calls *Public*. Check in Settings →
Network & internet → your Wi-Fi → Properties.
**Do:**
1. Phone Control → LAN Drop → Start. A red banner should name the network and refuse to start.
   Click **Start anyway for this session**: the server should start.
2. Friends → Invite. A banner should appear; click **Share anyway for this session**: the room
   should open.
3. Compare both pages with the images in `ui reference/` in Dark and Light.

**Tell me:** pass, or a screenshot of what is off.

---

## B. Checks that need a phone (about 20 minutes)

### B1. LAN Drop from a real phone (Prompt 13; P1, P6)
**Need:** this PC and a phone on the same **Private** Wi-Fi. An iPhone (Safari) and an Android
phone (Chrome) are best if you have both.
**Do:**
1. Phone Control → LAN Drop → Start, and scan the QR code with the phone. Also try the phone's
   normal camera app, not only a scanner app. The address box under the QR should show the full
   link ending in `?t=...`.
2. The page should load, and the address bar should **not** contain `t=`. Then type only
   `http://<ip>:5050/` into the phone's browser: it should show "Access link needed".
3. From the phone: upload a photo, send a text to the PC's clipboard, download a file the PC
   shared, and press play, pause and next on the media buttons.
4. Pull down to refresh the page on the phone: it should still work without scanning again.
5. On the PC: the phone should be listed under **Connected Devices**. Click **Disconnect All &
   Rotate Access**: the list empties, the QR changes, and the phone's page stops working until it
   scans the new QR.
6. If AxePower Music is playing, the phone's music controls should drive it and stay in step.

**Tell me:** which phone and browser, and any step that failed.

### B2. Firewall rule from the installer (Prompt 13; P7)
**Need:** this PC, plus the installer built from the current code (your normal Inno Setup build
of `Installer/AxePower.iss`).
**Do:** in an administrator PowerShell, run this after each step:
`netsh advfirewall firewall show rule name="AxePower LAN Drop"`
1. Install: exactly **one** rule, with `Profiles: Domain,Private`, `LocalPort: 5050`.
2. Run the same installer again (upgrade): still exactly one.
3. Uninstall: "No rules match".

**Tell me:** the three outputs, or paste them. Also tell me whether to delete any older "AxePower"
firewall rules that Windows created from earlier prompts. List them with
`netsh advfirewall firewall show rule name=all | findstr /i axepower`.

---

## C. Checks that need a second PC (about 45 minutes)

### C1. Share Screen between two PCs on your LAN (Prompt 14; P9, P10)
**Need:** two Windows PCs on the same Private network, both running this build (copy the
`bin\Release` folder or install the built installer on the second PC). Chrome and Edge on the
second PC.
**Do:**
1. PC 1: Friends → Invite, and copy the link.
2. PC 2: open the link in **Chrome**: the stream plays. Repeat in **Edge**.
3. PC 2 (browser): ask for control, and type the control code in **lowercase**.
4. PC 1: an Allow/Deny prompt appears. Click **Allow**. On PC 2, move the mouse and type: PC 1
   responds.
5. Repeat step 3, but click **Deny** on PC 1: the browser is refused.
6. PC 2: in AxePower, Friends → Join with the same link. Send chat both ways, send a file both
   ways, and talk on voice both ways.
7. PC 1: "In your room" lists the browser as "Video viewer" and highlights the controller.

**Tell me:** pass, or the step that failed. If a step fails, send me
`%APPDATA%\AxePower\AxePower.log` from both PCs (secrets are now removed from it automatically).

### C2. Share Screen over Tailscale with HTTPS (Prompt 14; P11)
**Need:** the two PCs in one Tailscale network. In the Tailscale admin console, under DNS, turn on
**MagicDNS** and **HTTPS Certificates**.
**Do:**
1. PC 1: Friends → Settings → **Viewer address**, and enter PC 1's name ending in `.ts.net`
   (shown by `tailscale status`).
2. Friends → Invite: the link should start with `https://`.
3. PC 2: open it. There should be no certificate warning, and the stream, chat, voice and control
   all work.
4. Turn HTTPS Certificates off in the admin console, then open a new room: the link falls back to
   `http://`, and the log has a line saying why.

**Tell me:** pass, or the step that failed, plus both log files.

---

## D. Things only you can get or decide

### D1. Code-signing certificate (Prompt 8): **decided 2026-10-06: stay unsigned for now**
Installers keep the SmartScreen warning, and the release workflow's signed gate is not used; you
build and upload releases by hand. When you get a certificate, the options below apply, and
`UpdateSigningKey.ReleasesAreAuthenticodeSigned` becomes `true`.

Without it, Windows SmartScreen warns on the installer and the release gate refuses to publish.
**Choose one:**
- **Azure Trusted Signing** (Microsoft's signing service; monthly fee; needs identity
  verification, and is available to individuals in some countries only), or
- **A code-signing certificate from a certificate authority** (for example Sectigo, Certum,
  DigiCert; yearly; identity checks; usually delivered on a hardware token or cloud HSM), or
- **Stay unsigned for now** (installs keep showing the SmartScreen warning).

**Then:** in GitHub → repository Settings → Environments → create `release` → add the secrets
listed in `docs/security/CODE_SIGNING.md` §1 (or the Trusted Signing settings). Tell me which
route you chose, and I wire up the release workflow for it.

### D2. Update-signing key (Prompt 9): **done 2026-10-06**
The public key is pinned in `UpdateSigningKey.cs` and committed; a test signature made with the
private key verified against it. Builds before that commit cannot check updates. The tooling is in place
(`scripts/new-update-signing-key.ps1`, `scripts/sign-update-manifest.ps1`). While releases are
unsigned, an update is accepted only when its manifest is signed with your key and the download
matches the signed size and SHA-256.

**Once (about 2 minutes):**
1. In the repository folder run:
   `powershell -ExecutionPolicy Bypass -File scripts\new-update-signing-key.ps1`
2. It prints where it saved the **private** key
   (`%USERPROFILE%\AxePower-update-signing\update-signing.key.pem`). Copy that file's text into
   your password manager. Never commit it or send it to anyone.
3. It also wrote the **public** key into `AxePower.Core\Services\Update\UpdateSigningKey.cs`.
   Tell me "D2 key done", and I commit that file.
4. The next release you ship must contain that file. Copies without it cannot check updates.

**Each release:** after building the installer, run
`powershell -ExecutionPolicy Bypass -File scripts\sign-update-manifest.ps1 -Installer Installer\Output\AxePower-vX.Y.Z-Setup.exe -Version X.Y.Z`,
then upload the `update-manifest.json` it writes to the GitHub release next to the installer.

### D3. Commit branch for Prompts 1–21
Nothing since `b68f001` is committed. Tell me the branch name (for example a new
`v2.1.1---completion` or the current `v2.1.1---monitization-features`), and I commit in logical
steps, one per prompt where possible, without pushing.

### D4. HTTPS on a plain LAN (P4) (a decision; it can wait)
On a normal Wi-Fi (not Tailscale), LAN Drop and the Share Screen viewer page are still plain HTTP.
That's fine at home, but someone on the same network could read or alter that traffic. The options
need their own design pass (threat model §5). Say if you want this before release, and I'll bring
choices.

---

## Progress

| Item | Needs | Status |
|---|---|---|
| A1 Visual check | this PC | waiting |
| A2 Chat ended | this PC | waiting |
| A3 Public-network banners | this PC on a Public network | waiting |
| B1 LAN Drop on a phone | phone | waiting |
| B2 Firewall rule | installer build | waiting |
| C1 Share Screen, two PCs | second PC | waiting |
| C2 Tailscale HTTPS | second PC, Tailscale | waiting |
| D1 Code signing | decision | decided: unsigned for now |
| D2 Update key | run one script | done 2026-10-06 (key pinned and committed) |
| D3 Commit branch | your answer | answered: `2.1.2-industry-standards-followup` |
| D4 LAN HTTPS | decision (later) | waiting |
