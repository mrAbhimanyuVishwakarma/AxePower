# Tracked Warning Debt & Static Analysis Policy

## 1. Overview and Architecture

AxePower enforces a **zero-warning policy** for all production and test builds. To eliminate technical debt without masking latent bugs:
1. Microsoft .NET code analyzers are centrally enabled via `Directory.Build.props` (`EnableNETAnalyzers=true`, `AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true`).
2. High-priority security and reliability diagnostic rules are enforced through [`.editorconfig`](../../.editorconfig).
3. Continuous integration strictly fails on warnings via `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` when running under CI (`ContinuousIntegrationBuild=true`).
4. Blanket project-level suppressions are strictly prohibited. There are none: the last one, in `AxePower.Core`, was removed in Prompt 22 (below).

---

## 2. Global Suppressions Ledger

**Empty.** `AxePower.Tests/AnalyzerPolicyTests.cs` fails if `Directory.Build.props`, App, Tests or
Core gains a `<NoWarn>`, if Core gains `null!` / `default!`, or if Core gains `#nullable disable` or
`#pragma warning disable CS86xx`.

### Removed in Prompt 22: `AxePower.Core.csproj` nullable flow warnings (2026-10-06)

```xml
<NoWarn>$(NoWarn);CS8600;CS8602;CS8604</NoWarn>   <!-- added in 506cbf4 (v1.1), removed in Prompt 22 -->
```

Measured before removal: with the line deleted, a clean Release build of Core produced **zero**
CS8600, CS8602 or CS8604 warnings. A deliberately planted dereference of a `string?` was reported,
so the compiler was checking. The code had been fixed over time and the suppression no longer hid
anything. What did hide nullability was nine `null!` / `default!` uses, now gone:

| Where | What it hid | Fix |
|---|---|---|
| `DisplayService.TrySetRefreshRateCCD` | `QueryDisplayConfig` was called with null arrays. Windows answers `ERROR_INVALID_PARAMETER` (87), checked on the owner's PC, so the DisplayConfig path never ran and every refresh-rate change came from GDI. | The dead path, `SetDisplayConfig` and its constants were removed (owner's choice). `SetRefreshRate` uses GDI only, which is what happened in practice. |
| `DisplayService.GetDisplayInfo` | `EnumDisplaySettings(null!, ...)` although the parameter is already `string?` | Plain `null`. |
| `SettingsService.TryLoadFile`, `PackageInput.TryWebUrl` / `TryDownloadUrl`, `UninstallCommandParser.TryParse` | Try-pattern `out` values that are null on failure | `[NotNullWhen(true)] out T?`. The callers already handled null, except the next row. |
| `GitHubPortableToolDownloader.DownloadAsync` | The fallback address's allow-list result was ignored, and `HttpClient.GetAsync` accepts a null `Uri` | The download is refused with an error line when the known release fails the allow-list too. |
| `MsiUninstallExecutor.UninstallAsync` | A loop variable started as `null!` | The last attempt returns directly (`StillBusy`). |
| `SHFILEOPSTRUCT.pTo`, `WINTRUST_DATA.pwszURLReference` | Native string fields that Windows reads as NULL | Declared `string?`. |

**What the compiler cannot see: JSON nulls.** System.Text.Json writes an explicit `null` into a
property declared non-nullable. Prompt 22 found that a `settings.json` containing, for example,
`"appRules": null` loaded "successfully", after which every `GetSettings()` threw
`ArgumentNullException` in `AppSettings.Clone()`. Thirteen settings properties were exposed that way.
`AxePower.Core/Helpers/JsonNullRepair.cs` now runs right after deserialisation:

- In files AxePower saves itself (settings, playback state, recently played, recent searches,
  account session, saved chat, uninstaller backup index, skins), a null goes back to the value a new
  instance starts with, null list items are removed, and a record with no default to fall back on
  is rejected.
- In network input (room chat messages, update manifest), an incomplete message is refused.
  Before, a room chat message with `"chunkData": null` passed decryption and then threw inside the
  host's chat loop.

Also fixed: `ShareRoomClient` threw `KeyNotFoundException` for a host reply without a nonce. It now
gives its own "The host returned no nonce." error.

Tests: `AxePower.Tests/Nullability/` (JSON files, network replies, wrong-typed registry values,
sensors without readings), plus a portable-download test in `SoftwareHubOperationsTests`. The
xUnit1031 warnings in two Prompt 15 fuzz tests (blocking `GetResult()` in a test method) were also
fixed, so a clean Release rebuild of the whole solution has 0 warnings again.

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
