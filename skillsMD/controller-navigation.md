---
name: controller-navigation
description: Controller Navigation — built-in Gopher360-style gamepad-to-mouse/keyboard emulation for Xbox (XInput) and PlayStation (DualShock 4 / DualSense) controllers, toggled from the Dashboard or Game Mode.
---

# Controller Navigation

This feature turns a gamepad into a mouse and keyboard for couch or handheld use. It is a C# port of the Gopher360 engine, with no external executable.

## Where it lives

| Piece | File |
|---|---|
| Engine | [ControllerNavigationService.cs](../AxePower.Core/Services/ControllerNavigationService.cs) |
| Dashboard toggle | [DashboardViewModel.cs](../AxePower.App/ViewModels/DashboardViewModel.cs): `ToggleControllerNavigationAsync` |
| Game Mode controls | [GameModeViewModel.cs](../AxePower.App/ViewModels/GameModeViewModel.cs): `ToggleControllerNavigationAsync`, `SetXboxMode`, `SetPs5Mode`, `RefreshController`, `OpenControllerHelp` |
| In-app guide | `MainViewModel.InitializeToolGuides()`, entry `Gopher360` |

## How it works
- **XInput is loaded dynamically:** `LoadLibrary` tries `xinput1_4.dll`, then `xinput1_3.dll`, then `xinput9_1_0.dll`, and binds `XInputGetState` / `XInputSetState` through `GetProcAddress`. If none loads, `StartAsync` fails with a logged error.
- **The loop** polls at 150 Hz (`FPS = 150`) and sends mouse and keyboard input with `SendInput`. Cursor movement is sub-pixel smoothed. Clicking the right stick cycles the cursor speed (low → medium → high) with a short vibration, and Y launches `osk.exe`.
- **Controller type** (`DetectConnectedGamepad`) checks RawInput first for Sony DualShock/DualSense devices, then XInput for Xbox pads. The UI shows the matching glyphs (Xbox or PS5 mode).
- **State** is `ControllerNavigationState` (`Running` / stopped). `IsRunning()` is read on Dashboard load so the toggle matches.

## Default mapping (from the in-app guide)

| Input | Action |
|---|---|
| Left stick | Move cursor |
| A / ✕ | Left click |
| X / ▢ | Right click |
| Right stick | Scroll (vertical and horizontal) |
| Y / △ | Open the on-screen keyboard |
| D-pad | Arrow keys |
| Back + Start | Toggle navigation on/off |

## Notes
- DualSense and DualShock pads only produce XInput input through Steam Input, DS4Windows or similar. RawInput detection only decides which glyphs to show.
- Injected input does not reach elevated windows (UIPI) unless AxePower itself is elevated.
- If you change the mapping, update the `Gopher360` guide text in `MainViewModel` so it stays accurate.

## Tests
[ControllerNavigationTests.cs](../AxePower.Tests/ControllerNavigationTests.cs)
