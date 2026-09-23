# Working notes for AI coding agents

## Music, providers and the LAN portal: keep the fix plan current

[`skillsMD/MUSIC_FIX_PLAN.md`](skillsMD/MUSIC_FIX_PLAN.md) is the live ledger of what is broken
or unproven in AxePower Music, the music sources, and the LAN portal shared with Phone Hub.

**If your work touches any of those, this is not optional:**

1. **Read it before you start.** Everything under *Open* is unproven or broken. Do not re-raise
   something already listed — add to that entry instead.
2. **Before you finish, delete every entry you fixed and verified.** Not a tick, not a
   strikethrough, not a "done" section: remove it. The commit that fixed it is the record. A
   ledger that only grows stops being read.
3. **Before you finish, add every issue you found**, including one you found and fixed only
   partly. The file has the entry shape and id scheme at the top.
4. Update the header's date and last-covered commit.

**What counts as open:** anything that has never actually run. Building, and passing tests
against fakes, is not verification of the real path. Say so plainly rather than reporting
something as working because its tests are green.

**What does not belong there:** work the plan simply has not reached yet. That goes in
*Not built yet*, which is a roadmap, not a defect list.

## Verifying changes in this repo

- `dotnet build AxePower.sln` then `dotnet test AxePower.Tests/AxePower.Tests.csproj`.
- `AxePower.exe --self-test` resolves the real container and prints a line per subsystem. Add a
  line when you add a subsystem; keep it read-only, so a self-test never creates user data.
- **Snapshot `%LOCALAPPDATA%\AxePower\settings.json` before a test run and compare it after.**
  Tests must leave it byte-identical. Use the `SettingsService(log, tempDirectory)` overload in
  any test that touches settings.
- The app relaunches elevated, so an agent cannot see the UI. Anything visual has to be handed
  to the owner to check, and listed as open until they have.

## Conventions

- Branch `v2.0` (one branch per release line). Do not push; commit locally unless told otherwise.
- Leave the untracked reference clones (`LenovoLegionToolkit/`, `lenovo_vantage/`) out of commits.
- UI must match the images in `ui reference/` closely.
