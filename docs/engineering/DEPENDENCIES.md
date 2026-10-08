# Dependency Remediation Record

## Audit identity

- Date: 2026-10-03 (Asia/Calcutta)
- Branch: `v2.1.1---monitization-features`
- Base commit: `b68f0019f850a051a736b60277a57c922ec581cc`
- Scope: GHSA-2m69-gcr7-jv3q in the transitive native SQLite dependency
- Status: remediated and validated in the current working tree

The repository already contained unrelated modified and untracked files. They were preserved. The only product dependency change made for this remediation is in `AxePower.Core/AxePower.Core.csproj`.

## Before

`dotnet list AxePower.sln package --include-transitive` resolved this chain for `AxePower.Core`, and transitively for `AxePower.App` and `AxePower.Tests`:

| Package | Relationship | Resolved version |
|---|---|---:|
| `Microsoft.Data.Sqlite` | Direct reference in `AxePower.Core` | 8.0.11 |
| `Microsoft.Data.Sqlite.Core` | Transitive | 8.0.11 |
| `SQLitePCLRaw.bundle_e_sqlite3` | Transitive | 2.1.6 |
| `SQLitePCLRaw.core` | Transitive | 2.1.6 |
| `SQLitePCLRaw.lib.e_sqlite3` | Transitive | 2.1.6 |
| `SQLitePCLRaw.provider.e_sqlite3` | Transitive | 2.1.6 |

The baseline vulnerability audit associated `SQLitePCLRaw.lib.e_sqlite3 2.1.6` with high-severity advisory `GHSA-2m69-gcr7-jv3q`.

## Version selection and change

The following command queried the configured NuGet source (`https://api.nuget.org/v3/index.json`):

```powershell
dotnet list AxePower.sln package --outdated --include-transitive --highest-minor
```

On 2026-10-03, it reported `Microsoft.Data.Sqlite 8.0.31` as the newest available release in the existing 8.0 servicing line. The direct reference was changed from `8.0.11` to `8.0.31`. No other direct dependency and no unrelated major version was changed.

After restore, the solution resolves:

| Package | Relationship | Resolved version |
|---|---|---:|
| `Microsoft.Data.Sqlite` | Direct in Core; transitive in App/Tests | 8.0.31 |
| `Microsoft.Data.Sqlite.Core` | Transitive | 8.0.31 |
| `SQLitePCLRaw.bundle_e_sqlite3` | Transitive | 2.1.12 |
| `SQLitePCLRaw.core` | Transitive | 2.1.12 |
| `SQLitePCLRaw.lib.e_sqlite3` | Transitive | 2.1.12 |
| `SQLitePCLRaw.provider.e_sqlite3` | Transitive | 2.1.12 |

NuGet reports `2.1.13` as the newest standalone SQLitePCLRaw release, but `Microsoft.Data.Sqlite 8.0.31` resolves `2.1.12`. The resolved `2.1.12` chain is secure according to the post-change NuGet vulnerability audit; no direct SQLitePCLRaw override was introduced.

## Validation

### Restore

```powershell
dotnet restore AxePower.sln
```

Result: succeeded for `AxePower.Core`, `AxePower.App`, and `AxePower.Tests`. An initial restricted-network attempt could not reach NuGet's repository-signature endpoint (`NU1301`); the approved network-enabled retry completed successfully.

### Release build

```powershell
dotnet build AxePower.sln -c Release --no-restore -t:Rebuild
```

Result: succeeded with 0 errors and 4 emitted warnings (2 distinct pre-existing warnings):

- `CS0108` in `AxePower.App/Views/PowerTimerControl.xaml.cs:65`
- `MVVMTK0034` in `AxePower.App/ViewModels/SettingsViewModel.cs:451`

### Desktop tests

```powershell
dotnet test AxePower.sln -c Release --no-build
```

Result: 1,120 passed, 0 failed, 1 skipped, 1,121 total. The skipped test was the existing intentionally destructive `AxePower.Tests.Uninstaller.LiveEchoMusicUninstallTest.RunControlledUninstallAndScan_EchoMusic` test.

The user settings file was unchanged across the run:

- Path: `%LOCALAPPDATA%\AxePower\settings.json`
- Length before/after: 8,430 bytes
- SHA-256 before/after: `1DCBEBED18A1EF828B6EA739246816E19D2B9A3E8591D53F45288791BC81B3C7`
- Last-write UTC before/after: `2026-10-03T13:05:42.8684675Z`

### Supabase backend tests

```powershell
npm.cmd install --prefix supabase\tests --no-package-lock --ignore-scripts --no-audit --no-fund
npm.cmd test --prefix supabase\tests
```

Result: 48/48 passed. The temporary `supabase/tests/node_modules` directory was removed after the run, and no package lock was created.

### Vulnerability proof

```powershell
dotnet list AxePower.sln package --vulnerable --include-transitive
```

Result (NuGet source `https://api.nuget.org/v3/index.json`):

