# AxePower usability test plan

**For:** an independent usability researcher running sessions with Windows users. Nothing here
assumes knowledge of AxePower's code or its developers.
**Build under test:** the release candidate in `docs/release/RELEASE_CANDIDATE_REPORT.md`
(AxePower 2.1.2). Use the installer from that report and check its SHA-256 against `SHA256SUMS`.
**Status:** no session has been run. Results go in [REVIEW_FINDINGS.md](REVIEW_FINDINGS.md), never
in this file.

## What AxePower is

A Windows 10/11 desktop utility for laptops and gaming PCs. It does the following:

- switches power modes (Eco, Smart, Beast, Game Mode);
- shows hardware status;
- installs and removes software (Software Hub, Uninstaller Pro);
- cleans and repairs Windows (System Care, System Doctor, Debloat);
- shares files with a phone on the same Wi-Fi (Phone Hub / LAN Drop);
- plays music, and shares the screen with friends.

Many actions change Windows settings. Every such action is previewed, and most can be undone from
Settings › Change history.

## Goals

The sessions should find out whether a representative user can:

1. Trust the installer enough to run it, and understand the warnings they see.
2. Understand the first-run welcome and make choices they agree with.
3. Do the common jobs without help: change power mode, start Game Mode, install an app, clean up.
4. Understand what a change will do before confirming it, and undo it afterwards.
5. Share a file from a phone over Wi-Fi.
6. Recover from an error (offline, permission declined) using only what the app says.
7. Update AxePower, and uninstall it.

## Participants

- **Who:** 6–8 participants, Windows 10 or 11 laptop users.
- **Mix:**
  - at least 2 who have never changed a power plan;
  - at least 2 gamers;
  - 1 who uses a screen reader or keyboard only, if available;
  - at least 1 on a standard (non-administrator) Windows account.
- **Excluded:** people who have worked on AxePower.

## Setup

- **Machines:** a Windows 11 laptop or VM per session, with a battery if possible, a restore point
  or VM snapshot taken first, and no earlier AxePower.
- **Phone:** an Android phone and an iPhone on the same Wi-Fi network, set to Private.
- **Accounts:** a second Windows account without administrator rights, for task T10.
- **Recording:** record the screen and voice with consent. The facilitator does not help unless
  the participant is stuck for 3 minutes; that task is then marked "assisted".

## Tasks

Read each task aloud as written. Do not name buttons or pages.

| # | Task | Success when | Watch for |
|---|---|---|---|
| T1 | "Here is the AxePower installer from its download page. Install it." | Installed and launched | Reaction to Windows SmartScreen and "Unknown publisher" (the build is unsigned); do they check the checksum? |
| T2 | "AxePower has just opened. Set it up the way you'd want it." | Welcome completed with choices they can explain | Do they read the explanations? Do they understand "Run as administrator" and that power plans are added or removed? |
| T3 | "Make your laptop save battery." | Eco mode on | Time to find it; confusion between modes |
| T4 | "You're about to play a game. Get the PC ready for it." | Game Mode started, and they can say what it changed | Do they understand the preview and the services list? |
| T5 | "Install VLC media player using AxePower." | VLC installed | Do they find Software Hub? Do they understand progress, and the result message? |
| T6 | "Free up some disk space using AxePower." | A cleanup run after reviewing the preview | Do they read the confirmation? Would they know how to undo it? |
| T7 | "Undo the last change AxePower made." | Rolled back from Change history | Can they find it? Do they understand when undo is not possible? |
| T8 | "Send a photo from this phone to the PC." | The file arrives | Starting the server, scanning the QR code, Wi-Fi network type warnings |
| T9 | (Wi-Fi switched off by the facilitator) "Check whether there's a new version of AxePower." | They understand they are offline and what to do | The error banner's wording, Details, recovery |
| T10 | (On the standard account) "Start AxePower and switch to Beast mode." | Done, or a clear explanation of why it needs permission | Reaction to the administrator prompt; declining it |
| T11 | "Update AxePower to the newest version." (Facilitator serves an update, or skip) | Updated, or the right message | Trust in the update prompt |
| T12 | "Remove AxePower from this PC." | Uninstalled | What they expect to remain (settings, power plans, firewall rule) |

**Keyboard and screen-reader participant:** do T2, T3, T5 and T9 without a mouse, with Narrator
if they use it.

## Measures

- **Per task:**
  - completed unaided, assisted, or failed;
  - time;
  - errors;
  - severity of each problem: 1 = blocks, 2 = major delay or wrong outcome, 3 = minor.
- **After each task:** "How confident are you that it did what you wanted?" (1–5).
- **After the session:** the System Usability Scale (SUS), plus three questions:
  - "Would you trust AxePower to change your PC's settings? Why?"
  - "Was there a moment you weren't sure what would happen?"
  - "What did you expect to stay on the PC after uninstalling?"

## Reporting

- **Each problem** goes in [REVIEW_FINDINGS.md](REVIEW_FINDINGS.md) with:
  - task;
  - participant number (no names);
  - what happened, with a quote if one was given;
  - severity;
  - a screenshot or timestamp.
- **No fixes.** The reviewer reports and does not propose fixes in the findings log. The owner
  turns confirmed findings into tasks.
- **No published results.** This plan and its results must not be described as a certification
  or as proof that AxePower is easy to use. It is one round of moderated testing.
