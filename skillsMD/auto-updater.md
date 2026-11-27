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
   - Compares the release tag with the running assembly version. `FallbackVersion` matches `Directory.Build.props` and is used only when there is no assembly version.
   - Verifies the signed update manifest and matches it to the advertised installer.
   - Returns an `UpdateCheckResult`: HasUpdate, version, title, notes, download URL, size, release URL.
2. **When it runs:** once at startup, then every hour, if `AutoCheckUpdates` is on. The About page's "Check for updates" button runs it on demand.
3. **Download** (`DownloadUpdateAsync`, when `AutoDownloadUpdates` is on):
   - Downloads the manifest's installer to `%TEMP%\AxePower_Update\` and checks its size and SHA-256.
   - When it finishes, the VM shows the Steam-style bottom banner ("downloaded and ready to install") with the scrollable patch notes.
4. **Apply** (`ApplyUpdate`) re-verifies the installer before launch. After launch succeeds, the app shuts down and [Inno Setup](../Installer/AxePower.iss) handles the upgrade.

The About page also shows community stats (`GetCommunityStatsAsync`: stars, open issues) from the GitHub API.

## Releasing a version the updater will pick up
Follow the [versioning guide](../docs/engineering/VERSIONING.md), [release pipeline](../docs/engineering/RELEASE_PIPELINE.md), and [signed-update procedure](../docs/security/SIGNED_UPDATES.md). A local unsigned installer can be used for local verification. The updater requires the published installer and its signed manifest.

## Gotchas
- The check is unauthenticated, so it is limited to 60 requests per hour per IP. Hourly checks are fine; tight loops are not.

## Tests
[UpdateServiceTests.cs](../AxePower.Tests/UpdateServiceTests.cs)
