---
name: terminal
description: Built-in Terminal — one persistent cmd/PowerShell session with redirected I/O that inherits AxePower's elevation, and the shared output bus that Software Hub, Update PC and System Doctor stream into (with a Stop button that cancels their running task).
---

# Terminal

The Terminal page does two jobs:
1. It is an **interactive shell** (PowerShell by default, or CMD) inside the app.
2. It is the **central output console**. Software Hub installs, Update PC steps, restore points and System Doctor repairs all write their live output here, so users only ever look in one place.

## Where it lives

| Piece | File |
|---|---|
| Session + output bus | [TerminalService.cs](../AxePower.Core/Services/TerminalService.cs) |
| View / VM | [TerminalView.xaml](../AxePower.App/Views/TerminalView.xaml), [TerminalViewModel.cs](../AxePower.App/ViewModels/TerminalViewModel.cs) |
| Navigation hook | [MainViewModel.cs](../AxePower.App/ViewModels/MainViewModel.cs) subscribes to `NavigateToTerminalRequested` |

Sidebar: MAINTENANCE → **Terminal**.

## Shell session
- `StartSession(TerminalShell.PowerShell | Cmd)` kills any existing process tree and starts either `powershell.exe -NoLogo -NoProfile` or `cmd.exe /Q /K`, with stdin, stdout and stderr redirected and no window.
- `SendCommand(text)` writes to stdin, starting a session first if none is running. A multi-line string is sent as-is, which is how `InstallSelectedViaTerminal` runs a whole generated script.
- The shell **inherits AxePower's token**: if AxePower runs elevated, so does every command typed here. `TerminalService.IsProcessElevated()` is the shared "are we admin?" check used by other features.
- The output is plain text. There is no PTY or ANSI rendering, so full-screen or interactive TUI programs (vim, `winget` progress bars, prompts that read the console directly) will not display correctly.

## Output bus API (for feature code)

```csharp
_terminalService.RequestNavigateToTerminal();   // switch the UI to the Terminal page
_terminalService.AppendExternalLine(line);      // show a line from a background task
_terminalService.ActiveExternalCancel = () => cts.Cancel();  // let Stop cancel your task
// ... when the task ends:
_terminalService.ActiveExternalCancel = null;
```

The usual pattern, from `SoftwareHubViewModel.AppendLog`: keep any local log you need **and** mirror every line to `AppendExternalLine`.

## UI behaviour (`TerminalViewModel`)
- **Run** sends the input line and adds it to history. **↑ / ↓** walk the history (`HistoryPrevious` / `HistoryNext`).
- **Clear** empties the output view only.
- **Stop:**
  - If a background task registered `ActiveExternalCancel`, Stop cancels that task.
  - Otherwise Stop restarts the shell to interrupt whatever is running. There is no Ctrl+C signal to a redirected process.
- Toggle between PowerShell and CMD with `UsePowerShell` / `UseCmd`. Switching starts a fresh session.

## Who writes here
- [software-hub.md](software-hub.md): install, update, uninstall, batch installs.
- [update-pc.md](update-pc.md): upgrade all, restore point, graphics drivers, firmware.
- [system-doctor.md](system-doctor.md): SFC, DISM, winget reset, network reset (cancellable).
