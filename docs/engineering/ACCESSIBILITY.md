# Accessibility

**For:** anyone changing AxePower's WPF interface, and the owner running the manual checks.
**Last updated:** 2026-10-06 (Prompt 28).

AxePower aims at WCAG 2.2 AA where it applies to a Windows desktop app: every core workflow works
from the keyboard and a controller, every control has a screen-reader name, text is readable in
every skin, and nothing is said by colour alone. This file records what is checked automatically,
the manual test matrix, its results, and the known exceptions.

---

## What is checked automatically

These run with `dotnet test AxePower.Tests/AxePower.Tests.csproj` and fail the build's tests on a
regression.

| Check | Test | What it enforces |
|---|---|---|
| Screen-reader names | `Shell/AccessibleNamesTests` | Every focusable control on all 20 sidebar pages has a readable UI Automation name (not empty, not an icon glyph). About 1,100 controls. |
| Name inference | `Shell/AccessibleNamesTests` | Icon-and-label buttons are named by their label, toggles by the label beside them, icon-only buttons by their tooltip, and a name set in XAML always wins. |
| Text contrast | `SkinContrastTests` | In all 42 skins in `skins/`: primary, secondary, muted, success, warning, error and accent text reach 4.5:1 on the window, cards and raised cards; primary-button text reaches 4.5:1 on the button. |
| High Contrast | `Shell/HighContrastFollowTests` | With Windows High Contrast on, the High Contrast skin shows in place of the chosen one, and the choice comes back when it is off. |
| Reduced motion | `Shell/ReducedMotionTests` | Every XAML animation takes its duration from `{a11y:Motion}`, every busy spinner hides through `{a11y:MotionOnly}`, and code that animates checks `Motion.IsReduced`. |
| Visible focus | `Shell/KeyboardFocusVisibilityTests` | A style or control that turns off WPF's focus rectangle draws its own focus state. |
| Keyboard and D-pad | `Shell/ShellViewTests`, `ControllerNavigationTests` | Tab order and arrow-key (D-pad) movement through the shell and Software Hub (Prompt 20). |

### How names are given

[`AccessibleNames`](../../AxePower.App/Helpers/AccessibleNames.cs) is switched on for each window
with `a11y:AccessibleNames.Enabled="True"` on the root `<Window>`. A window built in code calls
`AccessibleNames.SetEnabled(window, true)`. For a control with no name of its own it binds
`AutomationProperties.Name`, in this order, to:

1. its visible text, skipping icon-font glyphs;
2. for a toggle, dropdown, text box or slider, the label laid out before it in the same row;
3. its tooltip.

**When you add a control:**

- Set `AutomationProperties.Name` yourself when none of those fits. Typical cases: a check box in
  a list row whose label comes after it (`"{Binding Name, StringFormat='Select {0}'}"`), an
  icon-only button with a rich tooltip, or a text box with no label.
- Template parts and dividers are not tab stops: give them `Focusable="False"`.
- If you hide the focus rectangle (`FocusVisualStyle="{x:Null}"`), add an `IsKeyboardFocused`
  trigger that shows focus (border in `FocusBrush`).
- Animations use `Duration="{a11y:Motion 0:0:0.16}"`. A busy spinner next to status text uses
  `Visibility="{a11y:MotionOnly}"`.
- A status shown by colour also needs text, a tooltip, or a shape difference.
- A new skin must pass `SkinContrastTests`; the failure message names the colour to change.

---

## Fixed in Prompt 28

- **Names:** 449 of about 1,090 focusable controls had no usable name (Narrator read "button" or
  "check box"). Now none do. Explicit names were added where inference could not work: the Game
  Mode process and service rows, the System Doctor rows, the Debloat credit buttons and the
  Terminal output. ComboBox drop-down buttons and separators stopped being tab stops.
- **Contrast:** 46 colours in 26 skins were nudged in lightness (hue kept) to reach 4.5:1. Most
  moved a few percent. The largest change is Polar Night's error colour, from `#BF616A` to
  `#FAA6AE`: a red on its mid-grey cards can only reach 4.5:1 at that lightness. Royal Amethyst's
  primary button was darkened (`#A855F7` → `#9938F6`) so its white text reads.
