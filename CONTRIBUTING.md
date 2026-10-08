# Contributing to AxePower

Thank you for improving AxePower. This is a Windows desktop application that can modify power plans, services, scheduled tasks, the registry, files, installed applications, and other system state. Contributions must preserve user control and make risky behavior explicit.

## Before opening a change

- Use Windows 10 build 1809 or later, or Windows 11, with the .NET 8 SDK.
- Search existing issues and pull requests before starting substantial work.
- For a security vulnerability, follow [SECURITY.md](SECURITY.md) and report it privately.
- Discuss large features, new external services, new privileges, or installer changes with the maintainer before implementation.
- Base work on the maintainer-designated release-line branch and create a focused topic branch. Do not combine unrelated cleanup or dependency upgrades.

## Build and test

From the repository root in PowerShell:

```powershell
dotnet restore AxePower.sln
dotnet build AxePower.sln -c Release --no-restore
dotnet test AxePower.sln -c Release --no-build
```

The separate Supabase policy suite requires Node.js and npm:

```powershell
npm.cmd install --prefix supabase\tests --no-package-lock --ignore-scripts --no-audit --no-fund
npm.cmd test --prefix supabase\tests
```

Do not commit `supabase/tests/node_modules` or a generated package lock from this verification command.

When application composition or service registration changes, also run the published app's read-only self-test:

```powershell
dotnet publish AxePower.App\AxePower.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false
& .\AxePower.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\AxePower.exe --self-test
```

The self-test must remain read-only and must not create or modify user data.

## Windows safety rules

- Unit tests must not require elevation or alter real system state by default.
- Use mocks, fakes, and test-specific temporary directories for registry, process, filesystem, network-listener, update, uninstall, and power-management paths.
- Snapshot `%LOCALAPPDATA%\AxePower\settings.json` before tests and verify that its bytes, hash, and timestamp are unchanged afterward.
- Never run live destructive, uninstall, registry-cleanup, service-control, scheduled-task, reboot, shutdown, or power-plan tests on a daily-use machine. Use an isolated disposable Windows VM and enable such tests explicitly.
- Keep live destructive tests skipped by default. State the exact environment and safeguards in the test documentation.
- Never weaken confirmation prompts, path validation, signature/hash checks, least-privilege behavior, or rollback/backup handling without explicit security review.
- Remove secrets, tokens, personal data, machine names, and user-specific paths from fixtures, logs, screenshots, and pull requests.

## Pull requests

- Keep each pull request focused and explain the user-visible outcome, risk, and rollback behavior.
- Link the relevant issue and list the exact verification commands and results.
- Add or update tests for behavior changes and document intentionally untested hardware or elevated paths.
- Preserve public APIs and configuration compatibility unless a breaking change has been approved for a future release line.
- Include screenshots for UI changes. Visual acceptance must be performed by the project owner because automated agents cannot validate the elevated desktop UI.
- Update relevant documentation, third-party notices, and the music/LAN fix ledger when the change falls within those areas.
- Do not include untracked reference or prototype repositories in a commit.

## Release boundaries

Only the maintainer publishes an official AxePower release. A normal contribution must not:

- Change product, assembly, or installer versions unless the pull request is explicitly a release change.
- Create or commit installer binaries or other generated release artifacts.
- Publish to the download repository, website, package feed, or update channel.
- Add or use signing certificates, release credentials, production Supabase credentials, or deployment secrets.
- Represent an unsigned local build as an official release.

Installer, updater, privilege, destructive-operation, authentication, database-policy, and release-pipeline changes require focused review and explicit validation beyond the unit test suite.

## Commit and review expectations

Write concise imperative commit messages. Respond to review feedback with additional commits until the change is accepted; maintainers may squash when merging. Do not force-push after review begins unless coordinated with the reviewer.

## Code of Conduct decision

A separate `CODE_OF_CONDUCT.md` has not been adopted because the project owner has not yet confirmed that one is wanted. This governance change therefore does not add one. Adoption or selection of a specific code of conduct requires explicit owner approval in a later change.

Until then, contributors are expected to communicate respectfully, focus review on the work, and avoid harassment or disclosure of private information.
