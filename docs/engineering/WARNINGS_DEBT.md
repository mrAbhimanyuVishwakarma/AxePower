# Tracked Warning Debt & Static Analysis Policy

## 1. Overview and Architecture

AxePower enforces a **zero-warning policy** for all production and test builds. To eliminate technical debt without masking latent bugs:
1. Microsoft .NET code analyzers are centrally enabled via `Directory.Build.props` (`EnableNETAnalyzers=true`, `AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true`).
2. High-priority security and reliability diagnostic rules are enforced through [`.editorconfig`](../../.editorconfig).
3. Continuous integration strictly fails on warnings via `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` when running under CI (`ContinuousIntegrationBuild=true`).
4. Blanket project-level suppressions are strictly prohibited. The single legacy suppression in `AxePower.Core` is cataloged below with an explicit remediation plan.

---

## 2. Global Suppressions Ledger

### `AxePower.Core.csproj` — Nullable Flow Warnings
```xml
<NoWarn>$(NoWarn);CS8600;CS8602;CS8604</NoWarn>
```

| Warning ID | Description | Root Cause / Scope | Remediation Plan |
|---|---|---|---|
| **CS8600** | Converting null literal or possible null value to non-nullable type. | Native Win32 API interop, registry value lookups, and WMI query results where nullability was unannotated in earlier .NET frameworks. | **Prompt 22 ("Complete nullable correctness")**: Introduce guarded null checks, typed result wrappers, and proper nullable reference annotations. |
| **CS8602** | Dereference of a possibly null reference. | Third-party hardware monitoring library (`LibreHardwareMonitorLib`) and complex nested sensor hierarchies. | **Prompt 22**: Replace unguarded access with safe navigation operators (`?.`) and pattern matching. |
| **CS8604** | Possible null reference argument for parameter. | Passing registry query returns or optional process information into utility helpers. | **Prompt 22**: Audit parameter nullability across Core services and add regression tests for missing-data paths. |

> **Rationale for Preserving in Prompt 7**:  
> Per engineering standards, mass-adding null-forgiving operators (`!`) or dummy values merely to silence compiler diagnostics is dangerous and conceals runtime null-reference crashes. Prompt 22 will methodically fix each nullable path with unit tests for registry, JSON, and network corner cases.

---

## 3. Resolved Warnings Ledger (Prompt 7)

During Prompt 7, existing warnings in `AxePower.App` were investigated and surgically resolved:

| Warning ID | File & Location | Issue | Resolution |
|---|---|---|---|
| **CS0108** | [`AxePower.App/Views/PowerTimerControl.xaml.cs`](../../AxePower.App/Views/PowerTimerControl.xaml.cs) | `private static DependencyObject? Parent(...)` hid inherited member `FrameworkElement.Parent`. | Renamed helper method to `GetParentNode(node)`, preserving behavior and eliminating the compiler conflict. |
| **MVVMTK0034** | [`AxePower.App/ViewModels/SettingsViewModel.cs`](../../AxePower.App/ViewModels/SettingsViewModel.cs) | Backing field `_currentSettings` was directly referenced instead of the generated `[ObservableProperty]` `CurrentSettings`. | Updated field access to `CurrentSettings.HiddenTrayMenuItems`, adhering to CommunityToolkit.Mvvm conventions. |

With these resolutions, clean release rebuilds of `AxePower.sln` produce **0 warnings and 0 errors**.

---

## 4. Policy for Future Suppressions

1. **No New Blanket Suppressions**:
   No new `<NoWarn>` entries may be added to `Directory.Build.props` or any individual `.csproj` file.
2. **Narrow, Documented Suppressions Only**:
   If an analyzer rule produces a demonstrably false positive, suppression is only permitted using narrow in-line pragmas:
   ```csharp
   #pragma warning disable CAxxxx // Justification: [Explain why analyzer is incorrect in this specific context]
   ... single line or block ...
   #pragma warning restore CAxxxx
   ```
3. **CI Regression Prevention**:
   Any PR that introduces a compiler or analyzer warning will fail the GitHub Actions `build-and-test` job because `TreatWarningsAsErrors` is active.
