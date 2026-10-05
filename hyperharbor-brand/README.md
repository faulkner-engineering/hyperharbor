# HyperHarbor brand assets

The mark is the **Slip**: an H built from two piers and a walkway, with a VM docked in each slip.
The top slip lights up when the host is awake; both slips light when a session is connected.

## Colors

| Token | Hex | Use |
| --- | --- | --- |
| Ink | `#121A26` | Mark and text on light backgrounds, app icon tile |
| Fog | `#F3F1EC` | Mark and text on dark backgrounds, light page ground |
| Accent | `#E39B2E` | Lit slip, primary buttons, highlights |
| Sleep | `#8A93A1` | Asleep tray state, disabled controls |
| Text muted | `#5B6470` | Captions on light backgrounds |
| Border | `#E2DED5` | Hairlines on light backgrounds |

Alternate accents that were tested with the mark: Sea `#2E8B8B`, Coral `#D8694A`, Signal `#3B82A0`.
Full tokens are in `brand.json`.

## Typography

Wordmark is **Manrope**: `Hyper` at weight 500, `Harbor` at weight 800, letter spacing -0.02em.
The SVG wordmark and lockups are converted to outlines, so no font install is needed to use them.
For UI text, load Manrope from Google Fonts (SIL Open Font License 1.1).

## Files

```
svg/
  mark.svg                    Ink mark on transparent (32 px and up)
  mark-on-dark.svg            Fog mark for dark backgrounds
  mark-mono.svg               Single color, uses currentColor (inline in UI)
  mark-small.svg              Heavier geometry, drops the bottom slip (16 to 24 px)
  mark-small-on-dark.svg
  wordmark.svg / wordmark-on-dark.svg
  lockup-horizontal.svg       Mark plus wordmark, light backgrounds
  lockup-horizontal-on-dark.svg
  app-icon.svg                Fog mark on an Ink rounded tile
  app-icon-accent.svg         Ink mark on an Accent tile (alternate)
  favicon.svg
  tray-awake.svg, tray-asleep.svg, tray-connected.svg
  tray-*-light-theme.svg      Ink strokes for a light taskbar
png/
  mark-{16..1024}.png, mark-on-dark-{16..1024}.png
  lockup-horizontal@2x.png, lockup-horizontal-on-dark@2x.png
icons/
  app-icon-{32..1024}.png     Source for Tauri: npx tauri icon brand/icons/app-icon-1024.png
  app-icon-accent-1024.png
  app.ico                     Windows executable and shortcut icon (16 to 256)
  favicon.ico, favicon.svg
  tray-awake.ico, tray-asleep.ico, tray-connected.ico   (16, 20, 24, 32, 48)
  tray-*-light-theme.ico
```

## Usage rules

- Clear space around the mark: at least the width of one pier stroke on every side.
- Below 32 px use the `mark-small` geometry or the `.ico` files, which already switch automatically.
- Do not recolor the piers; only the accent slip changes color.
- Do not stretch, rotate, or add gradients or shadows.
- On photos or busy backgrounds, place the mark on an Ink or Fog tile.

## Tray state mapping

| State | Icon |
| --- | --- |
| Host awake, no session | `tray-awake.ico` |
| Host asleep or unreachable | `tray-asleep.ico` |
| Session connected | `tray-connected.ico` |

Pick the `-light-theme` variant when the Windows taskbar is in light mode
(`SystemUsesLightTheme` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`).

## Regenerating

`build_brand.py` rebuilds every file from the geometry in `brand.json` and the Manrope font files.
Requires Python with cairosvg, fonttools, uharfbuzz, and Pillow, plus `npm install @fontsource/manrope`.
