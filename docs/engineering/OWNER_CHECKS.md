# Owner checks: everything that needs you

**For:** the owner. These items cannot be finished by code alone: they need a real phone, a second
PC, the elevated app on screen, an installer run, or something only you can buy or decide.
**Last updated:** 2026-10-08 (after Prompt 31).

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

### A4. Settings › Diagnostics & Privacy (Prompt 23)
**Need:** this PC, the app built from the current code.
**Do:**
1. Start AxePower once. `%APPDATA%\AxePower\AxePower.log` and `AxePower.log.old` should be gone
   (they are deleted on the first start), and `%LOCALAPPDATA%\AxePower\Diagnostics` should hold
   one `axepower-YYYYMMDD.jsonl` file.
2. Settings → **Diagnostics & Privacy**. The card should say how many files are stored, since
   when, and "Kept on this PC for 14 days or 20 MB … Never uploaded."
3. Click **Open…**. Click through the files on the left: each one shows its full text on the
   right. Tick **Include my settings**: a `settings.json` appears, and your folders, app lists and
   friends show as `[removed: N item(s)]`. Search the log text for your Windows user name or this
   PC's name: neither should appear.
4. Click **Save bundle…**, save it to the Desktop, open the .zip: it holds exactly the files you
   saw. Cancel the Save dialog once too: nothing should be written.
5. Back in the card, click **Delete all…**, answer **No** (nothing changes), then again and
   answer **Yes**: the card says "Nothing stored" until the next event is logged.
6. Look at the card and the window in Dark and Light.

**Tell me:** pass, or a screenshot of what is off.

### A5. Change preview and Settings › Change history (Prompt 24)
**Need:** this PC. AxePower running as administrator (as usual).
**Do:**
1. System Optimizer: turn on **Disable Sticky Keys Shortcut**. A preview window should list one
   registry change with its current value → new value and a green **Fully reversible** badge.
   Click **Cancel**: nothing changes. Turn it on again and click **Apply**.
2. Turn it off again: the preview should say **Review the rollback** and show the value going back
   to what you had (not a Windows default).
