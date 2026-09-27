# AxePower skins

A skin is one folder with one file, `skin.json`. About twenty core colours describe the look, and
AxePower works out the rest of its 61 theme resources from them when the skin is applied.

```
skins/
  dark/skin.json       built-in
  light/skin.json      built-in
  glass/skin.json      built-in
  _template/skin.json  commented starting point (ignored by the app: it starts with "_")
  <your-skin>/skin.json
```

## Where AxePower looks

| Folder | What goes there |
|---|---|
| `skins\` next to `AxePower.exe` | Skins that ship with AxePower. Everything in this repo folder is copied there on build and goes into the installer. |
| `%APPDATA%\AxePower\Skins\` | Your own skins. Settings → Theme has a folder button that opens it. |

The folder is re-read each time Settings opens, so a new skin shows up in Settings → Theme without
restarting AxePower. `dark`, `light` and `glass` are also built into the app, so a theme always
exists even if the folders are empty. A folder copy with one of those ids is ignored.

A broken skin is skipped, never applied. The reason is written to the log, and
`AxePower.exe --self-test` lists it on the "Skins:" line.

## Make a skin

1. Copy `_template` to a new folder named like the skin's id, e.g. `skins\ocean-night\`.
2. Set `id` (lower-case letters, digits and dashes), `name` and `base`.
3. Change the colours. **Delete any colour you don't care about**: it falls back to the base skin.
   The smallest useful skin is:
   ```json
   { "id": "teal", "name": "Teal", "base": "dark", "colors": { "accent": "#14B8A6" } }
   ```
4. Open Settings → Theme and pick it.

Colours are `#RGB`, `#RRGGBB`, or `#AARRGGBB` for see-through (`AA`: `00` clear … `FF` solid).
Comments (`//`) and trailing commas are allowed.

## Fields

