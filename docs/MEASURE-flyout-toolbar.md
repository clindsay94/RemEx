# MEASURE: the tray flyout's toolbar, its own opacity, the Personalize Flyout section (RemEx-4kv0g.18.7)

Date: 2026-09-13 · Branch `feat/flyout-toolbar` at `db5d3c97` (this doc and the CHANGELOG are the only later changes) · PC: Release publish via `scripts/update-local-install.ps1`, `C:\Program Files\RemEx\Remex.Agent.exe`, main window parked on the second monitor · Connor's live dashboard (44 cards, 37 on presets) · Home pins: 11 sensors · Launcher: 44 entries · Theme `#579400` Vibrant Dark, contrast 0 · Card Opacity 0.1585 (set to 1 for the colour samples, restored after).

## Method

Same constraints as `docs/MEASURE-flyout-cards.md`: the popup opens only from Connor's click on the tray icon (the taskbar mod exposes no accessible tray icon), and nothing here injected a keystroke. Everything else went through UIA patterns and synthetic mouse:

- The Personalize sheet's new **FLYOUT** section was driven by `flyout-section.ps1` — `RangeValuePattern.SetValue` on the `Popup opacity` slider, `TogglePattern.Toggle` on the Cards / Buttons / Apps check boxes, `-List` to read the section back. The sheet was closed (`SettingsSheetCloseButton`) before every screenshot because an open sheet scrims the canvas.
- Popup screenshots and UIA trees by `scripts/ui-snapshot.ps1 -Screenshot -Tree -WindowTitle 'Live Glance'`; colour samples by `win-sample.ps1` on rects taken from the tree; the tooltip by a synthetic hover (`uia.ps1 -Seq hover:Pair`).
- Persisted values read straight from `%LOCALAPPDATA%\Remex\dashboard_layout.json` → `customization`. Copies of the scripts and captures: `.superpowers/sdd/2026-09-13-flyout-toolbar/eyes/`.

## Results

**Section → file → popup.** Set through the sheet: popup opacity 0.5, DRAM unticked under Cards, Sleep unticked under Buttons, steam and Code ticked under Apps. The file then carried `flyoutOpacity: 0.5`, `flyoutHiddenSensorIds: [<the DRAM sensor id>]`, `flyoutHiddenTileIds: ["sleep"]`, `flyoutAppIds: [<2 launcher guids>]`, with `glassOpacity` untouched. The open popup rebuilt itself without a restart (`CustomizationApplied`): the grid showed 10 of the 11 pins (no DRAM), and the toolbar's UIA list read, left to right,

| Button | Rect | Note |
|---|---|---|
| Lock | 1640,1370 32×32 | |
| Remote | 1680,1370 32×32 | Sleep absent between Lock and Remote |
| Send | 1720,1370 32×32 | |
| Pair | 1760,1370 32×32 | |
| Power | 1800,1370 32×32 | keeps its `MenuFlyout` (Restart / Shutdown / Sign out / Hibernate) — wiring unchanged, only the face |
| steam | 1857,1370 32×32 | after the divider; 20×20 `Image` child |
| Code | 1897,1370 32×32 | 20×20 `Image` child |

One row, 40-px pitch, the 17-px gap after Power is the divider. Every button carries its name as `AutomationProperties.Name` and shows it as a tooltip — `d2-hover-pair.png` has **Pair** over the pair glyph. The apps come in launcher order (the .18.5 review's fix), not tick order.

**Popup opacity drives the chrome only** (Acceptance 3). With Card Opacity at 1 so the card brushes are solid and the popup pinned at 944×435:

| Popup opacity | Popup chrome (empty glass beside the grid) | VTT body (Smoke & Gold) | VDDCR_SOC title ink (themed, tertiary) |
|---|---|---|---|
| 0 | `#262626` (the desktop behind shows through) | `#1C2116` | `#B4F0CC` |
| 0.5 | `#1B1E18` | `#1C2116` | `#B4F0CC` |
| 1 | `#10150A` (the theme's dark glass, solid) | `#1C2116` | `#B4F0CC` |

Chrome moves, cards do not: 3 of 3 bit-equal across the sweep, and equal to the canvas values in `docs/MEASURE-flyout-cards.md`. At the app's own 0.1585 Card Opacity the cards stay translucent over whatever the chrome is — the two sliders are independent, as the tooltip says.

**Where the section lives.** The four keys exist only in the PC's `%LOCALAPPDATA%\Remex\dashboard_layout.json`; `C:\ProgramData\RemEx\host_dashboard_layout.json` does not carry them, so a LayoutSync from the phone cannot touch them (Acceptance 4's host-sync clause holds by construction; restart is `CustomizationSettingsRoundTripTests` + `CustomizationSettingsFlyoutDefaultsTests` + the 6→7 migration test, older profiles load opacity 1.0 and empty lists).

**Geometry.** Pinned 944×435: cards `ScrollViewer` 886×273 with four columns and a scrollbar, toolbar row 886×40 under it, header above — `MinHeight` 382 = header + one card row + the toolbar budget. The transient budget (`CardsMaxHeight` 486 + `ToolbarMaxHeight` 96 + header + margins ≤ `MaxHeight` 800) was not re-measured live — the popup stayed pinned for this pass. `TrayFlyoutGeometryTests` pin the arithmetic (`MinHeightShowsOneCardRow`, the budget test, both column tests).

## Decision

**PASS** at `db5d3c97`. Acceptance 1 (icon-only row, tooltips + accessible names, hidden button absent, Power's menu wiring intact), 2 (ticked apps after a divider with icon + name tooltip, launcher order, unticked absent), 3 (chrome-only opacity, card brushes bit-equal 3/3), 4 (persisted, rebuilt live, out of the host store's reach), 5 (MinHeight 382 shows a card row, transient ≤ 800), 6 (nine resx, `-Scope dotnet` PASS, `-Check` VALID, guard extended, this doc).

Not exercised live: clicking a shortcut (it would launch Steam / VS Code on Connor's machine — `TrayFlyoutViewModelToolbarTests` cover the command) and choosing a Power action (obviously). The toolbar wraps beyond two rows into a clip (`ToolbarContentMaxHeight` 80) rather than growing the popup; with 6 + 2 buttons it never wraps at the default width.

## Eyes pass

Looked at `d2-o0.png`, `d2-o50.png`, `d2-o100.png`, `d2-cards1-o0/o50/o100.png`, `d2-hover-pair.png`:

1. The bottom row is glyphs only — the lock, monitor, up-arrow, pair and power glyphs — then a thin vertical divider, then the Steam and VS Code icons at the launcher's size; the labelled 4+2 tiles are gone and the row is 40 tall instead of the old two rows of tiles (`TilesMaxHeight` 164).
2. Hovering a glyph shows its name in the app's tooltip style; nothing else lights up.
3. At popup opacity 0 the taskbar and wallpaper show through the popup's whole body while the cards keep their own glass; at 0.5 the popup is a tint; at 1 it is solid dark glass. The cards look the same in all three.
4. DRAM is missing from the grid and Sleep from the row after unticking them; both come back on re-tick (the `-List` readback matched the file every time).
5. The FLYOUT section sits between LOOK and TEXT: a slider with the Clear / Frosted end labels, then Cards (the 11 pins), Buttons (the six), Apps (all 44 launcher entries with their icons) as wrapping check boxes.
6. Restored afterwards: popup opacity 1, all cards and buttons shown, no apps ticked, Card Opacity 0.1585, the popup closed and its stored geometry back to Connor's `pinned 706×334 @ 1845,1101` (the 334 will clamp to the new `MinHeight` 382 on the next open).