3. Game Mode: tick **Print Spooler** (or any service you don't need for an hour), choose to pause
   it. The preview should show "Automatic, running → Manual, stopped" and an amber **Partially
   reversible** badge with the reason. Apply.
4. Settings → **Change history**: all three changes are listed, newest first. Click **Details** on
   each. On the Print Spooler one, click **Roll back…**, approve: the service should be Automatic
   and running again (check in services.msc).
5. The rolled-back entry should no longer offer Roll back, and say why.
6. Look at the preview and the history in Dark and Light.

**Tell me:** pass, or a screenshot of what is off.

### A6. Trust & Permissions page (Prompt 25)
**Need:** this PC; your phone for step 2.
**Do:**
1. Sidebar → **Trust & Permissions** (above Settings). Check "Administrator rights" and "Start with
   Windows" match what you set.
2. Start LAN Drop and open its page on your phone. Within a few seconds the Phone Hub entry should
   say "Open · 1 device(s) connected" with your phone listed. Click **Disconnect all and rotate
   access link**: a confirmation lists your phone; confirm; the phone's page stops working.
3. In "Data on this PC", click **Open folder** on two entries: File Explorer opens (not as
   administrator). Click **Delete…** on Music history, read the confirmation, choose **Cancel**.
4. Signed in: check the account section shows your @username, and that **Delete my account…**
   shows the high-risk confirmation with the tick box and a disabled button until ticked (do not
   confirm unless you mean it).
5. "Updates and this build" shows version 2.1.0, a build commit, and "Not code-signed".
6. Use only the keyboard (Tab, Shift+Tab, Enter) and then the controller's D-pad to reach every
   button on the page. Look at it in Dark and Light.

**Tell me:** pass, or a screenshot of what is off.

### A7. The new confirmations (Prompt 27)
**Need:** this PC. Choose **Cancel** in every dialog unless you really want the action.
**Do:**
1. Uninstaller Pro: select an app you don't need, Uninstall. The dialog lists it, says "High risk"
   and "Not reversible", and **Uninstall …** stays disabled until you tick the box. Cancel.
2. Debloat: click Win11Debloat's launch button: high risk, restart needed, the exact command
   shown. Cancel.
3. System Doctor › DNS: choose a provider. The dialog shows your adapter's DNS now and after, and
   (if you typed DNS servers in yourself) warns that "Automatic" will not bring them back. Cancel.
4. Software Hub: select two apps and install. Both are listed. Cancel. Then Update all apps and
   the Windows Update cache reset: check what each lists. Cancel.
5. In each dialog, press Enter straight away: it must cancel, not run.
6. Look at a dialog in Dark and Light.

**Tell me:** pass, or a screenshot of what is off.

### A8. Accessibility: Narrator, keyboard, scaling, High Contrast, motion, controller (Prompt 28)
**Need:** this PC; a controller for M-9. About 30 minutes.
**Do:** the ten rows of the manual test matrix in `docs/engineering/ACCESSIBILITY.md` (M-1 to
M-10). Each row says what to set up, what to do and what passing looks like.
**Tell me:** "A8" with pass or what you saw for each row number, e.g. "M-5: Settings text clipped
at 200%".

### A9. Idle cost of the real app (Prompt 29)
**Need:** this PC, this build installed or run from `bin\Release`. About 10 minutes.
**Do:**
1. Start AxePower. Open the Dashboard and leave it for a minute. In a normal (not admin)
   PowerShell in the repo folder run:
   `powershell -ExecutionPolicy Bypass -File scripts\measure-axepower.ps1 -Label Dashboard`
2. Open Settings, wait a minute, run it again with `-Label Settings`.
3. Close the window to the tray, wait a minute, run it with `-Label Tray`.
4. Open Game Mode, Settings, Game Mode again: the second Game Mode visit should feel instant.
5. Exit from the tray menu. The tray icon should vanish at once, and Task Manager should show no
   AxePower or leftover `powershell.exe`.

**Tell me:** the three lines the script printed, and anything odd in steps 4 and 5.

### A10. Error messages (Prompt 30)
**Need:** this PC. About 10 minutes.
**Do:**
1. Turn Wi-Fi off. Settings › Updates › Check for updates: a banner says it could not check, that
   you appear to be offline, with **Details** and **Close**. Click Details: a short technical line
   (no long stack of "at …" lines), and **Copy details** works.
2. Settings › Notifications: turn notifications off, then repeat step 1: the error still shows.
3. Software Hub: select two apps, start installing, then Cancel: the banner says what was done
   and what was cancelled, and does not call it an error. Turn Wi-Fi back on.
4. Look at the banner in Dark and Light.

**Tell me:** pass, or a screenshot of what is off.

### A11. First run (Prompt 31)
**Need:** this PC, this build. About 10 minutes. Your settings are put back at the end.
**Do:**
1. Exit AxePower. Rename `%LOCALAPPDATA%\AxePower\settings.json` to `settings.json.mine`.
2. Start this build. Before anything else, the **Welcome to AxePower** window appears: no Windows
   permission prompt, no power plan change (check Control Panel › Power Options stays as it was).
3. Click **Not now**. AxePower opens without asking for administrator rights.
4. Exit, start again: the welcome is back. Choose Continue with the recommended choices. Windows now
   asks for permission (Run as administrator), and Power Options shows AxePower's plans.
5. Look at the welcome window in Dark and Light (Settings › Theme, then repeat from step 1).
6. Exit. Delete the new `settings.json` and rename `settings.json.mine` back. Start AxePower: no
   welcome, everything as before.

**Tell me:** pass, or what happened at the step that differed.

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
| A4 Diagnostics & Privacy | this PC | waiting |
| A5 Change preview and history | this PC | waiting |
| A6 Trust & Permissions | this PC, phone | waiting |
| A7 New confirmations | this PC | waiting |
| A8 Accessibility matrix | this PC, controller | waiting |
| A9 Idle cost (real app) | this PC | waiting |
| A10 Error messages | this PC | waiting |
| A11 First run | this PC | waiting |
| B1 LAN Drop on a phone | phone | waiting |
| B2 Firewall rule | installer build | waiting |
| C1 Share Screen, two PCs | second PC | waiting |
| C2 Tailscale HTTPS | second PC, Tailscale | waiting |
| D1 Code signing | decision | decided: unsigned for now; buy a certificate once AxePower earns (2026-10-08) |
| D2 Update key | run one script | done 2026-10-06 (key pinned and committed) |
| D3 Commit branch | your answer | answered: `2.1.2-industry-standards-followup` |
| D4 LAN HTTPS | decision (later) | decided 2026-10-08: later, not before this release |
