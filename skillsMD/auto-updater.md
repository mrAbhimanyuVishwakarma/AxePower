---
name: auto-updater
description: AxePower self-update — GitHub Releases check (AxePower_Download then AxePower repo), hourly silent background check, optional silent installer download to %TEMP%, Steam-style update banner and patch notes, "Restart & Install".
---

# Auto-Updater (AxePower self-update)

AxePower keeps itself up to date from GitHub Releases. This is separate from Update PC, which updates Windows, drivers and other apps (see [update-pc.md](update-pc.md)).

## Where it lives

| Piece | File |
|---|---|
| Check / download / apply | [UpdateService.cs](../AxePower.Core/Services/UpdateService.cs) |
| About page, modal, banner | [AboutView.xaml](../AxePower.App/Views/AboutView.xaml), [AboutViewModel.cs](../AxePower.App/ViewModels/AboutViewModel.cs) |
| Background loop | `MainViewModel.CheckUpdateInBackgroundAsync` + hourly `DispatcherTimer` ([MainViewModel.cs](../AxePower.App/ViewModels/MainViewModel.cs)) |
| Settings | `autoCheckUpdates`, `autoDownloadUpdates`, `notifyOnNewRelease` (all default `true`) in [AppSettings.cs](../AxePower.Core/Models/AppSettings.cs) |

## Flow
1. **Check** (`CheckForUpdatesAsync`):
   - Calls `GET https://api.github.com/repos/mrAbhimanyuVishwakarma/AxePower_Download/releases/latest`, falling back to the `AxePower` repo.
   - Parses the tag (`ParseSemanticVersion`) and compares it with the running assembly version. `FallbackVersion = "1.8.0"` is used only when there is no assembly version.
   - `FindBestAsset` prefers an `.exe` asset.
   - Returns an `UpdateCheckResult`: HasUpdate, version, title, notes, download URL, size, release URL.
2. **When it runs:** once at startup, then every hour, if `AutoCheckUpdates` is on. The About page's "Check for updates" button runs it on demand.
3. **Download** (`DownloadUpdateAsync`, when `AutoDownloadUpdates` is on and the asset is `.exe` or `.msi`):
   - Streams the file to `%TEMP%\AxePower_Update\<asset name>` (default name `AxePower_Setup.exe`), reporting progress.
   - When it finishes, the VM shows the Steam-style bottom banner ("downloaded and ready to install") with the scrollable patch notes.
4. **Apply** (`ApplyUpdate`) starts the installer with `UseShellExecute`, and the app then calls `Application.Current.Shutdown()`. The Inno Setup installer ([Installer/AxePower.iss](../Installer/AxePower.iss)) handles the upgrade.

The About page also shows community stats (`GetCommunityStatsAsync`: stars, open issues) from the GitHub API.

## Releasing a version the updater will pick up
1. Bump the assembly version in [AxePower.App.csproj](../AxePower.App/AxePower.App.csproj), and update `FallbackVersion`.
2. Publish, then compile `Installer/AxePower.iss` (see the README's build section).
3. Create a GitHub Release on **AxePower_Download** with a tag such as `v1.8.1`, and attach the `.exe` installer as an asset.

## Gotchas
- The check is unauthenticated, so it is limited to 60 requests per hour per IP. Hourly checks are fine; tight loops are not.
- The hourly loop does not check `IsUpdateDownloaded`, so it downloads the same installer again every hour until the user installs it.
- The installer is not signature-checked before it runs. AxePower trusts whatever asset the release carries.

## Tests
[UpdateServiceTests.cs](../AxePower.Tests/UpdateServiceTests.cs)
