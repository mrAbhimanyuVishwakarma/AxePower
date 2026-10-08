# First run and safe defaults

**For:** anyone adding a setting or something that runs at start-up.
**Last updated:** 2026-10-08 (Prompt 31).

## Rule

A fresh install performs no system or network action until the user agrees. A fresh install
means there is no settings file, or the file can't be read. The rule covers:

- start-up entries;
- elevation;
- power plans;
- update checks;
- listeners;
- sign-in;
- uploads;
- optimisations.

On first launch AxePower shows its welcome screen (`Windows/WelcomeWindow`,
`ViewModels/WelcomeViewModel`) before anything else. Each optional capability is explained where it
is asked for, pre-set to the recommended choice, and applied only on **Continue**.

**Not now** (or closing the window) applies nothing:

- AxePower opens without elevation, start-up entry, power-mode change or update check.
- The welcome appears again at the next launch.

## What a fresh install starts with

`AppSettings.CreateForNewInstall()`, then the welcome's choices:

| Setting | Before Continue | Recommended on the welcome | Why |
|---|---|---|---|
| Start with Windows | Off | Off | A scheduled task at sign-in is something to ask for |
| Run as administrator | Off | On | Power plans, sensors, Game Mode services and cleaning need it; the screen says so, and Windows asks |
| Automatic power modes (`AutoSwitchEnabled`, `ApplyExpectedModeOnStartup`) | Off | On | The core feature; the screen says it creates AxePower's four plans and removes other custom plans |
| Check for updates at start-up | Off | On | One request to GitHub; nothing installs without asking |
| Download updates automatically | Off | Off | Downloading an installer is left to the user |

**Always off until the user turns them on, as before:**

- Phone Hub / LAN Drop listener.
- Share Screen rooms.
- Account sign-in.
- Remote access (disabled in code).
- Diagnostics upload: there is none; a bundle is only saved when asked.
- Routines: none exist.
- App rules.
- Every optimisation and cleaning action. Each runs only from its button, through the confirmation
  standard.

## What changed at start-up

- **Elevation** waits for the welcome: there is no UAC prompt on first launch until the user
  chooses Run as administrator and clicks Continue.
- **The start-up task** is not registered or touched until the welcome is completed.
- **Applying Smart at start-up** now honours `ApplyExpectedModeOnStartup`. That setting existed
  but was never read: AxePower switched mode on every launch. The first switch creates AxePower's
  power plans and deletes other custom ones. Existing users have it on, so nothing changes for
  them.
- **The start-up update check** already honoured `AutoCheckUpdates`, which a new install keeps
  off until Continue.

## Existing users

A settings file written before this change has no `firstRunCompleted` key. It is read as an
existing install:

- every value is kept exactly;
- the welcome never shows.

`FirstRunTests.AnExistingSettingsFile_IsKeptExactly_AndNeverSeesTheWelcome` serialises an old file
and the loaded result, and compares them. No security migration was needed: nothing an existing
user has chosen is less safe than the user intended.

## Tests

- **`FirstRunTests`:**
  - fresh-install values;
  - the welcome persists until Continue;
  - old files are kept exactly;
  - recommended choices;
  - Continue saves exactly what was chosen;
  - update download follows update check;
  - no power-mode change at start-up without agreement, with a positive control.
- **`WelcomeWindowTests`:** renders in Dark, Light and Glass without binding errors; every switch
  and button has a screen-reader name; Continue is the default button.

## Owner check

A11 in `OWNER_CHECKS.md`: a real fresh-install run.
