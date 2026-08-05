---
name: axepower-wpf-engineer
description: Guides implementation, redesign, debugging, testing, review, and release work for the AxePower .NET 8 WPF application using strict MVVM, CommunityToolkit.Mvvm, safe Windows power integration, responsive Fluent-inspired XAML, accessibility, and verified builds.
---

# AxePower WPF Engineering Skill

You must follow these rules whenever working on AxePower, specifically when handling WPF, XAML, dashboard redesign, navigation, styles, themes, ViewModels, power-plan services, battery services, Auto Switch, tray behaviour, settings, installer preparation, testing, debugging, or release review.

## A. Inspect before editing

Always inspect relevant existing files before creating replacements.

You must:
* Reuse existing services and commands.
* Reuse existing dependency injection.
* Reuse existing resource dictionaries.
* Identify the real DataContext.
* Identify existing properties and commands.
* Avoid creating duplicate services or duplicate state.
* Check `git status` before major changes.
* Preserve uncommitted work.
* Avoid destructive Git commands.

## B. Architecture rules

Require strict MVVM:
* Views contain presentation and layout.
* ViewModels contain observable presentation state and commands.
* Core services contain Windows and business logic.
* Views must never run `powercfg`.
* Services must not reference WPF controls.
* Code-behind is limited to genuine window and native platform integration.
* Use constructor injection.
* Use interfaces for system-facing services.
* Use `[ObservableProperty]`.
* Use `[RelayCommand]` and `[AsyncRelayCommand]`.
* Use `CancellationToken`.
* Avoid `async void` except genuine event handlers.
* Dispose event subscriptions and native resources.
* Keep `AxePower.App` and `AxePower.Core` decoupled.

## C. UI implementation workflow

Before editing XAML:
1. Inspect the View.
2. Inspect its ViewModel.
3. Inspect existing styles and converters.
4. Identify real available data.
5. Identify commands already implemented.
6. Create a short implementation plan.
7. Make focused changes.
8. Build immediately after structural XAML changes.
9. Check for WPF binding errors.
10. Fix errors before moving to another page.

Require responsive WPF layouts using:
* Grid
* Shared size groups where appropriate
* Viewbox only when genuinely suitable
* ScrollViewer for overflowing content
* WrapPanel or responsive card layouts
* Dynamic sizing
* MinWidth and MaxWidth
* Text wrapping

Avoid:
* Canvas for primary application layout
* Absolute positioning
* Screenshot-specific fixed dimensions
* Large fixed margins used to force alignment
* Clipped buttons
* Controls placed against window edges
* Empty unused dashboard areas
* Hard-coded screen-size assumptions

## D. UI design system

Require:
* Windows 11 Fluent-inspired appearance
* Dark navy visual identity
* Rounded cards
* Restrained gradients
* Subtle glow
* Clear hierarchy
* Segoe UI Variable with Segoe UI fallback
* Semantic theme resources
* Consistent spacing
* Consistent corner radii
* Consistent typography
* Visible hover, selected, focus, disabled, loading, success, warning, and error states

AxePower mode identity:
Eco Mode: Green accent, Energy efficiency
Smart Mode: Blue accent, Balanced everyday use
Beast Mode: Orange/red accent, Maximum available performance

Require semantic resources such as:
WindowBackgroundBrush, NavigationBackgroundBrush, SurfaceBrush, SurfaceElevatedBrush, CardBorderBrush, PrimaryTextBrush, SecondaryTextBrush, DisabledTextBrush, AccentBrush, SuccessBrush, WarningBrush, DangerBrush, EcoBrush, SmartBrush, BeastBrush, FocusBrush.

Do not spread hard-coded colours throughout Views.

## E. Honest data rule

Never add fake data to production UI.
Do not invent:
* Battery-history points
* Battery savings
* Battery health
* Temperature
* Fan speed
* GPU power
* Performance gains
* Application detection
* Charging estimates
* System optimisation scores

When real data is unavailable, show an honest state such as:
* Collecting battery history…
* Not reported by this device
* No activity recorded yet
* This power mode is not supported on this device

Remove or disable non-functional controls. Every visible button must:
* Work
* Be disabled with a clear reason
* Or be removed

## F. Accessibility requirements

Require:
* Logical Tab order
* Visible keyboard focus
* Enter and Space activation
* Accessible control names
* `AutomationProperties.Name`
* `AutomationProperties.HelpText` where useful
* Tooltips for icon-only buttons
* No important information communicated only by colour
* Windows contrast-theme compatibility
* Text scaling support
* Keyboard-only primary flows
* Reduced-motion support
* Readable contrast

## G. Safety rules

AxePower manages Windows power settings. Require:
* No arbitrary shell commands.
* No command strings from user input.
* No disabling critical Windows services.
* No deleting user files.
* No unsafe registry modifications.
* No permanent administrator requirement.
* No destructive reset of all Windows power plans.
* No deleting user-created power plans.
* No repeated duplication of Ultimate Performance.
* No false success messages.
* Verify the active scheme after applying it.
* Preserve the previous scheme on failure where possible.
* Log detailed technical failure information.
* Show concise user-facing errors.
* Never modify the developer’s real power plan in automated tests.

## H. Performance rules

Require:
* No tight polling loops
* Event-driven power detection where possible
* No blocking the UI thread
* No repeated unnecessary scheme enumeration
* Bounded logs
* Bounded battery history
* Cancellation of stale operations
* No duplicate automatic transitions
* No unnecessary JSON writes
* Fast application startup
* Proper disposal

## I. Verification loop

After UI changes:
```powershell
dotnet restore
dotnet build -c Debug
dotnet build -c Release
```

After ViewModel, service, settings, or business-logic changes:
```powershell
dotnet restore
dotnet build -c Debug
dotnet build -c Release
dotnet test -c Release
```

You must:
* Read actual command output.
* Fix build failures.
* Fix test failures.
* Check XAML resource errors.
* Check binding errors.
* Never claim success without running the command.
* Never stop at the first failure.

## J. Completion report

At the end of every substantial AxePower task, report:
* Files inspected
* Files modified
* Files created
* Existing functionality preserved
* UI changes
* Logic changes
* Build command and actual result
* Test command and actual result
* Known limitations
* Manual verification still required
