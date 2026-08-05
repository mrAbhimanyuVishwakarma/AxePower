# AxePower Testing and Quality

* **Build after XAML structure changes**: Always verify that the WPF UI compiles.
* **Tests after logic changes**: Always run the `AxePower.Tests` project after modifying services or view models.
* **Inspect WPF binding output**: Look for DataBinding errors in the debug output window to ensure Views match ViewModels.
* **Test settings persistence**: Verify that `settings.json` saves and loads atomically.
* **Test Auto Switch cancellation**: Ensure the background service respects `CancellationToken`s and gracefully exits.
* **Test low-battery precedence**: Low battery states must override performance mode desires.
* **Test corrupt JSON recovery**: Application must load defaults if `settings.json` is mangled.
* **Test single-instance behaviour**: Verify that launching a second executable brings the first one to the foreground instead of crashing.
* **Test tray lifecycle**: The application should minimize to the system tray and close cleanly when exited from the context menu.
* **Test responsive layouts**: Ensure windows do not clip when resized.
* **Test keyboard operation**: Verify primary flows can be achieved without a mouse (Tab navigation).
* **Test 125%, 150%, and 200% scaling**: Ensure text and layouts scale correctly on high-DPI displays.
* **Test no-battery and unsupported-mode states**: The UI must handle desktop PCs gracefully (no battery available).
* **Never use fake success reports**: Always report the genuine success or failure of an action.
* **Record real commands and results**: Do not fabricate terminal output.
