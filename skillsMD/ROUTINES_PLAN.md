---
name: routines-plan
description: Plan for Routines — Samsung-style "If … Then …" rules on the PC (time, apps, power, battery, Wi-Fi, Bluetooth, displays, lock/idle, temperature, phone → power mode, close/open apps, timer, notify, audio, brightness, music), with undo when a routine ends, living with the power timer under Tools → Timer & Routines.
---

# Routines — Plan

> **Status:** planned; only the Tools cards exist · **Written:** 2026-09-27 · **Desktop:** v2.0.9 (branch `v2.0.9`)

Goal: let the owner write simple **If … Then …** rules, the way Samsung's *Routines* works, and
have AxePower carry them out and put things back when the "If" stops being true.

> If **a game starts** and **I'm plugged in** → **Beast mode**, **close Discord and the browser**.
> When the game closes, go back to how it was.

Only if-then routines. No Samsung-style *Modes*.

## Decisions

| Question | Decision |
|---|---|
| What it is | **If-then routines only** (owner, 2026-09-27). |
| Where it lives | **Tools → "Timer & Routines"** section: a **Timer** card and a separate **Routines** card (owner, 2026-09-27). |
| Auto Switch and Game Mode app rules | **Open.** Move them in as ready-made routines, or leave them where they are. |
| Two routines want the same setting | **Open.** Suggested: the one that started most recently wins. |
| Release | **v2.0.9** (owner, 2026-09-27). |

## How Samsung does it

- A routine is a list of **If** conditions (all must be true) and a list of **Then** actions.
- Conditions are either a **state** ("connected to home Wi-Fi", "between 23:00 and 07:00") or an
  **event** ("an app opens", "at 07:00").
- For a state routine, turning on *Undo when it ends* restores what the actions changed once the
  state is no longer true. An event routine just runs; there is nothing to undo.
- Routines show on a list with an on/off switch, and a running one is marked as running.
- Ready-made routines are offered to start from.

We copy all of that. We also allow **"any of these"** as well as **"all of these"**.

## Where we start from

| Existing piece | What it becomes |
|---|---|
| [PowerTimerService](../AxePower.Core/Services/PowerTimerService.cs) — shut down / restart / sleep after a set time, with a 2-minute warning | The **Timer** half of the page, and a **"Start the power timer"** action. The only way a routine can shut down, restart or sleep, so the warning always appears. |
| [AutoSwitchService](../AxePower.Core/Services/AutoSwitchService.cs) — plugged in / on battery / low battery → power mode | Three ready-made routines, if we decide to move it in. Its hysteresis and switch delay are what the battery condition and the engine's debounce should copy. |
| [AppMonitorService](../AxePower.Core/Services/AppMonitorService.cs) — WMI process start/stop, needs admin | The **"App is running"** condition. |
| [AppKillerService](../AxePower.Core/Services/AppKillerService.cs) (Game Booster) — close apps, restart them later | The **"Close apps"** action, with restart as its undo. |
| [PowerPlanService](../AxePower.Core/Services/PowerPlanService.cs) `ApplyModeAsync` | The **"Power mode"** action. |
| [DynamicModeAutomationService](../AxePower.Core/Services/DynamicModeAutomationService.cs) `OnModeChangedAsync` — never called | Nothing directly; it stays out of this plan. |
| Tools → **Timer & Routines** section | Built 2026-09-27. The **Timer** card shows the timer's status and opens the timer. The **Routines** card says "Coming soon" and has no button; in R1 it opens the routines page. |

## The rule

```
Routine
  Name, Icon, Enabled
  Match: All | Any
  If:    one or more Conditions        (state or event)
  Then:  one or more Actions           (in order)
  UndoWhenItEnds: bool                 (state routines only)
```

Stored under a new `routines` key in `settings.json`. A file without the key loads no routines.

### If — conditions

| Condition | Kind | How we detect it | Phase | Notes |
|---|---|---|---|---|
| Time of day, days of week | state (range) or event (at a time) | a one-minute clock | R1 | |
| App is running / opens / closes | state / event | WMI process watch (admin), polling fallback | R1 | AxePower normally runs elevated; the fallback keeps it working when not. |
| Plugged in / on battery | state | `SystemEvents.PowerModeChanged` + power status | R1 | |
| Battery at or below N % | state | power status, 5 % hysteresis like Auto Switch | R1 | |
| Connected to Wi-Fi network X | state | network change event + WLAN API | R2 | **Windows 11 24H2 hides the SSID unless the app has location access.** Must handle "can't tell" honestly. |
| Bluetooth device connects | state | `DeviceWatcher`, connected Bluetooth devices | R2 | Headphones, controllers. |
| External display connected | state | `SystemEvents.DisplaySettingsChanged` + display count | R2 | |
| PC locked / unlocked, idle for N min, woke from sleep | state / event | `SessionSwitch`, `GetLastInputInfo`, `PowerModes.Resume` | R2 | |
| CPU or GPU above N °C | state | existing telemetry ([cpu-gpu-telemetry.md](cpu-gpu-telemetry.md)) | R2 | Needs a hold time so a spike does not flap it. |
| Phone connected to Phone Hub | state | Phone Hub service event | R4 | Touches the LAN portal: update [MUSIC_FIX_PLAN.md](MUSIC_FIX_PLAN.md). |

