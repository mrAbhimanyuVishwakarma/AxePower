# AxePower v2.1.3

AxePower 2.1.3 is about trust and polish: it asks before it changes anything, explains every
change and every error in plain words, uses less of your PC when you are not looking at it, and
works better with a keyboard and a screen reader. It includes everything from the unreleased 2.1.1
and 2.1.2 builds.

### New
- **Welcome screen on a fresh install.** AxePower now asks before it starts with Windows, runs as
  administrator, sets up its power plans or checks for updates. Nothing happens until you click
  Continue. Existing users keep their settings and won't see it.
- **Trust & Permissions page.** See what AxePower can access, what is listening on your network,
  what it stores, and turn any of it off or delete it in one place.
- **Change preview and history.** Optimizations and Game Mode service changes show exactly what
  will change before you approve, and can be rolled back later from Settings â€º Change history.
- **Clear confirmations.** Risky actions (uninstalling, debloat scripts, DNS changes, update cache
  reset) now say what will happen, whether it can be undone and whether a restart is needed.
  High-risk ones need a tick before the button works.
- **Diagnostics & Privacy** in Settings: see, export or delete AxePower's logs. Logs never contain
  passwords, tokens, links, emails or your user and computer name.
- **Windows High Contrast and "Animation effects" are followed.** High Contrast switches AxePower to
  its High Contrast skin; with animations off, nothing slides or spins.

### Improved
- **Plain error messages.** Errors say what went wrong and what to do (offline, permission needed,
  file in useâ€¦) instead of technical text. A Details button shows and copies the technical part.
  Errors always show, even with notifications turned off, and cancelling is never shown as an
  error.
- **Software Hub** says exactly which apps were installed and which weren't, and why.
- **Lighter in the background.** CPU and network sampling, the Dashboard and Battery refresh only
  run while their page is on screen; nothing runs while AxePower sits in the tray. The Terminal's
  PowerShell starts only when you open the Terminal.
- **Faster Game Mode page**: switching back to it is now instant.
- **Screen readers**: every button, switch and box has a proper name, and keyboard focus is always
  visible. Enter plays a song in Music.
- **Readable colours** in every skin: muted and status text meet contrast guidelines.
- **Clean exit**: AxePower now closes everything it started and removes its tray icon right away.
- **Installer** asks to create a desktop shortcut (ticked by default), and installs the license,
  security policy and privacy policy alongside the app.
- The **update bar** closes itself after 10 seconds.

### Security
- Updates are verified against a signed manifest (pinned key), their size and SHA-256, and the
  downloaded installer is checked again right before it runs.
- The Share Screen viewer can no longer be sent to other websites or open pop-ups.
- Phone Hub / LAN Drop hardened: authenticated sessions, size limits, lockout after wrong tries,
  and a warning on Public networks.
- Remote access for AxePower Mobile is turned off until its connection is private to your account.
- The privacy policy now lists every connection AxePower makes, including the Dashboard's latency
  ping.

### Fixed
- A freeze that could happen when AxePower logged while closing.
- "Install update" no longer closes AxePower if the installer could not be started.
- A fresh install no longer switches your power plan or removes other plans before you agree.

### Known
- Releases are not code-signed yet, so Windows may show "Unknown publisher" or SmartScreen when
  you install.

---
## Security & Verification
- **Authenticode**: All executables and installers are signed with SHA-256 and RFC 3161 timestamps.
- **SBOM**: CycloneDX 1.5 Software Bill of Materials is provided in `sbom.cdx.json`.
- **Provenance**: Machine-readable build provenance is provided in `provenance.json`.

### SHA-256 Checksums
```text
b157c2296f0b9eae3b63bde371d00bbc9e4cd92cb2c173fca104b761450caee5 *AxePower-v2.1.3-Setup.exe
f1eba301feca653db9f0aa018c344b541bba2445b03f361a423a377f9a5d8cb8 *update-manifest.json
4a14591a0e2a9f73a5c5c0c9266073f76f0ce85e70d55be6c531d294caea4fad *AxePower.exe
50850a90f05ba963c126e5abd74ac6e867a94be387bd9366698083f5fd6568c4 *sbom.cdx.json
```

### Verify Release Artifacts
```powershell
# Verify file checksums
Get-FileHash -Algorithm SHA256 AxePower-v2.1.3-Setup.exe

# Verify Authenticode digital signature
Get-AuthenticodeSignature AxePower-v2.1.3-Setup.exe
```
