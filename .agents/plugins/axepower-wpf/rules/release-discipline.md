# AxePower Release Discipline

* **No placeholders in Release**: Production builds must never contain placeholder text, "lorem ipsum", or stubbed features.
* **No fake screenshots**: Visuals provided in READMEs must be accurate representations of the current UI.
* **No unimplemented visible features**: If a feature is not ready, do not surface a button for it in the UI.
* **Accurate README**: Update `README.md` to accurately reflect the current state of the application.
* **Correct version metadata**: Ensure `.csproj` properties correctly map to the intended release version.
* **Correct application icon**: Ensure the production build contains the AxePower `.ico`.
* **Debug and Release builds**: Test both configurations (`-c Debug` and `-c Release`).
* **Tests**: All tests in `AxePower.Tests` must pass on Release configuration before deployment.
* **Self-test**: Use the `--self-test` startup argument to silently verify the single-instance mutex and service container resolution.
* **Publish validation**: Ensure the `dotnet publish` command successfully produces the portable executable without missing DLLs.
* **Installer validation**: Verify that Inno Setup script (`AxePower.iss`) correctly compiles the deployment package.
* **No destructive Git operations**: Do not force-push over main or run `git reset --hard` destroying user work.
* **No public release or push without approval**: Explicitly request user authorization before executing `git push` or merging PRs.
* **Clearly label unsigned builds**: If code signing certificates are absent, document this limitation.
* **Report externally blocked items honestly**: If an API or tool is unavailable during deployment, notify the user.