No condition reads something a routine can set (for example "power mode is Beast"), so a routine
can never trigger itself.

### Then — actions

| Action | Undo when it ends | How | Phase | Notes |
|---|---|---|---|---|
| Set power mode | previous mode | `PowerPlanService.ApplyModeAsync` | R1 | |
| Close apps | reopen them | `AppKillerService` | R1 | Same list picker as Game Booster. |
| Open an app, file or website | — | `Process.Start` | R1 | |
| Start the power timer (shut down / restart / sleep in N min) | cancel the timer | `PowerTimerService.Start` | R1 | The 2-minute warning always shows. |
| Show a notification | — | `NotificationService` | R1 | |
| Set volume / mute | previous volume | Core Audio | R3 | |
| Set default audio output | previous device | `IPolicyConfig` (undocumented COM) | R3 | Undocumented: may break in a Windows update. |
| Set brightness | previous brightness | WMI `WmiMonitorBrightnessMethods` | R3 | Laptop panel only. |
| Set refresh rate | previous rate | existing refresh-rate code ([power-modes.md](power-modes.md)) | R3 | |
| Play / pause music, play a playlist | pause | `IMusicPlayerService` | R3 | Touches Music: update [MUSIC_FIX_PLAN.md](MUSIC_FIX_PLAN.md). |
| Do Not Disturb on/off | previous state | **no public API** | research | Only if a supported way exists. |

Shutting down, restarting or sleeping directly is **not** an action. It goes through the power timer so
there is always a warning and a way to cancel.

## Engine

- **One evaluator.** Condition sources raise "changed"; the engine re-evaluates only the routines
  that use that source, after a short debounce (Auto Switch uses 5 s).
- **State routines** start when their If becomes true and end when it becomes false. **Event
  routines** run once per event, with a cool-down so a flapping app cannot fire them in a loop.
- **Undo is per setting, not per routine.** Each setting keeps a stack: the original value at the
  bottom, then each running routine's value. When a routine ends, its entry is removed and the
  setting returns to whatever is now on top. That is what makes "game ends, but I'm still on
  battery" land on the battery routine's mode instead of the value from before both.
- **Conflicts:** the routine that started most recently is on top (pending the decision above).
- **Manual changes win.** If the owner changes a setting a routine set, that routine stops owning
  it and won't undo it.
- **At startup** routines evaluate once the app is up. At exit, running routines are undone.
  Settings that reboot away (closed apps) are not restored after a crash; say so in the UI.
- Every start, end, action and failure is logged. `--self-test` gets a read-only line: routine
  count, enabled count, and which condition sources are available on this PC (admin for WMI,
  SSID readable, Bluetooth present).

## The page — Tools → Timer & Routines

1. **Timer** at the top: the same controls as the corner flyout (shut down / restart / sleep,
   hours and minutes, presets, start, cancel, +10 min), bound to the same `PowerTimerViewModel`,
   so both always agree.
2. **Routines** below: a list of cards, one per routine — icon, name, a one-line
   "If … → Then …" summary, a *Running* badge, and an on/off switch.
3. **Add routine** opens the builder: **If** (add conditions, All/Any), **Then** (add actions,
   in order), *Undo when it ends*, name and icon. Every condition and action is picked from a list,
   never typed as text.
4. **Ready-made routines** to start from:
   - *Gaming* — a chosen game runs → Beast, close background apps.
   - *Battery saver* — on battery and ≤ 20 % → Eco, brightness 40 %.
   - *Night* — 23:00–07:00 → Eco, volume 20 %.
   - *Bedtime* — 01:00 and idle 15 min → sleep timer 10 min.
   - *Headphones* — Bluetooth headphones connect → play music.

There is no image for this page in `ui reference/`; it follows the Tools cards. The owner checks it.

## Phases

| Phase | What | Done when |
|---|---|---|
| **R0** | Tools section "Timer & Routines": Timer card (status + opens the timer) and a Routines card marked "Coming soon". | **Built 2026-09-27**; owner to check in the real window. |
| **R1** | Engine, storage, page (Timer section, list, builder, ready-made routines). Conditions: time, app, power source, battery. Actions: power mode, close apps, open app, power timer, notify. Undo stacks. | Unit tests over fake sources and actions, then the owner runs *Gaming* and *Battery saver* for real. |
| **R2** | Conditions: Wi-Fi, Bluetooth, display, lock/idle/wake, temperature. | Each seen changing on the real PC, including SSID with location access off. |
| **R3** | Actions: volume, audio output, brightness, refresh rate, music; Do Not Disturb if possible. | Each applied and undone on the real PC. |
| **R4** | Phone: "phone connected" condition; run a routine from AxePower Mobile. | Seen from a real phone. |
| **R5** | Auto Switch and app rules as ready-made routines (only if decided). | Existing settings migrate once, and old behaviour is unchanged. |

## Risks

- **SSID needs location access** on Windows 11 24H2 and later. Without it, the Wi-Fi condition
  must show "can't tell" rather than silently never matching.
- **WMI process watch needs admin.** The polling fallback is slower (a few seconds).
- **Audio output switching and Do Not Disturb** have no supported API.
- **Tests must not touch the real machine:** fake condition sources and fake actions, and the
  `SettingsService(log, tempDirectory)` seam.
