# AxePower System Safety

* **Safe Windows Power-Plan Access**: Prefer native P/Invoke APIs for power management.
* **Controlled `powercfg` Use**: If `powercfg` is required, use explicit allow-listing for arguments and executables.
* **No Arbitrary Command Execution**: Never execute arbitrary shell commands or command strings sourced from user input.
* **Verification After Mode Changes**: Always query the active power scheme to verify success after applying a change.
* **Capability Detection**: Detect available power plans dynamically. Do not assume hardcoded GUIDs are always available or active.
* **No False Success State**: If a transition fails, report the error honestly. Do not show a success notification if the API failed.
* **No Destructive Operations**: 
  * No destructive registry or Windows service modifications.
  * No deleting user files or user-created power plans.
  * No destructive reset of all Windows power plans.
* **No Permanent Admin Execution**: AxePower must run as a standard user.
* **Safe Settings Storage**: Store settings safely under `%LOCALAPPDATA%\AxePower`. Use atomic writes to prevent corruption.
* **Safe Failure and Recovery**: Preserve the previous power scheme on failure where possible. Implement robust recovery logic.
* **No Telemetry**: Do not integrate telemetry or analytics tracking.
* **No Secrets in Logs**: Ensure sensitive personal paths or data are redacted from exported diagnostics.
* **Automated Tests**: Automated tests must use fakes and must never modify the developer's real system power plan.