- **High Contrast:** AxePower now follows Windows High Contrast, switching to its High Contrast
  skin and back. Settings → Theme says so while it is on.
- **Reduced motion:** the six toggle-switch slide animations become instant, and the two busy
  spinners (Debloat restore point, System Care optimiser) hide, when Windows' *Animation
  effects* is off. Lyrics scrolling already followed it.
- **Colour-only status:** the sidebar profile dot was the only status shown by colour alone
  (green online, grey offline). Offline is now a hollow ring with a tooltip, and the profile
  button reports the status to screen readers.
- **Focus:** the YT Music button and Music song rows hid the focus rectangle and showed nothing;
  both now draw a focus border. Enter on a focused song row plays it, as a click does.

---

## Manual test matrix

Automated checks cannot see the screen or hear Narrator. The app relaunches elevated, so these
are run by the owner (see `OWNER_CHECKS.md`, A8). Results go in the last column.

| # | Setup | Do | Pass when | Result |
|---|---|---|---|---|
| M-1 | Narrator on (Ctrl+Win+Enter) | Tab through Dashboard, Game Mode, Settings, Software Hub and Music | Every stop is read with a meaningful name and its role; no bare "button" or "check box" | not run |
| M-2 | Narrator on | Open a high-risk confirmation (Uninstaller) and the operation preview | The dialog title, tick box and buttons are read; Cancel is announced as the default | not run |
| M-3 | Keyboard only, mouse unplugged or untouched | Switch power mode, start Game Mode, install one app in Software Hub, change a setting, play a song (Enter) | All five done without the mouse; focus is always visible | not run |
| M-4 | Keyboard only | Tab through each page start to end | Focus never gets stuck and never disappears; the order follows the layout | not run |
| M-5 | Display scaling 200% (Settings → System → Display) | Open every sidebar page at the smallest window size | No clipped or overlapping text; every control still reachable | not run |
| M-6 | Text size 150% (Settings → Accessibility → Text size) | Dashboard, Settings, a confirmation dialog | Text grows and is not clipped | not run |
| M-7 | Windows High Contrast on (Left Alt+Left Shift+Print Screen) | Open AxePower; open Settings → Theme; turn High Contrast off | The High Contrast skin shows and the note appears; your skin returns when it is off | not run |
| M-8 | Animation effects off (Settings → Accessibility → Visual effects), then restart AxePower | Flip a toggle in Settings and in Game Mode; start a System Care scan | Toggles snap instead of sliding; no spinner, the status text still shows | not run |
| M-9 | Xbox or PlayStation controller | Move with the D-pad through the sidebar and a page; press A on a button | Focus moves predictably and A activates | not run |
| M-10 | Dark, Light, Polar Night, Solar Cream and one glass skin | Look at muted text, status badges and the primary button | All readable; colours still look like the skin | not run |

---

## Known exceptions

These are open, not fixed. Each says why and what would close it.

1. **The shrunk Dashboard widget can only be moved with the mouse.** It is dragged by its top
   strip. Moving it is a placement convenience, not a workflow. To close: arrow keys on a
   focusable strip move the window.
2. **Reduced motion follows a change only on pages opened after it.** Storyboards in templates are
   frozen when they load. A restart picks the change up everywhere.
3. **Acrylic (glass) skins are measured against the window's tint, not the real desktop.** Behind
   the blur, a very bright wallpaper can lower contrast. Owner check M-10 covers one glass skin.
4. **Card sheen is not measured.** Skins with a sheen lighten the top-left of cards slightly; the
   test uses the base card colour.
5. **High Contrast uses AxePower's own black, white and yellow skin**, not the colours of the
   user's chosen Windows contrast theme (Aquatic, Desert, Dusk, Night sky).
6. **Game Mode's caution icon** (the triangle beside some process and service names) has no
   tooltip or text saying what it warns about.
7. **Names come from the visible label, so they are only as good as the label.** A few inferred
   names may be terse (for example "Open" on a row button); M-1 is how these are found.
8. **200% scaling, Narrator, High Contrast and controller behaviour have not been run** (matrix
   above). Until they have, they are unverified, whatever the tests say.