| Field | Meaning |
|---|---|
| `base` | `"dark"` (light text) or `"light"` (dark text). Also picks the fallback colours, the title bar mode, the logo, the tray menu and the music-track colour. |
| `colors.window` | App background |
| `colors.sidebar` | Left navigation panel (also the secondary background) |
| `colors.card` / `cardElevated` | Regular cards / raised cards, inputs, tiles inside cards |
| `colors.hover` | Hovered rows and buttons |
| `colors.border` / `divider` | Outlines / thin separator lines |
| `colors.textPrimary` … `textDisabled` | Four text levels: headings, descriptions, hints, disabled |
| `colors.accent` | Buttons, links, focus ring, sliders, music accent |
| `colors.success` / `warning` / `error` | Status colours |
| `colors.eco` / `smart` / `beast` | Power mode colours |
| `colors.buttonPrimary` / `buttonPrimaryText` | The call-to-action button (Apply) and its text |
| `glass.backdrop` | `"none"`, or `"acrylic"` to blur the desktop behind the window. Give `window`, `sidebar` and the card colours an alpha so the blur shows. |
| `glass.sheen` | 0–100: top-left highlight on cards (100 is the music player's) |
| `glass.rim` | `true`: a lit card edge, brightest at the top-left |
| `glass.shadow` | `true`: a soft shadow under cards that use the shared card style |
| `overrides` | Pin any theme resource by name (see below) |

## What AxePower works out for you

| Resource | From |
|---|---|
| `AccentHoverBrush` | accent, 25% lighter on dark / 15% darker on light |
| `AccentPressedBrush` | accent, 20% darker on dark / 30% darker on light |
| `PrimaryBlueBrush`, `FocusBrush`, `MusicAccentBrush` | accent |
| `TitleBarBackground*` | window (transparent with acrylic) |
| `WindowSecondaryBackground*` | sidebar |
| `Card*Brush` | card colours, as a sheen gradient when `glass.sheen` > 0 |
| `CardBorderBrush` | border, or the lit rim when `glass.rim` is on |
| `CardShadowEffect` | none, or the soft shadow when `glass.shadow` is on |
| `MusicGlassSurfaceBrush` | cardElevated at 90% (dark) / 92% (light) opacity |
| `MusicPlayGlyphBrush` | buttonPrimaryText |
| `MusicPlaceholderArtBrush` | accent → violet → pink |
| `TrayMenu*`, `AppIconImage`, `MusicTrackBrush` | fixed per base |
| `Share*` | fixed: the Share Screen video stage stays dark in every skin |

## Overrides

For a colour the rules above don't get right, name the resource:

```json
"overrides": {
  "AccentHoverBrush": "#60A5FA",
  "MusicPlaceholderArtBrush": "#3B82F6, #7C5CE6, #EC4899"
}
```

A brush or colour takes one hex value. A gradient takes one colour per stop, comma-separated. An
unknown name is an error, so a typo is reported instead of silently ignored. The built-in skins show
real examples.

All 63 names: `WindowBackgroundColor` `WindowSecondaryBackgroundColor` `SidebarBackgroundColor`
`TitleBarBackgroundColor` `WindowBackgroundBrush` `WindowSecondaryBackgroundBrush`
`SidebarBackgroundBrush` `TitleBarBackgroundBrush` `CardBackgroundColor`
`ElevatedCardBackgroundColor` `HoverSurfaceColor` `CardBackgroundBrush`
`ElevatedCardBackgroundBrush` `PopupBackgroundBrush` `CardHoverBrush` `HoverBackgroundBrush` `CtaButtonBackgroundBrush`
`CtaButtonForegroundBrush` `CardBorderColor` `DividerColor` `CardBorderBrush` `CardShadowEffect`
`DividerBrush` `PrimaryTextBrush` `SecondaryTextBrush` `MutedTextBrush` `DisabledTextBrush`
`PrimaryBlueBrush` `AccentBrush` `AccentHoverBrush` `AccentPressedBrush` `FocusBrush` `AccentForegroundBrush`
`SuccessBrush` `WarningBrush` `ErrorBrush` `EcoBrush` `SmartBrush` `BeastBrush`
`TrayMenuBackgroundBrush` `TrayMenuBorderBrush` `TrayMenuForegroundBrush`
`TrayMenuHoverBackgroundBrush` `TrayMenuHoverForegroundBrush` `TrayMenuSeparatorBrush`
`AppIconImage` `MusicAccentBrush` `MusicGlassSurfaceBrush` `MusicTrackBrush` `MusicPlayGlyphBrush`
`MusicPlaceholderArtBrush` `ShareStageBackgroundBrush` `ShareTileBackgroundBrush`
`ShareTileBorderBrush` `ShareControlBarBrush` `ShareControlButtonBrush`
`ShareControlButtonHoverBrush` `ShareControlForegroundBrush` `ShareControlMutedBrush`
`ShareNamePillBrush` `ShareDangerBrush` `ShareDangerHoverBrush` `ShareLiveBrush`
(`CardShadowEffect` and `AppIconImage` are not colours and can't be overridden. `AccentForegroundBrush`, the text on accent buttons, defaults to black, or white when the accent is too dark for black.)

## Asking for a skin

Any of these is enough to build a skin from:

- **A colour list**: hex codes and what each is for, even partial ("background #0B1020, accent #F472B6").
- **A screenshot or mockup**: colours are picked from the image and mapped to the fields.
- **A reference**: an app, brand, game or mood ("like Spotify", "Cyberpunk 2077 yellow", "Nord").
- **A wallpaper or photo**: a palette taken from the picture, so AxePower matches the desktop.
- **An existing theme file**: a VS Code or Windows Terminal theme, CSS variables, or Figma tokens,
  translated field by field.
- **Just an accent**: one colour on top of dark or light. The smallest skin there is.
- **A variant**: "Glass, but purple" or "Light with a warm background". Copy the built-in skin and
  change a few values.

## Checks for a new skin (for whoever builds it)

1. `dotnet test` must stay green. `SkinCompilerTests` proves the built-in skins still reproduce the
   original theme files resource for resource, and that `_template` compiles.
2. `AxePower.exe --self-test` lists the skin on the "Skins:" line, with nothing skipped.
3. Contrast: `textPrimary` and `textSecondary` must read clearly on `card` and `cardElevated`.
   Text on `accent` (the Apply button and toggles) must read too.
4. Look at it in the app: Dashboard, Settings, Music and Game Mode. A skin isn't done until someone
   has seen it on screen.