```text
The given project `AxePower.Core` has no vulnerable packages given the current sources.
The given project `AxePower.App` has no vulnerable packages given the current sources.
The given project `AxePower.Tests` has no vulnerable packages given the current sources.
```

Therefore, the high-severity SQLite advisory `GHSA-2m69-gcr7-jv3q` is no longer present in the resolved solution dependency graph.

---

## Deterministic Dependency Locking & Policy (Prompt 6)

### 1. Lock Files Architecture

To ensure builds are 100% reproducible and immune to silent transitive dependency drift, NuGet package lock files (`packages.lock.json`) are committed to version control for all tracked projects:
- [`AxePower.App/packages.lock.json`](../../AxePower.App/packages.lock.json)
- [`AxePower.Core/packages.lock.json`](../../AxePower.Core/packages.lock.json)
- [`AxePower.Tests/packages.lock.json`](../../AxePower.Tests/packages.lock.json)

In `Directory.Build.props`, lock file generation and enforcement are centrally enabled:
```xml
<!-- Deterministic dependency locking policy -->
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<RestoreLockedMode Condition="'$(ContinuousIntegrationBuild)' == 'true' or '$(CI)' == 'true'">true</RestoreLockedMode>
```

### 2. CI Enforcement (`--locked-mode`)

In GitHub Actions CI (`.github/workflows/ci.yml`), restoration is executed with locked-mode enforcement:
```powershell
dotnet restore AxePower.sln --locked-mode
```
If any direct or transitive dependency diverges from what is recorded in `packages.lock.json`, the restore operation fails with error `NU1004`, preventing unauthorized supply-chain changes from entering the build.

### 3. Procedure for Intentional Dependency Updates

When intentionally updating a package:
1. Update the `<PackageReference>` version in the project file (or via `dotnet add package <PackageName> -v <Version>`).
2. Run a force-evaluated restore locally to re-resolve the dependency graph and update the lock files:
   ```powershell
   dotnet restore AxePower.sln --force-evaluate
   ```
3. Run the complete test suite and release build:
   ```powershell
   dotnet build AxePower.sln -c Release
   dotnet test AxePower.Tests/AxePower.Tests.csproj -c Release
   ```
4. Run the dependency audit script to confirm no new vulnerabilities were introduced:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\audit-dependencies.ps1
   ```
5. Commit the updated project file (`.csproj`) together with the refreshed `packages.lock.json`.

### 4. Categorized Update Policy

AxePower classifies dependency updates into three tiers:

| Category | Definition | Action in CI | Policy / Handling |
|---|---|---|---|
| **High / Critical Vulnerabilities** | Any package advisory rated High or Critical by NuGet / GitHub Advisory Database. | **FAIL** (blocks PR & build) | Mandatory, immediate remediation. Must not be bypassed. |
| **Normal Outdated Packages (Minor / Patch)** | Backward-compatible servicing releases within the same major version. | Logged as advisory; build continues. | Reviewed and merged during scheduled routine maintenance. |
| **Major-Version Upgrades** | Breaking-change releases (e.g. .NET major platform jumps, NAudio 2 → 3, xUnit 2 → 3). | Logged distinctly with "REQUIRES MANUAL REVIEW". | Must be tested in a dedicated feature branch. Requires explicit architectural review and compatibility verification before merging. |

### 5. Scheduled Dependency Checks (No Auto-Merge)

1. **GitHub Dependabot** ([`.github/dependabot.yml`](../../.github/dependabot.yml)): Runs weekly on Mondays at 06:00 UTC, opening at most 5 PRs for maintainer review. Automatic merging is strictly prohibited.
2. **Weekly CI Schedule** ([`.github/workflows/ci.yml`](../../.github/workflows/ci.yml)): Triggers every Monday to audit all dependencies against the latest vulnerability databases.

## QR code encoding (2026-10-06)

The hand-written QR encoder in `AxePower.Core/Helpers/QrCodeGenerator.cs` produced codes that no scanner could read (even the content `HELLO` did not decode). It was replaced by a maintained library rather than repaired.

| Package | Project | Relationship | Version | License | Why |
|---|---|---|---:|---|---|
| `QRCoder` | Core (flows to App, Tests, UpdateSigning) | Direct | 1.8.0 | MIT | Encodes the QR codes for the LAN Drop portal, Phone Hub and the About page. Only `QRCodeGenerator` and `QRCodeData.ModuleMatrix` are used; AxePower draws the SVG and BMP itself. |
| `System.Drawing.Common` | Core | Transitive (from QRCoder) | 6.0.0 | MIT | Pulled in by QRCoder's .NET 6+ target. AxePower does not call it. |
| `ZXing.Net` | Tests only | Direct | 0.16.11 | Apache-2.0 | A real decoder that reads every generated QR code back in `QrCodeDecodeTests` and the LAN Drop end-to-end test. Not shipped. |

Lock files were refreshed with `dotnet restore AxePower.sln --force-evaluate`, and `dotnet list AxePower.sln package --vulnerable --include-transitive` reported no vulnerable packages afterwards.

