# Visual verification: XAML split (Prompt 20)

**For:** the owner, before Prompt 20 is marked fully verified.
**Change:** `MainWindow.xaml` (2,612 → 282 lines) and `SoftwareHubView.xaml` (1,383 → 68 lines)
were cut into feature controls and resource dictionaries. Nothing was meant to look or behave
differently, except the colour fixes listed under *Intended changes*.

AxePower relaunches elevated, so these checks need a person in front of the real window.

## What is already proven automatically

You do not need to re-check these by eye. They are listed so you know what the manual pass is for.

- **Pixel comparison, before and after the split.** 14 states (6 shell, 8 Software Hub) were
  rendered off-screen under five skins (dark, light, glass, high-contrast, royal-amethyst-glass).
  All 70 images were identical, apart from the intended red icons below and the like counts in the
  patch notes, which load from the network and also differed between two runs before the split.
- **Keyboard and controller map, before and after.** For every visible control a keyboard can
  reach, these were identical: the order, accessibility name, command and position, and where each
  arrow key moves focus. The controller's D-pad sends arrow keys.
- **Tests that stay in the suite** (`AxePower.Tests/Shell/ShellViewTests.cs`):
  - Binding: no binding errors in any shell state or Software Hub tab/dialog, in dark, light and glass.
  - Resources: every resource the split files use exists in all 44 skins, and so does every
    `DynamicResource` in every XAML file of the app.
  - Skins: every skin compiles and builds its resources.
  - Navigation: the sidebar's Up/Down walk all 18 entries in order and wrap, the title-bar buttons
    step Left/Right, and the tray menu keeps its frame, item style, separators and all 13 items.

What automation cannot see: real DPI scaling, the live window frame and Mica/acrylic effects, hover
and pressed states, popups (the tray menu), focus rectangles, and how it feels with a controller.

## Before you start

1. Build and run AxePower as usual (it relaunches elevated).
2. For each skin below, switch in Settings and work through the list. Do the full list on
   **Dark**, then the quick list on **Light**, **Glass** and one accent skin (for example
   **Royal Amethyst Glass**).

## Full list (Dark)

### Main window and sidebar
- [ ] 1. The sidebar looks as before: logo, the CONTROL / CONNECTION / MAINTENANCE / UTILITIES
      groups, Settings, and your profile with its online dot.
- [ ] 2. Expand and collapse MAINTENANCE and UTILITIES; the chevrons turn and the items appear.
- [ ] 3. Click the collapse button (next to the logo): the sidebar folds to the icon rail; click the
      logo to unfold it. Make the window narrow: it folds on its own.
- [ ] 4. Every sidebar entry opens its page, and the highlight follows.
- [ ] 5. Title bar: "Relaunch with Admin" (only when not elevated), Music ▾ (when something has
      played), Help, Minimise, Maximise/Restore, and Close (to the tray). Hover each: grey
      highlight, red on Close.
- [ ] 6. Drag the window by the empty title bar; double-click it to maximise. The page under the
      title bar still takes clicks on pages without the battery pill.
- [ ] 7. Dashboard: the battery pill and the music player sit where they did.

### Help window
- [ ] 8. Help opens the tool help window over everything. Pick other tools from the list on the
      right: the guide changes **and starts at the top**.
- [ ] 9. Clicking the dark backdrop, or ✕, closes it.

### Update banner and patch notes
- [ ] 10. When an update is ready, the Steam-style banner shows along the top with its button and ✕.
- [ ] 11. Open the patch notes (the Profile page's patch notes row, or the banner's link). The posts scroll with the
      mouse wheel, and **the rail's ▲ and ▼ arrows scroll the feed**. The reaction buttons (like,
      dislike, comment, share) look and work as before. ✕ closes it.

### Tray menu (a popup; automated tests cannot render it)
- [ ] 12. Right-click the tray icon: same frame, and Eco/Smart/Beast/Game Mode with their coloured
      icons, then the separators and the rest of the items.
- [ ] 13. Items highlight on hover, and each one does what it did (power modes, open pages, Second
      Monitor, Exit). Double-clicking the tray icon brings the window back.

### Software Hub (Update PC)
- [ ] 14. The header and the three tabs look as before; switch between them.
- [ ] 15. **System & Driver Updaters**: the hero banner, graphics driver cards (with the NVIDIA picker
      button on NVIDIA), BIOS & firmware panel, the driver updater card and "Upgrade All Apps".
- [ ] 16. **Gaming Runtimes & Tools**: the category rail expands and collapses, categories filter,
      search filters, and the selection toolbar wraps below on a narrow window.
- [ ] 17. Select packages: the "N Selected" pill updates, and Install Selected (Direct) and Install
      in Terminal enable.
- [ ] 18. During an install, the card shows **Cancel** and the toolbar shows **Cancel** (both with a
      red ✕) and they stop the install.
- [ ] 19. **Windows Update Shield**: both policy switches, the cache repair and the restore point card.
- [ ] 20. A website link shows the warning dialog (Cancel / Open in Browser). "NVIDIA version" opens
      the driver picker (notebook/desktop toggle, version list, notes, actions); ✕ closes it.

### Keyboard and controller
- [ ] 21. With the keyboard: Tab moves through the sidebar entries in order, then the title-bar
      buttons, then the page. Enter or Space activates the focused button.
- [ ] 22. With the arrow keys in the sidebar: Up/Down move entry by entry and wrap at the ends.
- [ ] 23. With a controller (Game Mode → controller navigation on): the stick moves the pointer over
      the sidebar, the title bar and the Software Hub as before; the D-pad moves focus; A clicks.

## Quick list (Light, Glass and one accent skin)

- [ ] Q1. Sidebar, title bar and the open page use the skin's colours (no white or black blocks).
- [ ] Q2. Help window, patch notes and the tray menu are readable.
- [ ] Q3. Software Hub: all three tabs, the warning dialog and the NVIDIA picker are readable.

## Intended changes

These looked wrong before because their colour key, `DangerBrush`, existed in no skin. They now use
the skin's error red (`ErrorBrush`):
- Software Hub: the red ✕ on the card and toolbar **Cancel** buttons, and the **Uninstall** label.
- System Doctor: the **Cancel Task** button's icon and label.
- Terminal: the icon on the **Stop** button.

In Debloat, LAN Drop, Phone Hub and Logs, references to `SurfaceBrush` / `SurfaceHoverBrush` (also
never defined) became `Transparent`, which is how they always drew. **Nothing there should look
different.**

## Sign-off

| Skin | Full or quick | Date | Result / notes |
|---|---|---|---|
| Dark | full | | |
| Light | quick | | |
| Glass | quick | | |
| Accent skin: ______ | quick | | |

When every box is ticked, tell me (or note it here). Prompt 20 is then marked fully verified in
`SOFTWARE_COMPLETION_PROMPTS.md`. Report anything that differs with the item number and skin.
