---
name: debloat
description: Debloat utility — launches Raphire's Win11Debloat script elevated from its official short URL, focuses an already-open instance, credits the author.
---

# Debloat (Win11Debloat by Raphire)

A one-click launcher for [Win11Debloat](https://github.com/Raphire/Win11Debloat), a third-party script that removes preinstalled apps, disables telemetry and more. AxePower does not bundle the script; it downloads and runs the current version each time.

## Where it lives

| Piece | File |
|---|---|
| Launcher | [DebloatService.cs](../AxePower.Core/Services/DebloatService.cs) |
| Sidebar command | `MainViewModel.Debloat()` / `OpenDebloatCredit()` ([MainViewModel.cs](../AxePower.App/ViewModels/MainViewModel.cs)) |
| Help modal entry | `InitializeToolGuides()`, id `Debloat` |

Sidebar: UTILITIES → **Debloat**. It is a command, not a page.

## What it runs
`LaunchDebloatUtility()` starts PowerShell with `Verb = "runas"`, which shows a UAC prompt:

```
powershell -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((irm 'https://debloat.raphi.re/')))"
```

- If a window whose title contains "Win11Debloat" or "Debloat" is already open, the service focuses it instead of starting another.
- The method returns `false` when the user cancels UAC or the launch fails. The VM then shows "Debloat utility was cancelled or failed to start."

## Safety notes
- This downloads and runs remote code with admin rights. Its behaviour depends on the upstream script at run time, not on anything in this repo.
- Changes are made by Win11Debloat, not AxePower. Undoing them is up to that tool (it has a restore option for default apps) or to a restore point.
- For AxePower's own reversible tweaks, see [system-optimizer.md](system-optimizer.md).

## Credit
Keep the credit link (`OpenDebloatCredit` → the GitHub repo) and the README acknowledgement whenever you change this feature.
