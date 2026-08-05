# AxePower WPF Plugin

This is a workspace-specific Antigravity plugin for the AxePower repository. It provides specialized rules, guidelines, and skills tailored to the AxePower project to ensure consistent and high-quality AI engineering.

## What it does

This plugin configures Antigravity to strictly adhere to the AxePower engineering standards:
* **Architecture**: Enforces .NET 8 WPF, CommunityToolkit.Mvvm, and strict separation between `AxePower.App` and `AxePower.Core`.
* **UI Quality**: Enforces responsive, accessible, Fluent-inspired UI without fake data or hard-coded assumptions.
* **Safety**: Protects Windows power-plans and local environments from destructive commands.
* **Testing & Release**: Enforces rigorous verification via builds, tests, and installer checks prior to claiming success.

## Installation Location

This plugin is installed in the repository's workspace customizations directory:
`C:\Users\Abhimanyu Vishwakarm\GitHub\AxePower\.agents\plugins\axepower-wpf`

## Skills Provided

* **`axepower-wpf-engineer`**: Guides Antigravity on how to correctly inspect, modify, and verify AxePower code while maintaining safety and UI fidelity.

## Rules Provided

The plugin contains several rules that apply to the workspace:
* `architecture.md`: Defines strict MVVM constraints.
* `ui-design.md`: Controls visual identity and structural UI approach.
* `system-safety.md`: Safeguards against reckless execution.
* `testing-and-quality.md`: Mandates validation cycles.
* `release-discipline.md`: Prevents shortcuts during production builds.

## Verification

To confirm the repository is healthy, you can run the verification script included in this plugin:

```powershell
powershell -ExecutionPolicy Bypass -File ".agents\plugins\axepower-wpf\skills\axepower-wpf-engineer\scripts\verify-axepower.ps1"
```

## How to confirm discovery

To confirm Antigravity has discovered the plugin:
1. Reload the project/workspace or restart Antigravity if necessary.
2. Open Customisations in the IDE.
3. Confirm `axepower-wpf` is detected and the `axepower-wpf-engineer` skill is available.

## Modification

This plugin applies specifically to the current workspace and does not modify AxePower source files by itself. To disable it, remove or rename the `.agents\plugins\axepower-wpf` directory.
